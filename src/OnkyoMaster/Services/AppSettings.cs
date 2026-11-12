using System.Collections.ObjectModel;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using OnkyoMaster.Model;

namespace OnkyoMaster.Services;

/// <summary>A user-defined ISCP command that can be given a hotkey.</summary>
public sealed class CustomCommand
{
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
}

/// <summary>A saved combination of input, volume and listening mode.</summary>
public sealed class Scene
{
    public string Name { get; set; } = "";
    public string InputId { get; set; } = "";
    public string InputName { get; set; } = "";
    public double? Volume { get; set; }
    public string ListeningMode { get; set; } = "";
    public string ListeningModeName { get; set; } = "";

    /// <summary>Where version 1.0 kept the scene's hotkey; read once so old settings files migrate.</summary>
    [JsonPropertyName("Hotkey"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyHotkey { get; set; }

    [JsonIgnore]
    public string Summary => string.Join(" · ", new[]
    {
        InputName,
        Volume is { } v ? "громкость " + VolumeScale.Format(v, halfSteps: v != Math.Floor(v)) : "",
        ListeningModeName,
    }.Where(s => s.Length > 0));
}

public sealed class AppSettings
{
    /// <summary>Version 1.0 stored five fixed hotkeys under these names instead of under their commands.</summary>
    private static readonly Dictionary<string, string> LegacyHotkeyNames = new()
    {
        ["VolumeUp"] = "vol-up",
        ["VolumeDown"] = "vol-down",
        ["Mute"] = "mute",
        ["Power"] = "power",
        ["Flyout"] = "flyout",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string ReceiverAddress { get; set; } = "";
    public string ReceiverId { get; set; } = "";
    public string ReceiverModel { get; set; } = "";

    /// <summary>
    /// Which network adapter to send through: "" leaves routing to Windows, "auto" uses the adapter
    /// that is in the receiver's subnet (and only that one), anything else is an adapter id.
    /// </summary>
    public string NetworkInterface { get; set; } = "auto";

    /// <summary>Upper limit for volume set from this app, on the receiver's absolute scale.</summary>
    public double MaxVolume { get; set; } = 70;

    /// <summary>
    /// Show volume in decibels (0 dB = absolute 82), as the receiver does with "Volume Display: Relative".
    /// Only the way levels are written out changes; they are stored and sent as absolute values.
    /// </summary>
    public bool VolumeInDecibels { get; set; }

    public double VolumeStep { get; set; } = 1;
    public bool ShowOsd { get; set; } = true;

    /// <summary>Closing the window keeps the app running in the tray; when off, closing it exits the app.</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>The keyboard's volume keys control the receiver while it is on.</summary>
    public bool MediaKeys { get; set; }

    /// <summary>Which optional parts the quick panel shows; ids from <see cref="FlyoutLayout"/>.</summary>
    public List<string> FlyoutItems { get; set; } = FlyoutLayout.Default;

    /// <summary>
    /// Global hotkeys: command → key combination. The command is anything App.Execute understands
    /// ("vol-up", "input:05", "raw:NTCPLAY"), so every action can have one.
    /// </summary>
    public Dictionary<string, string> Hotkeys { get; set; } = new()
    {
        ["vol-up"] = "Ctrl+Alt+PageUp",
        ["vol-down"] = "Ctrl+Alt+PageDown",
        ["mute"] = "Ctrl+Alt+End",
        ["flyout"] = "Ctrl+Alt+Home",
    };

    public ObservableCollection<CustomCommand> CustomCommands { get; set; } = [];

    public bool PowerOnWithPc { get; set; }
    public bool StandbyWithPc { get; set; }

    /// <summary>The input the PC is plugged into; used by the two options above.</summary>
    public string PcInputId { get; set; } = "";

    public ObservableCollection<Scene> Scenes { get; set; } = [];

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OnkyoMaster");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
                settings.Migrate();
                return settings;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged settings file should not stop the app from starting.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public string HotkeyFor(string command) => Hotkeys.GetValueOrDefault(command) ?? "";

    /// <summary>Assigns a key combination to a command; an empty one removes the hotkey.</summary>
    public void SetHotkey(string command, string gesture)
    {
        if (gesture.Length == 0) Hotkeys.Remove(command);
        else Hotkeys[command] = gesture;
    }

    private void Migrate()
    {
        foreach (var (legacyName, command) in LegacyHotkeyNames)
        {
            if (Hotkeys.Remove(legacyName, out var gesture) && !string.IsNullOrEmpty(gesture)) Hotkeys.TryAdd(command, gesture);
        }
        foreach (var scene in Scenes)
        {
            if (scene.LegacyHotkey is { Length: > 0 } gesture) Hotkeys.TryAdd("scene:" + scene.Name, gesture);
            scene.LegacyHotkey = null;
        }
        foreach (var command in Hotkeys.Where(h => string.IsNullOrEmpty(h.Value)).Select(h => h.Key).ToList())
        {
            Hotkeys.Remove(command);
        }
    }
}
