using OnkyoMaster.Model;

namespace OnkyoMaster.Services;

/// <summary>
/// The optional parts of the quick panel, in the order they appear on it.
/// Power, volume and mute are always there and are not listed.
/// </summary>
public static class FlyoutLayout
{
    public const string Inputs = "inputs";
    public const string Signal = "signal";
    public const string Mode = "mode";
    public const string LateNight = "late-night";
    public const string Center = "center";
    public const string Vocal = "vocal";
    public const string Subwoofer = "subwoofer";
    public const string NowPlaying = "now-playing";
    public const string Scenes = "scenes";
    public const string Zones = "zones";

    public static IReadOnlyList<Option> All { get; } =
    [
        new(Inputs, "Входы"),
        new(Signal, "Сигнал: вход и выход"),
        new(Mode, "Режим звучания"),
        new(LateNight, "Ночной режим"),
        new(Center, "Центральный канал"),
        new(Vocal, "Голос"),
        new(Subwoofer, "Сабвуфер"),
        new(NowPlaying, "Что играет"),
        new(Scenes, "Сцены"),
        new(Zones, "Зоны 2 и 3"),
    ];

    public static List<string> Default => [Signal, Mode, LateNight, Center, Vocal, NowPlaying, Scenes];

    /// <summary>
    /// The parts the first version with a configurable panel had. Settings saved by it do not say which
    /// parts they knew about, and that is needed to switch on a part added later (see AppSettings.Migrate).
    /// </summary>
    public static List<string> FirstVersion => [Inputs, Mode, LateNight, Center, Vocal, Subwoofer, NowPlaying, Scenes, Zones];
}
