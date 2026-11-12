using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace OnkyoMaster.Services;

/// <summary>
/// Redirects volume keys that do not arrive as key presses.
/// <para>
/// A plain key press is taken by a registered hotkey before anything else sees it (see
/// <see cref="HotkeyManager"/>). The software of some keyboards and remotes posts a volume command
/// (WM_APPCOMMAND) instead of a key press. The shell announces those, so they are caught here, and
/// the change Windows makes to its own volume in response is undone.
/// </para>
/// <para>
/// Changes of the Windows volume that no command accompanies (the volume slider, another program)
/// are left alone.
/// </para>
/// </summary>
public sealed class VolumeCommandCapture : IDisposable
{
    private const int HshellAppCommand = 12;
    private const int AppCommandVolumeMute = 8, AppCommandVolumeDown = 9, AppCommandVolumeUp = 10;

    /// <summary>A volume change this soon after a command (or before it) is that command's effect.</summary>
    private const int CommandWindowMs = 300, ChangeBeforeCommandMs = 150;

    /// <summary>Same pace as held hotkeys: commands repeat about 30 times a second while a key is down.</summary>
    private const int StepIntervalMs = 120, MuteIntervalMs = 400;

    /// <summary>Marks the changes made here, so that their notifications are not taken for someone else's.</summary>
    private static readonly Guid OwnChange = new("7b1d2c0e-5a43-4c8e-9f10-3d6e8a4b9c21");

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly HwndSource _window;
    private readonly int _shellMessage;
    private readonly Callbacks _callbacks;
    private IMMDeviceEnumerator? _enumerator;
    private IAudioEndpointVolume? _endpoint;
    private bool _active;

    // The Windows volume as it was before a redirected command made Windows change it.
    private float _level;
    private bool _muted;
    private float _previousLevel;
    private bool _previousMuted;
    private long _changedAt = long.MinValue / 2;
    private long _commandAt = long.MinValue / 2;
    private long _stepAt = long.MinValue / 2;
    private long _muteAt = long.MinValue / 2;

    public VolumeCommandCapture()
    {
        // Shell notifications only go to top-level windows, so this one is a real window that is never shown.
        _window = new HwndSource(new HwndSourceParameters("OnkyoMasterVolumeCommands")
        {
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP
            ExtendedWindowStyle = 0x80,               // WS_EX_TOOLWINDOW: no taskbar button
            Width = 0,
            Height = 0,
        });
        _window.AddHook(WndProc);
        _shellMessage = RegisterWindowMessage("SHELLHOOK");
        _callbacks = new Callbacks(this);
    }

    /// <summary>The user asked for one volume step up (+1) or down (-1).</summary>
    public event Action<int>? Step;

    public event Action? MuteToggled;

    public void Start()
    {
        if (_active) return;
        _active = true;
        RegisterShellHookWindow(_window.Handle);
        try
        {
            _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            _enumerator.RegisterEndpointNotificationCallback(_callbacks);
            Attach();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // No audio service: commands are still redirected, their effect on Windows is just not undone.
        }
    }

    public void Stop()
    {
        if (!_active) return;
        _active = false;
        DeregisterShellHookWindow(_window.Handle);
        Detach();
        _enumerator?.UnregisterEndpointNotificationCallback(_callbacks);
        _enumerator = null;
    }

    public void Dispose()
    {
        Stop();
        _window.Dispose();
    }

    /// <summary>Follows the default playback device: that is the one volume keys act on.</summary>
    private void Attach()
    {
        Detach();
        if (!_active || _enumerator is null) return;
        if (_enumerator.GetDefaultAudioEndpoint(DataFlowRender, RoleMultimedia, out var device) != 0 || device is null) return;
        var iid = typeof(IAudioEndpointVolume).GUID;
        if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var instance) != 0 || instance is not IAudioEndpointVolume endpoint) return;

        endpoint.GetMasterVolumeLevelScalar(out _level);
        endpoint.GetMute(out _muted);
        endpoint.RegisterControlChangeNotify(_callbacks);
        _endpoint = endpoint;
    }

    private void Detach()
    {
        _endpoint?.UnregisterControlChangeNotify(_callbacks);
        _endpoint = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != _shellMessage || wParam.ToInt64() != HshellAppCommand || !_active) return IntPtr.Zero;

        // The command is in the high word of lParam, with the device that sent it in the top four bits.
        var command = (int)((lParam.ToInt64() >> 16) & 0x0FFF);
        if (command is not (AppCommandVolumeUp or AppCommandVolumeDown or AppCommandVolumeMute)) return IntPtr.Zero;

        OnCommand(command);
        handled = true;
        return new IntPtr(1);
    }

    private void OnCommand(int command)
    {
        var now = Environment.TickCount64;
        if (now - _changedAt <= ChangeBeforeCommandMs)
        {
            // Windows had already reacted by the time the command was announced: take that back.
            _level = _previousLevel;
            _muted = _previousMuted;
            _changedAt = long.MinValue / 2;
            Restore();
        }
        _commandAt = now;

        if (command == AppCommandVolumeMute) EmitMute(now);
        else EmitStep(command == AppCommandVolumeUp ? 1 : -1, now);
    }

    private void OnEndpointChanged(float level, bool muted)
    {
        if (!_active || _endpoint is null) return;
        var levelChanged = Math.Abs(level - _level) > 0.0005f;
        if (!levelChanged && muted == _muted) return;

        var now = Environment.TickCount64;
        if (now - _commandAt <= CommandWindowMs)
        {
            // Windows reacting to a command that has already gone to the receiver.
            Restore();
            return;
        }

        // An ordinary change. Unless the announcement of a command is still on its way: keep what to go back to.
        _previousLevel = _level;
        _previousMuted = _muted;
        _changedAt = now;
        _level = level;
        _muted = muted;
    }

    private void Restore()
    {
        if (_endpoint is null) return;
        var context = OwnChange;
        _endpoint.SetMasterVolumeLevelScalar(_level, ref context);
        _endpoint.SetMute(_muted, ref context);
    }

    private void EmitStep(int direction, long now)
    {
        if (now - _stepAt < StepIntervalMs) return;
        _stepAt = now;
        Step?.Invoke(direction);
    }

    private void EmitMute(long now)
    {
        if (now - _muteAt < MuteIntervalMs) return;
        _muteAt = now;
        MuteToggled?.Invoke();
    }

    /// <summary>Receives the audio system's notifications, which arrive on its own threads.</summary>
    private sealed class Callbacks(VolumeCommandCapture owner) : IAudioEndpointVolumeCallback, IMMNotificationClient
    {
        public int OnNotify(IntPtr data)
        {
            // AUDIO_VOLUME_NOTIFICATION_DATA: GUID context, BOOL muted, float masterVolume, ...
            var context = Marshal.PtrToStructure<Guid>(data);
            if (context == OwnChange) return 0;
            var muted = Marshal.ReadInt32(data, 16) != 0;
            var level = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(data, 20));
            owner._dispatcher.BeginInvoke(() => owner.OnEndpointChanged(level, muted));
            return 0;
        }

        public int OnDefaultDeviceChanged(int dataFlow, int role, string? id)
        {
            if (dataFlow == DataFlowRender && role == RoleMultimedia) owner._dispatcher.BeginInvoke(() => owner.Attach());
            return 0;
        }

        public int OnDeviceStateChanged(string id, int state) => 0;
        public int OnDeviceAdded(string id) => 0;
        public int OnDeviceRemoved(string id) => 0;
        public int OnPropertyValueChanged(string id, PropertyKey key) => 0;
    }

    // ---- Windows interop --------------------------------------------------------------------

    private const int DataFlowRender = 0, RoleMultimedia = 1, ClsCtxAll = 23;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    private static extern bool RegisterShellHookWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool DeregisterShellHookWindow(IntPtr hwnd);

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object? instance);
    }

    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, int state);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDefaultDeviceChanged(int dataFlow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? id);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PropertyKey key);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public int PropertyId;
    }

    [ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolumeCallback
    {
        [PreserveSig] int OnNotify(IntPtr data);
    }

    // Only the methods up to the last one used are declared; the order is the interface's own.
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback callback);
        [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback callback);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float decibels, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float decibels);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float decibels, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float decibels);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
