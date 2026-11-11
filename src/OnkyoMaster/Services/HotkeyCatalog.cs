using OnkyoMaster.Model;

namespace OnkyoMaster.Services;

/// <summary>One thing a hotkey can do: a title for the list and the command App.Execute runs.</summary>
public sealed record HotkeyAction(string Title, string Command);

public sealed record HotkeyActionGroup(string Title, IReadOnlyList<HotkeyAction> Actions);

/// <summary>
/// Everything that can be put on a hotkey, grouped for display. Inputs, zones, network services and
/// presets come from the receiver's description, so the list matches the connected model.
/// </summary>
public static class HotkeyCatalog
{
    public const string CustomGroup = "Свои команды";
    public const string UnknownGroup = "Недоступные сейчас";

    /// <summary>Commands that should keep firing while the keys are held down.</summary>
    private static readonly HashSet<string> Repeating = new(StringComparer.Ordinal)
    {
        "vol-up", "vol-down", "z2-vol-up", "z2-vol-down", "z3-vol-up", "z3-vol-down",
        "bass-up", "bass-down", "treble-up", "treble-down", "center-up", "center-down", "sub-up", "sub-down",
        "raw:TUNUP", "raw:TUNDOWN", "raw:NTCUP", "raw:NTCDOWN",
        "raw:OSDUP", "raw:OSDDOWN", "raw:OSDLEFT", "raw:OSDRIGHT",
        "raw:CTVVLUP", "raw:CTVVLDN", "raw:CTVCHUP", "raw:CTVCHDN",
    };

    public static bool Repeats(string command) => Repeating.Contains(command);

    public static List<HotkeyActionGroup> Build(Receiver receiver, AppSettings settings)
    {
        var groups = new List<HotkeyActionGroup>
        {
            new("Приложение",
            [
                new("Открыть быструю панель", "flyout"),
                new("Открыть панель управления", "show"),
            ]),
            Zone("Основная зона", "", receiver.Main),
            new("Режим звучания",
            [
                .. receiver.ModeButtons.Select(b => new HotkeyAction($"Кнопка {b.Label} — следующий режим группы", "mode:" + b.Code)),
                new("Следующий режим", "raw:LMDUP"),
                new("Предыдущий режим", "raw:LMDDOWN"),
                .. ListeningModes.Selectable.Select(m => new HotkeyAction(m.Label, "mode:" + m.Code)),
            ]),
            new("Звук",
            [
                new("Низкие частоты +", "bass-up"),
                new("Низкие частоты −", "bass-down"),
                new("Высокие частоты +", "treble-up"),
                new("Высокие частоты −", "treble-down"),
                new("Центральный канал +", "center-up"),
                new("Центральный канал −", "center-down"),
                new("Сабвуфер +", "sub-up"),
                new("Сабвуфер −", "sub-down"),
                new("Music Optimizer: включить / выключить", "music-optimizer"),
                new("Cinema Filter: включить / выключить", "cinema-filter"),
                .. Choice("Late Night", "late-night", receiver.LateNight),
            ]),
            new("Дисплей, таймер сна, HDMI",
            [
                .. Choice("Яркость дисплея", "dimmer", receiver.Dimmer),
                .. Choice("Таймер сна", "sleep", receiver.Sleep),
                .. Choice("Выход HDMI", "hdmi-out", receiver.HdmiOut),
            ]),
        };

        if (receiver.Zone2.IsAvailable) groups.Add(Zone("Zone 2", "z2-", receiver.Zone2));
        if (receiver.Zone3.IsAvailable) groups.Add(Zone("Zone 3", "z3-", receiver.Zone3));

        groups.Add(new("Тюнер",
        [
            new("Частота +", "raw:TUNUP"),
            new("Частота −", "raw:TUNDOWN"),
            new("Следующий пресет", "raw:PRSUP"),
            new("Предыдущий пресет", "raw:PRSDOWN"),
            .. receiver.Presets.Select(p => new HotkeyAction($"Пресет: {p.Label}", "raw:PRS" + p.Code)),
        ]));
        groups.Add(new("Воспроизведение (сеть, USB, Bluetooth)",
        [
            new("Пуск / пауза", "raw:NTCP/P"),
            new("Пуск", "raw:NTCPLAY"),
            new("Пауза", "raw:NTCPAUSE"),
            new("Стоп", "raw:NTCSTOP"),
            new("Следующий трек", "raw:NTCTRUP"),
            new("Предыдущий трек", "raw:NTCTRDN"),
            new("Повтор", "raw:NTCREPEAT"),
            new("Случайный порядок", "raw:NTCRANDOM"),
            new("Меню источника: вверх", "raw:NTCUP"),
            new("Меню источника: вниз", "raw:NTCDOWN"),
            new("Меню источника: выбрать", "raw:NTCSELECT"),
            new("Меню источника: назад", "raw:NTCRETURN"),
            new("Меню источника: в начало", "raw:NTCTOP"),
        ]));
        if (receiver.NetServices.Count > 0)
        {
            groups.Add(new("Сетевые сервисы",
                [.. receiver.NetServices.Select(s => new HotkeyAction(s.Label, $"raw:NSV{s.Code}0"))]));
        }
        groups.Add(new("Экранное меню ресивера",
        [
            new("Вверх", "raw:OSDUP"),
            new("Вниз", "raw:OSDDOWN"),
            new("Влево", "raw:OSDLEFT"),
            new("Вправо", "raw:OSDRIGHT"),
            new("Enter", "raw:OSDENTER"),
            new("Setup", "raw:OSDMENU"),
            new("Quick Menu", "raw:OSDQUICK"),
            new("Назад", "raw:OSDEXIT"),
        ]));
        groups.Add(new("Телевизор (HDMI-CEC)",
        [
            new("Питание", "raw:CTVPOWER"),
            new("Вход", "raw:CTVINPUT"),
            new("Громкость +", "raw:CTVVLUP"),
            new("Громкость −", "raw:CTVVLDN"),
            new("Без звука", "raw:CTVMUTE"),
            new("Канал +", "raw:CTVCHUP"),
            new("Канал −", "raw:CTVCHDN"),
        ]));
        groups.Add(new("Плеер (HDMI-CEC)",
        [
            new("Питание", "raw:CDVPOWER"),
            new("Пуск", "raw:CDVPLAY"),
            new("Пауза", "raw:CDVPAUSE"),
            new("Стоп", "raw:CDVSTOP"),
            new("Назад", "raw:CDVSKIP.R"),
            new("Вперёд", "raw:CDVSKIP.F"),
            new("Меню", "raw:CDVMENU"),
            new("Главное меню", "raw:CDVTOPMENU"),
        ]));

        if (settings.Scenes.Count > 0)
        {
            groups.Add(new("Сцены", [.. settings.Scenes.Select(s => new HotkeyAction(s.Name, "scene:" + s.Name))]));
        }
        if (settings.CustomCommands.Count > 0)
        {
            groups.Add(new(CustomGroup,
                [.. settings.CustomCommands.Select(c => new HotkeyAction($"{c.Name}  ({c.Command})", "raw:" + c.Command))]));
        }

        // Hotkeys whose action is not in the list right now (a deleted scene, an input of another receiver)
        // still have to be visible, or they could never be removed.
        var known = groups.SelectMany(g => g.Actions).Select(a => a.Command).ToHashSet(StringComparer.Ordinal);
        var unknown = settings.Hotkeys.Keys.Where(c => !known.Contains(c)).Select(c => new HotkeyAction(c, c)).ToList();
        if (unknown.Count > 0) groups.Add(new(UnknownGroup, unknown));

        return groups;
    }

    private static HotkeyActionGroup Zone(string title, string prefix, ZoneState zone) => new(title,
    [
        new("Питание: включить / выключить", prefix + "power"),
        new("Питание: включить", prefix + "power-on"),
        new("Питание: выключить", prefix + "power-off"),
        new("Громче", prefix + "vol-up"),
        new("Тише", prefix + "vol-down"),
        new("Без звука: включить / выключить", prefix + "mute"),
        .. zone.Inputs.Select(i => new HotkeyAction($"Вход: {i.Name}", $"{prefix}input:{i.Id}")),
    ]);

    private static IEnumerable<HotkeyAction> Choice(string title, string cycleName, ChoiceControl control) =>
        control.Options
            .Select(o => new HotkeyAction($"{title}: {o.Label}", $"raw:{control.Command}{o.Code}"))
            .Prepend(new HotkeyAction($"{title}: следующее значение", "cycle:" + cycleName));
}
