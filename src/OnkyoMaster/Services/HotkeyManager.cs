using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace OnkyoMaster.Services;

/// <summary>
/// System-wide hotkeys via RegisterHotKey. Gestures are strings like "Ctrl+Alt+PageUp";
/// a bare "VolumeUp" captures the keyboard's media key.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 1, ModControl = 2, ModShift = 4, ModWin = 8, ModNoRepeat = 0x4000;

    private readonly HwndSource _source;
    private readonly Dictionary<int, (string Group, Action Action)> _registered = [];
    private int _nextId = 1;

    public HotkeyManager()
    {
        // A message-only window (parent HWND_MESSAGE) to receive WM_HOTKEY.
        _source = new HwndSource(new HwndSourceParameters("OnkyoMasterHotkeys")
        {
            ParentWindow = new IntPtr(-3),
            WindowStyle = 0,
        });
        _source.AddHook(WndProc);
    }

    /// <summary>
    /// Registers a hotkey. Returns false if the gesture is invalid or taken by another program.
    /// With <paramref name="repeat"/> the action fires again and again while the keys are held (volume);
    /// without it, once per press (toggles).
    /// </summary>
    public bool Register(string group, string gesture, Action action, bool repeat = false)
    {
        if (!TryParse(gesture, out var modifiers, out var key)) return false;
        if (!repeat) modifiers |= ModNoRepeat;
        var id = _nextId++;
        if (!RegisterHotKey(_source.Handle, id, modifiers, (uint)KeyInterop.VirtualKeyFromKey(key))) return false;
        _registered[id] = (group, action);
        return true;
    }

    public void UnregisterGroup(string group)
    {
        foreach (var id in _registered.Where(r => r.Value.Group == group).Select(r => r.Key).ToList())
        {
            UnregisterHotKey(_source.Handle, id);
            _registered.Remove(id);
        }
    }

    public bool HasGroup(string group) => _registered.Values.Any(r => r.Group == group);

    public void Dispose()
    {
        foreach (var id in _registered.Keys) UnregisterHotKey(_source.Handle, id);
        _registered.Clear();
        _source.Dispose();
    }

    public static string Format(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyName(key));
        return string.Join("+", parts);
    }

    /// <summary>
    /// Turns a key press in a hotkey box into a gesture string: null to ignore the key,
    /// empty to clear the hotkey.
    /// </summary>
    public static string? Capture(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        if (key == Key.Tab) return null; // let focus move on
        e.Handled = true;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return null;
        }
        if (modifiers == ModifierKeys.None && key is Key.Back or Key.Delete or Key.Escape) return "";
        // A bare letter as a system-wide hotkey would make that letter impossible to type.
        if (modifiers is ModifierKeys.None or ModifierKeys.Shift && !IsStandaloneKey(key)) return null;
        return Format(modifiers, key);
    }

    /// <summary>Keys that may be used without a modifier: they are not needed for typing.</summary>
    private static bool IsStandaloneKey(Key key) =>
        key is >= Key.F1 and <= Key.F24 or Key.VolumeUp or Key.VolumeDown or Key.VolumeMute
            or Key.MediaPlayPause or Key.MediaNextTrack or Key.MediaPreviousTrack or Key.MediaStop;

    private static bool TryParse(string gesture, out uint modifiers, out Key key)
    {
        modifiers = 0;
        key = Key.None;
        var parts = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;
        foreach (var part in parts[..^1])
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= ModControl; break;
                case "alt": modifiers |= ModAlt; break;
                case "shift": modifiers |= ModShift; break;
                case "win": modifiers |= ModWin; break;
                default: return false;
            }
        }
        key = ParseKey(parts[^1]);
        return key != Key.None;
    }

    // Several Key members share a value (PageUp/Prior), so Enum.ToString is not stable; name the common ones here.
    private static string KeyName(Key key) => key switch
    {
        Key.PageUp => "PageUp",
        Key.PageDown => "PageDown",
        Key.Return => "Enter",
        Key.Back => "Backspace",
        Key.Escape => "Esc",
        Key.Capital => "CapsLock",
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        _ => key.ToString(),
    };

    private static Key ParseKey(string name) => name.ToLowerInvariant() switch
    {
        "pageup" => Key.PageUp,
        "pagedown" => Key.PageDown,
        "enter" => Key.Return,
        "backspace" => Key.Back,
        "esc" => Key.Escape,
        "capslock" => Key.Capital,
        { Length: 1 } digit when char.IsAsciiDigit(digit[0]) => Key.D0 + (digit[0] - '0'),
        _ => Enum.TryParse<Key>(name, ignoreCase: true, out var key) ? key : Key.None,
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && _registered.TryGetValue(wParam.ToInt32(), out var entry))
        {
            entry.Action();
            handled = true;
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
