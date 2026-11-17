using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Valafind.App.Localization;

namespace Valafind.App.Platform;

/// <summary>
/// Global hotkey via Carbon <c>RegisterEventHotKey</c>. Carbon delivers hot-key events through the application
/// event target on the main run loop, which Avalonia's macOS backend drives; every Carbon call is therefore made
/// on the main thread (see <see cref="MacMainThread"/>). No Accessibility permission is required.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe class MacHotkeyService : IGlobalHotkeyService
{
    private static int s_nextId;

    private readonly object _gate = new();
    private GCHandle _self;
    private nint _handlerRef;
    private nint _hotKeyRef;
    private HotkeyChord? _activeChord;
    private volatile uint _activeId;
    private volatile Action? _callback;
    private volatile string? _activeChordText;
    private bool _disposed;

    public string? ActiveChord => _activeChordText;

    public bool TryRegister(string chord, Action onPressed, out string? error)
    {
        ArgumentNullException.ThrowIfNull(onPressed);
        if (!HotkeyChord.TryParse(chord, out var parsed, out error))
            return false;

        if (HotkeyChord.MacRestriction(parsed) is { } restriction)
        {
            error = restriction;
            return false;
        }

        if (HotkeyKeyMaps.MacVirtualKey(parsed.Key) is not { } keyCode)
        {
            error = Loc.Format(Strings.HotkeyKeyNotUsableMac, parsed.Key);
            return false;
        }

        uint modifiers = 0;
        if (parsed.Ctrl) modifiers |= Carbon.controlKey;
        if (parsed.Alt) modifiers |= Carbon.optionKey;
        if (parsed.Shift) modifiers |= Carbon.shiftKey;
        if (parsed.Super) modifiers |= Carbon.cmdKey;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try
            {
                error = MacMainThread.Run(() => RegisterOnMainThread(parsed, keyCode, modifiers));
            }
            catch (Exception ex)
            {
                error = $"Could not register {parsed.DisplayString()}: {(ex is AggregateException { InnerException: { } inner } ? inner.Message : ex.Message)}";
            }

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
            if (_disposed)
                return;
            _callback = null;
            _activeChordText = null;
            RunQuietly(() => UnregisterOnMainThread());
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _callback = null;
            _activeChordText = null;
            RunQuietly(() =>
            {
                UnregisterOnMainThread();
                if (_handlerRef != 0)
                {
                    _ = Carbon.RemoveEventHandler(_handlerRef);
                    _handlerRef = 0;
                }
            });

            // Only free the handle once the handler is gone; otherwise a late event would dereference it.
            if (_handlerRef == 0 && _self.IsAllocated)
                _self.Free();
        }
    }

    private string? RegisterOnMainThread(HotkeyChord chord, uint keyCode, uint modifiers)
    {
        if (_activeChord == chord && _hotKeyRef != 0)
            return null;

        if (_handlerRef == 0)
        {
            if (!_self.IsAllocated)
                _self = GCHandle.Alloc(this);

            var spec = new Carbon.EventTypeSpec { EventClass = Carbon.kEventClassKeyboard, EventKind = Carbon.kEventHotKeyPressed };
            var status = Carbon.InstallEventHandler(
                Carbon.GetApplicationEventTarget(), &OnHotKeyEvent, 1, &spec, GCHandle.ToIntPtr(_self), out _handlerRef);
            if (status != Carbon.noErr)
            {
                _handlerRef = 0;
                return $"Could not install the hotkey handler (OSStatus {status}).";
            }
        }

        var id = (uint)Interlocked.Increment(ref s_nextId);
        var hotKeyId = new Carbon.EventHotKeyID { Signature = Carbon.Signature, Id = id };
        var result = Carbon.RegisterEventHotKey(keyCode, modifiers, hotKeyId, Carbon.GetApplicationEventTarget(), 0, out var hotKeyRef);
        if (result != Carbon.noErr)
        {
            var spotlightHint = chord is { Super: true, Ctrl: false, Alt: false, Key: "Space" }
                ? " " + Strings.HotkeySpotlightHint
                : string.Empty;
            return result == Carbon.eventHotKeyExistsErr
                ? HotkeyDispatch.InUseMessage(chord) + spotlightHint
                : $"Could not register {chord.DisplayString()} (OSStatus {result}).{spotlightHint}";
        }

        // The new key is live; drop the previous one.
        UnregisterOnMainThread();
        _hotKeyRef = hotKeyRef;
        _activeId = id;
        _activeChord = chord;
        return null;
    }

    private void UnregisterOnMainThread()
    {
        if (_hotKeyRef != 0)
            _ = Carbon.UnregisterEventHotKey(_hotKeyRef);
        _hotKeyRef = 0;
        _activeId = 0;
        _activeChord = null;
    }

    private static void RunQuietly(Action action)
    {
        try
        {
            MacMainThread.Run(() =>
            {
                action();
                return 0;
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"Valafind: could not release the macOS hotkey: {ex.Message}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnHotKeyEvent(nint callRef, nint eventRef, nint userData)
    {
        try
        {
            Carbon.EventHotKeyID hotKeyId;
            var status = Carbon.GetEventParameter(
                eventRef, Carbon.kEventParamDirectObject, Carbon.typeEventHotKeyID,
                null, (nuint)sizeof(Carbon.EventHotKeyID), null, &hotKeyId);
            if (status != Carbon.noErr || hotKeyId.Signature != Carbon.Signature)
                return Carbon.eventNotHandledErr;

            if (GCHandle.FromIntPtr(userData).Target is not MacHotkeyService service || service._activeId != hotKeyId.Id)
                return Carbon.eventNotHandledErr;

            HotkeyDispatch.Raise(service._callback);
            return Carbon.noErr;
        }
        catch
        {
            return Carbon.eventNotHandledErr;
        }
    }
}
