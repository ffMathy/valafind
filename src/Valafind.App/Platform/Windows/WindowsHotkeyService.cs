using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Valafind.App.Localization;

namespace Valafind.App.Platform;

/// <summary>
/// Global hotkey via <c>RegisterHotKey</c>. A dedicated background STA thread owns a message-only window
/// (<c>HWND_MESSAGE</c>) and runs its own <c>GetMessage</c> loop; all RegisterHotKey/UnregisterHotKey calls are
/// executed on that thread because hotkeys are bound to the registering thread's queue.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class WindowsHotkeyService : IGlobalHotkeyService
{
    private const uint WM_APP_INVOKE = Win32.WM_APP + 1;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly ConcurrentQueue<Action> _work = new();
    private readonly ManualResetEventSlim _ready = new(false);

    private Thread? _thread;
    private nint _hwnd;
    private string? _startError;
    private bool _disposed;

    // Owned by the message thread.
    private int _activeId;
    private int _lastId;
    private HotkeyChord? _activeChord;

    private volatile Action? _callback;
    private volatile string? _activeChordText;

    public string? ActiveChord => _activeChordText;

    public bool TryRegister(string chord, Action onPressed, out string? error)
    {
        ArgumentNullException.ThrowIfNull(onPressed);
        if (!HotkeyChord.TryParse(chord, out var parsed, out error))
            return false;

        if (HotkeyKeyMaps.WindowsVirtualKey(parsed.Key) is not { } vk)
        {
            error = Loc.Format(Strings.HotkeyKeyNotUsableWindows, parsed.Key);
            return false;
        }

        uint modifiers = Win32.MOD_NOREPEAT;
        if (parsed.Ctrl) modifiers |= Win32.MOD_CONTROL;
        if (parsed.Alt) modifiers |= Win32.MOD_ALT;
        if (parsed.Shift) modifiers |= Win32.MOD_SHIFT;
        if (parsed.Super) modifiers |= Win32.MOD_WIN;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!EnsureThread(out error))
                return false;

            var result = Invoke(() =>
            {
                if (_activeChord == parsed)
                    return (string?)null;

                var id = NextId();
                if (!Win32.RegisterHotKey(_hwnd, id, modifiers, vk))
                {
                    var code = Marshal.GetLastPInvokeError();
                    return code == Win32.ERROR_HOTKEY_ALREADY_REGISTERED
                        ? HotkeyDispatch.InUseMessage(parsed)
                        : Loc.Format(Strings.HotkeyCouldNotRegisterReason, parsed.DisplayString(), new Win32Exception(code).Message);
                }

                if (_activeId != 0)
                    Win32.UnregisterHotKey(_hwnd, _activeId);
                _activeId = id;
                _activeChord = parsed;
                return null;
            }, out var invokeError);

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

            Invoke(() =>
            {
                UnregisterActive();
                return (string?)null;
            }, out _);
            _callback = null;
            _activeChordText = null;
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
            thread = _thread;
            _callback = null;
            _activeChordText = null;
            if (thread is not null && _startError is null)
            {
                _work.Enqueue(() => Win32.PostQuitMessage(0));
                Win32.PostMessageW(_hwnd, WM_APP_INVOKE, 0, 0);
            }
        }

        if (thread is not null && thread.IsAlive && !thread.Join(OperationTimeout))
            System.Diagnostics.Trace.TraceWarning("Valafind: hotkey message thread did not exit in time.");
    }

    private int NextId()
    {
        // Application hotkey ids must be in 0x0000..0xBFFF.
        _lastId = _lastId >= 0xBFFF ? 1 : _lastId + 1;
        return _lastId;
    }

    private void UnregisterActive()
    {
        if (_activeId != 0)
            Win32.UnregisterHotKey(_hwnd, _activeId);
        _activeId = 0;
        _activeChord = null;
    }

    private bool EnsureThread(out string? error)
    {
        error = null;
        if (_thread is null)
        {
            var thread = new Thread(MessageLoop)
            {
                IsBackground = true,
                Name = "Valafind global hotkey",
            };
            thread.SetApartmentState(ApartmentState.STA);
            _thread = thread;
            thread.Start();
            if (!_ready.Wait(OperationTimeout))
                _startError ??= "The hotkey message window did not start in time.";
        }

        error = _startError;
        return error is null;
    }

    /// <summary>Runs <paramref name="func"/> on the message thread and waits for its result.</summary>
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

        if (!Win32.PostMessageW(_hwnd, WM_APP_INVOKE, 0, 0))
        {
            invokeError = $"Could not reach the hotkey message window: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}";
            return null;
        }

        try
        {
            if (!tcs.Task.Wait(OperationTimeout))
            {
                invokeError = "Timed out talking to the hotkey message window.";
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

    private void MessageLoop()
    {
        var instance = Win32.GetModuleHandleW(0);
        var className = Marshal.StringToHGlobalUni("ValafindHotkeyWindow_" + Guid.NewGuid().ToString("N"));
        var classRegistered = false;
        try
        {
            var wc = new Win32.WNDCLASSEXW
            {
                cbSize = (uint)sizeof(Win32.WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = instance,
                lpszClassName = (char*)className,
            };

            if (Win32.RegisterClassExW(in wc) == 0)
            {
                _startError = $"Could not create the hotkey window class: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}";
                return;
            }

            classRegistered = true;
            _hwnd = Win32.CreateWindowExW(0, (char*)className, null, 0, 0, 0, 0, 0, Win32.HWND_MESSAGE, 0, instance, 0);
            if (_hwnd == 0)
            {
                _startError = $"Could not create the hotkey window: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}";
                return;
            }

            _ready.Set();

            while (true)
            {
                var result = Win32.GetMessageW(out var msg, 0, 0, 0);
                if (result == 0)
                    break; // WM_QUIT
                if (result == -1)
                {
                    System.Diagnostics.Trace.TraceError(
                        $"Valafind: GetMessage failed: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
                    break;
                }

                if (msg.hwnd == _hwnd && msg.message == WM_APP_INVOKE)
                {
                    DrainWork();
                    continue;
                }

                if (msg.hwnd == _hwnd && msg.message == Win32.WM_HOTKEY)
                {
                    if (_activeId != 0 && (int)msg.wParam == _activeId)
                        HotkeyDispatch.Raise(_callback);
                    continue;
                }

                Win32.TranslateMessage(in msg);
                Win32.DispatchMessageW(in msg);
            }
        }
        catch (Exception ex)
        {
            _startError ??= $"The hotkey message thread failed: {ex.Message}";
            System.Diagnostics.Trace.TraceError($"Valafind: hotkey message thread failed: {ex}");
        }
        finally
        {
            if (_hwnd != 0)
            {
                UnregisterActive();
                Win32.DestroyWindow(_hwnd);
            }

            if (classRegistered)
                Win32.UnregisterClassW((char*)className, instance);
            Marshal.FreeHGlobal(className);

            // Callers blocked in Invoke get answered rather than timing out.
            _startError ??= "The hotkey message thread has stopped.";
            DrainWork();
            _ready.Set();
        }
    }

    private void DrainWork()
    {
        while (_work.TryDequeue(out var item))
            item();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WndProc(nint hwnd, uint msg, nuint wParam, nint lParam) =>
        Win32.DefWindowProcW(hwnd, msg, wParam, lParam);
}
