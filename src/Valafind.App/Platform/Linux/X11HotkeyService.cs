using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Valafind.App.Platform;

/// <summary>
/// Global hotkey on X11 via passive <c>XGrabKey</c> on the root window. A dedicated thread owns a private
/// Display connection (so it never contends with Avalonia's), grabs the key for every Caps Lock / Num Lock
/// combination, and waits on the X socket plus a wake-up pipe with <c>poll</c> so it idles without busy-polling
/// and can be stopped promptly.
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("freebsd")]
internal sealed unsafe class X11HotkeyService : IGlobalHotkeyService
{
    private static readonly uint[] LockVariants =
        [0, Xlib.LockMask, Xlib.Mod2Mask, Xlib.LockMask | Xlib.Mod2Mask];

    private const uint RelevantModifiers = Xlib.ShiftMask | Xlib.ControlMask | Xlib.Mod1Mask | Xlib.Mod4Mask;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(5);

    // Xlib's error handler is process-wide. Ours records errors for our own displays and forwards everything
    // else to whichever handler was installed before (normally Avalonia's).
    private static readonly ConcurrentDictionary<nint, X11HotkeyService> s_byDisplay = new();
    private static readonly object s_xlibInitGate = new();
    private static bool s_xlibInitialized;
    private static nint s_previousErrorHandler;

    private readonly object _gate = new();
    private readonly ConcurrentQueue<Action> _work = new();
    private readonly ManualResetEventSlim _ready = new(false);

    private Thread? _thread;
    private volatile bool _stop;
    private volatile string? _startError;
    private int _wakeRead = -1;
    private int _wakeWrite = -1;
    private bool _disposed;

    // Owned by the X thread.
    private nint _display;
    private nuint _root;
    private int _keycode;
    private uint _modifiers;
    private HotkeyChord? _activeChord;
    private bool _pressed;
    private byte _lastErrorCode;

    private volatile Action? _callback;
    private volatile string? _activeChordText;

    public string? ActiveChord => _activeChordText;

    public bool TryRegister(string chord, Action onPressed, out string? error)
    {
        ArgumentNullException.ThrowIfNull(onPressed);
        if (!HotkeyChord.TryParse(chord, out var parsed, out error))
            return false;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!EnsureThread(out error))
                return false;

            var result = Invoke(() => GrabOnThread(parsed), out var invokeError);
            error = invokeError ?? result;
            if (error is not null)
                return false;

            _callback = onPressed;
            _activeChordText = parsed.ToString();
            return true;
        }
    }

    public void Unregister()
    {
        lock (_gate)
        {
            if (_disposed || _thread is null || _startError is not null)
                return;
            _callback = null;
            _activeChordText = null;
            Invoke(() =>
            {
                UngrabActive();
                return null;
            }, out _);
        }
    }

    public void Dispose()
    {
        Thread? thread;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _callback = null;
            _activeChordText = null;
            thread = _thread;
            _stop = true;
            Wake();
        }

        if (thread is not null && thread.IsAlive && !thread.Join(OperationTimeout))
            System.Diagnostics.Trace.TraceWarning("Valafind: X11 hotkey thread did not exit in time.");
    }

    private bool EnsureThread(out string? error)
    {
        if (_thread is null)
        {
            var thread = new Thread(EventLoop) { IsBackground = true, Name = "Valafind X11 hotkey" };
            _thread = thread;
            thread.Start();
            if (!_ready.Wait(OperationTimeout))
                _startError ??= "The X11 hotkey thread did not start in time.";
        }

        error = _startError;
        return error is null;
    }

    private string? Invoke(Func<string?> func, out string? invokeError)
    {
        invokeError = null;
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Enqueue(() =>
        {
            try
            {
                tcs.TrySetResult(func());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        Wake();

        try
        {
            if (!tcs.Task.Wait(OperationTimeout))
            {
                invokeError = "Timed out talking to the X server.";
                return null;
            }

            return tcs.Task.Result;
        }
        catch (AggregateException ex)
        {
            invokeError = ex.InnerException?.Message ?? ex.Message;
            return null;
        }
    }

    private void Wake()
    {
        var fd = _wakeWrite;
        if (fd < 0)
            return;
        byte b = 1;
        LibC.Write(fd, &b, 1); // A full pipe already guarantees a wake-up; the result can be ignored.
    }

    private void EventLoop()
    {
        try
        {
            lock (s_xlibInitGate)
            {
                if (!s_xlibInitialized)
                {
                    _ = Xlib.XInitThreads();
                    s_xlibInitialized = true;
                }
            }

            _display = Xlib.XOpenDisplay(0);
            if (_display == 0)
            {
                _startError = $"Could not connect to the X server (DISPLAY={Environment.GetEnvironmentVariable("DISPLAY")}).";
                return;
            }

            s_byDisplay[_display] = this;
            _root = Xlib.XDefaultRootWindow(_display);

            // Repeats then arrive as Press, Press, ..., Release so holding the chord fires once.
            int supported;
            _ = Xlib.XkbSetDetectableAutoRepeat(_display, 1, &supported);

            var fds = stackalloc int[2];
            if (LibC.Pipe(fds) != 0)
            {
                _startError = $"Could not create the X11 hotkey wake-up pipe (errno {Marshal.GetLastPInvokeError()}).";
                return;
            }

            _wakeRead = fds[0];
            _wakeWrite = fds[1];
            _ready.Set();

            var pollFds = stackalloc LibC.PollFd[2];
            pollFds[0] = new LibC.PollFd { fd = Xlib.XConnectionNumber(_display), events = LibC.POLLIN };
            pollFds[1] = new LibC.PollFd { fd = _wakeRead, events = LibC.POLLIN };
            var drain = stackalloc byte[64];
            Xlib.XEvent ev;

            while (!_stop)
            {
                DrainWork();

                // XPending flushes our output buffer and reads whatever the server has sent.
                while (!_stop && Xlib.XPending(_display) > 0)
                {
                    _ = Xlib.XNextEvent(_display, &ev);
                    HandleEvent((Xlib.XKeyEvent*)&ev);
                }

                if (_stop || !_work.IsEmpty)
                    continue;

                pollFds[0].revents = 0;
                pollFds[1].revents = 0;
                if (LibC.Poll(pollFds, 2, -1) < 0)
                {
                    var errno = Marshal.GetLastPInvokeError();
                    if (errno == LibC.EINTR)
                        continue;
                    System.Diagnostics.Trace.TraceError($"Valafind: poll on the X connection failed (errno {errno}).");
                    break;
                }

                if ((pollFds[1].revents & LibC.POLLIN) != 0)
                    LibC.Read(_wakeRead, drain, 64);
            }
        }
        catch (Exception ex)
        {
            _startError ??= $"The X11 hotkey thread failed: {ex.Message}";
            System.Diagnostics.Trace.TraceError($"Valafind: X11 hotkey thread failed: {ex}");
        }
        finally
        {
            if (_display != 0)
            {
                try
                {
                    UngrabActive();
                }
                finally
                {
                    s_byDisplay.TryRemove(_display, out _);
                    _ = Xlib.XCloseDisplay(_display);
                    _display = 0;
                }
            }

            var (r, w) = (_wakeRead, _wakeWrite);
            _wakeWrite = -1;
            _wakeRead = -1;
            if (w >= 0) LibC.Close(w);
            if (r >= 0) LibC.Close(r);

            _startError ??= "The X11 hotkey thread has stopped.";
            DrainWork();
            _ready.Set();
        }
    }

    private void HandleEvent(Xlib.XKeyEvent* key)
    {
        if (_activeChord is null || (int)key->keycode != _keycode)
            return;

        if (key->type == Xlib.KeyRelease)
        {
            _pressed = false;
        }
        else if (key->type == Xlib.KeyPress && (key->state & RelevantModifiers) == _modifiers && !_pressed)
        {
            _pressed = true;
            HotkeyDispatch.Raise(_callback);
        }
    }

    private void DrainWork()
    {
        while (_work.TryDequeue(out var item))
            item();
    }

    private string? GrabOnThread(HotkeyChord chord)
    {
        if (_display == 0)
            return "Not connected to the X server.";
        if (_activeChord == chord)
            return null;

        var keysymName = HotkeyKeyMaps.XKeysymName(chord.Key);
        var keysym = keysymName is null ? 0 : Xlib.XStringToKeysym(keysymName);
        if (keysym == 0)
            return $"The key '{chord.Key}' cannot be used as a global shortcut on X11.";

        int keycode = Xlib.XKeysymToKeycode(_display, keysym);
        if (keycode == 0)
            return $"The key '{chord.Key}' is not present on the current keyboard layout.";

        uint modifiers = 0;
        if (chord.Ctrl) modifiers |= Xlib.ControlMask;
        if (chord.Alt) modifiers |= Xlib.Mod1Mask;
        if (chord.Shift) modifiers |= Xlib.ShiftMask;
        if (chord.Super) modifiers |= Xlib.Mod4Mask;

        if (_activeChord is not null && keycode == _keycode && modifiers == _modifiers)
        {
            _activeChord = chord; // Same physical grab (e.g. alias); nothing to change.
            return null;
        }

        InstallErrorHandler();
        _lastErrorCode = 0;
        foreach (var variant in LockVariants)
            _ = Xlib.XGrabKey(_display, keycode, modifiers | variant, _root, 0, Xlib.GrabModeAsync, Xlib.GrabModeAsync);
        _ = Xlib.XSync(_display, 0);

        if (_lastErrorCode != 0)
        {
            var code = _lastErrorCode;
            foreach (var variant in LockVariants)
                _ = Xlib.XUngrabKey(_display, keycode, modifiers | variant, _root);
            _ = Xlib.XSync(_display, 0);
            _lastErrorCode = 0;
            return code == Xlib.BadAccess
                ? HotkeyDispatch.InUseMessage(chord)
                : $"Could not register {chord.DisplayString()} (X error {code}).";
        }

        UngrabActive();
        _keycode = keycode;
        _modifiers = modifiers;
        _activeChord = chord;
        _pressed = false;
        return null;
    }

    private void UngrabActive()
    {
        if (_activeChord is null || _display == 0)
            return;
        foreach (var variant in LockVariants)
            _ = Xlib.XUngrabKey(_display, _keycode, _modifiers | variant, _root);
        _ = Xlib.XSync(_display, 0);
        _activeChord = null;
        _keycode = 0;
        _modifiers = 0;
        _pressed = false;
    }

    private static void InstallErrorHandler()
    {
        lock (s_xlibInitGate)
        {
            delegate* unmanaged[Cdecl]<nint, Xlib.XErrorEvent*, int> ours = &OnXError;
            var previous = Xlib.XSetErrorHandler(ours);
            if (previous != (nint)ours)
                s_previousErrorHandler = previous;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnXError(nint display, Xlib.XErrorEvent* error)
    {
        if (s_byDisplay.TryGetValue(display, out var service))
        {
            if (service._lastErrorCode == 0)
                service._lastErrorCode = error->error_code;
            return 0;
        }

        var previous = s_previousErrorHandler;
        if (previous != 0)
            return ((delegate* unmanaged[Cdecl]<nint, Xlib.XErrorEvent*, int>)previous)(display, error);
        return 0;
    }
}
