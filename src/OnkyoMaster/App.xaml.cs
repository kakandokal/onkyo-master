using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using OnkyoMaster.Model;
using OnkyoMaster.Services;
using OnkyoMaster.UI;

namespace OnkyoMaster;

/// <summary>
/// Tray application: owns the receiver model, the tray icon, hotkeys and windows.
/// A second launch forwards its arguments to the running instance and exits.
/// </summary>
public partial class App : Application
{
    private const string MutexName = "OnkyoMaster.SingleInstance";
    private const string UserHotkeys = "user";
    private const string MediaHotkeys = "media";

    private readonly CancellationTokenSource _lifetime = new();
    private Mutex? _mutex;
    private HotkeyManager? _hotkeys;
    private VolumeCommandCapture? _volumeCommands;
    private TrayIcon? _tray;
    private FlyoutWindow? _flyout;
    private MainWindow? _main;
    private VolumeOsd? _osd;

    public static new App Current => (App)Application.Current;

    public AppSettings Settings { get; private set; } = new();
    public Receiver Receiver { get; private set; } = null!;

    /// <summary>Commands whose key combination could not be registered (taken by another program).</summary>
    public HashSet<string> FailedHotkeys { get; } = [];

    /// <summary>The keyboard's volume keys should be captured right now, but Windows would not hand them over.</summary>
    public bool MediaKeysRefused { get; private set; }

    public event Action? MediaKeysRefusedChanged;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var startInTray = false;
        string? snapshotDirectory = null;
        var snapshotWait = 12;
        var commands = new List<string>();
        for (var i = 0; i < e.Args.Length; i++)
        {
            var arg = e.Args[i];
            var next = i + 1 < e.Args.Length ? e.Args[i + 1] : null;
            switch (arg)
            {
                case "--tray": startInTray = true; break;
                case "--snapshot" when next is not null: snapshotDirectory = next; i++; break;
                case "--wait" when int.TryParse(next, out var seconds): snapshotWait = seconds; i++; break;
                case "--theme" when next is not null:
                    ThemeMode = next == "light" ? ThemeMode.Light : ThemeMode.Dark;
                    i++;
                    break;
                default: commands.Add(arg); break;
            }
        }

        if (snapshotDirectory is null)
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirstInstance);
            if (!isFirstInstance)
            {
                CommandPipe.Send(commands.Count > 0 ? commands : ["show"]);
                Shutdown();
                return;
            }
        }

        DispatcherUnhandledException += OnUnhandledException;
        Settings = AppSettings.Load();
        Receiver = new Receiver(Settings);

        if (snapshotDirectory is not null)
        {
            StartConnection();
            _ = RenderSnapshotsAsync(snapshotDirectory, snapshotWait, commands);
            return;
        }

        _hotkeys = new HotkeyManager();
        _volumeCommands = new VolumeCommandCapture();
        _volumeCommands.Step += direction => Execute(direction > 0 ? "vol-up" : "vol-down");
        _volumeCommands.MuteToggled += () => Execute("mute");
        _tray = new TrayIcon(this);
        Receiver.VolumeFeedback += ShowOsd;
        Receiver.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(Receiver.IsReady)) UpdateMediaKeys();
        };
        Receiver.Main.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ZoneState.Power)) UpdateMediaKeys();
        };
        ApplySettings();

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionEnding += OnSessionEnding;
        _ = CommandPipe.ListenAsync(command => Dispatcher.BeginInvoke(() => Execute(command)), _lifetime.Token);

        StartConnection();
        if (!startInTray) ShowMainWindow();
        if (commands.Count > 0) _ = RunStartupCommandsAsync(commands);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _lifetime.Cancel();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionEnding -= OnSessionEnding;
        _tray?.Dispose();
        _volumeCommands?.Dispose();
        _hotkeys?.Dispose();
        Receiver?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    public void Quit()
    {
        if (_main is not null) _main.AllowClose = true;
        Shutdown();
    }

    public void ShowMainWindow()
    {
        _main ??= new MainWindow(Receiver, Settings);
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Show();
        _main.Activate();
    }

    public void ToggleFlyout()
    {
        _flyout ??= new FlyoutWindow(Receiver, Settings);
        _flyout.Toggle();
    }

    /// <summary>Saves settings and re-applies everything that depends on them.</summary>
    public void ApplySettings()
    {
        Settings.Save();
        Receiver.ApplySettings();
        RegisterHotkeys();
        UpdateMediaKeys();
    }

    /// <summary>
    /// Runs a command from a hotkey, the command line or another instance.
    /// <list type="bullet">
    /// <item>show, flyout, exit — the app itself;</item>
    /// <item>power, power-on, power-off, vol-up, vol-down, mute, input:NAME — a zone; the main one,
    /// or Zone 2 / Zone 3 with a "z2-" / "z3-" prefix;</item>
    /// <item>mode:CODE, bass-up … sub-down, vocal-up, vocal-down, music-optimizer, cinema-filter,
    /// cycle:dimmer|sleep|late-night|hdmi-out — main-zone sound and display;</item>
    /// <item>scene:NAME, raw:ISCP.</item>
    /// </list>
    /// </summary>
    public void Execute(string command)
    {
        var separator = command.IndexOf(':');
        var name = (separator < 0 ? command : command[..separator]).Trim().ToLowerInvariant();
        var argument = separator < 0 ? "" : command[(separator + 1)..].Trim();

        var zone = Receiver.Main;
        if (name.StartsWith("z2-", StringComparison.Ordinal))
        {
            zone = Receiver.Zone2;
            name = name[3..];
        }
        else if (name.StartsWith("z3-", StringComparison.Ordinal))
        {
            zone = Receiver.Zone3;
            name = name[3..];
        }
        var mainOn = Receiver.Main.Power;

        switch (name)
        {
            case "show": ShowMainWindow(); break;
            case "flyout": ToggleFlyout(); break;
            case "exit": Quit(); break;

            case "power" when Receiver.IsReady: zone.Power = !zone.Power; break;
            case "power-on" when Receiver.IsReady: zone.Power = true; break;
            case "power-off" when Receiver.IsReady: zone.Power = false; break;
            case "vol-up" when zone.Power: zone.Volume.Nudge(+1, Settings.VolumeStep); break;
            case "vol-down" when zone.Power: zone.Volume.Nudge(-1, Settings.VolumeStep); break;
            case "mute" when zone.Power: zone.Muted = !zone.Muted; break;
            case "input" when zone.Power:
                var input = zone.Inputs.FirstOrDefault(i =>
                    i.Name.Equals(argument, StringComparison.OrdinalIgnoreCase) || i.Id.Equals(argument, StringComparison.OrdinalIgnoreCase));
                if (input is not null) zone.SelectInput(input.Id);
                break;

            case "mode" when mainOn: Receiver.Send("LMD" + argument.ToUpperInvariant()); break;
            case "music-optimizer" when mainOn: Receiver.MusicOptimizer.Value = !Receiver.MusicOptimizer.Value; break;
            case "cinema-filter" when mainOn: Receiver.CinemaFilter.Value = !Receiver.CinemaFilter.Value; break;
            case "cycle" when mainOn:
                var choice = argument.ToLowerInvariant() switch
                {
                    "dimmer" => Receiver.Dimmer,
                    "sleep" => Receiver.Sleep,
                    "late-night" => Receiver.LateNight,
                    "hdmi-out" => Receiver.HdmiOut,
                    _ => null,
                };
                choice?.Cycle();
                break;
            case "bass-up" or "treble-up" or "center-up" or "sub-up" or "vocal-up" when mainOn: LevelFor(name)?.Nudge(+1); break;
            case "bass-down" or "treble-down" or "center-down" or "sub-down" or "vocal-down" when mainOn: LevelFor(name)?.Nudge(-1); break;

            case "scene":
                var scene = Settings.Scenes.FirstOrDefault(s => s.Name.Equals(argument, StringComparison.OrdinalIgnoreCase));
                if (scene is not null) _ = Receiver.ApplySceneAsync(scene);
                break;
            case "raw": Receiver.Send(argument); break;
        }
    }

    private LevelControl? LevelFor(string command) => command[..command.IndexOf('-')] switch
    {
        "bass" => Receiver.Bass,
        "treble" => Receiver.Treble,
        "center" => Receiver.CenterLevel,
        "sub" => Receiver.SubwooferLevel,
        "vocal" => Receiver.Vocal,
        _ => null,
    };

    private void StartConnection()
    {
        if (Settings.ReceiverAddress.Length == 0) _ = Receiver.DiscoverAndConnectAsync();
        else Receiver.Connect();
    }

    private async Task RunStartupCommandsAsync(List<string> commands)
    {
        // Commands given to the first launch have to wait for the connection.
        for (var i = 0; i < 75 && !Receiver.IsReady; i++) await Task.Delay(200);
        await Task.Delay(1500);
        foreach (var command in commands) Execute(command);
    }

    private void RegisterHotkeys()
    {
        if (_hotkeys is null) return;
        _hotkeys.UnregisterGroup(UserHotkeys);
        FailedHotkeys.Clear();

        foreach (var (command, gesture) in Settings.Hotkeys)
        {
            if (gesture.Length == 0) continue;
            if (!_hotkeys.Register(UserHotkeys, gesture, () => Execute(command), HotkeyCatalog.Repeats(command)))
            {
                FailedHotkeys.Add(command);
            }
        }
    }

    /// <summary>
    /// The keyboard's volume keys are captured only while the receiver is on;
    /// the rest of the time they keep controlling Windows volume.
    /// How a key gets to Windows depends on the keyboard and its software, so there are two nets:
    /// hotkeys for plain key presses, and <see cref="VolumeCommandCapture"/> for volume commands.
    /// </summary>
    private void UpdateMediaKeys()
    {
        if (_hotkeys is null) return;
        var wanted = Settings.MediaKeys && Receiver.IsReady && Receiver.Main.Power;

        if (wanted) _volumeCommands?.Start();
        else _volumeCommands?.Stop();

        var refused = MediaKeysRefused;
        if (!wanted)
        {
            _hotkeys.UnregisterGroup(MediaHotkeys);
            refused = false;
        }
        else if (!_hotkeys.HasGroup(MediaHotkeys) || refused)
        {
            // Also retried after a refusal: the program that held the keys may have let go of them.
            _hotkeys.UnregisterGroup(MediaHotkeys);
            var up = _hotkeys.Register(MediaHotkeys, "VolumeUp", () => Execute("vol-up"), repeat: true);
            var down = _hotkeys.Register(MediaHotkeys, "VolumeDown", () => Execute("vol-down"), repeat: true);
            var mute = _hotkeys.Register(MediaHotkeys, "VolumeMute", () => Execute("mute"));
            refused = !(up && down && mute);
        }

        if (refused == MediaKeysRefused) return;
        MediaKeysRefused = refused;
        MediaKeysRefusedChanged?.Invoke();
    }

    private void ShowOsd()
    {
        if (!Settings.ShowOsd) return;
        // No need for an indicator while the user is looking at the slider itself.
        if (_flyout is { IsVisible: true } || _main is { IsActive: true }) return;
        _osd ??= new VolumeOsd();
        _osd.ShowFor(Receiver.Main);
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        // Suspend must be handled before returning, so this is a blocking Invoke.
        Dispatcher.Invoke(() =>
        {
            if (e.Mode == PowerModes.Suspend) StandbyWithPc();
            else if (e.Mode == PowerModes.Resume) _ = ResumeAsync();
        });
    }

    private void OnSessionEnding(object sender, SessionEndingEventArgs e) => Dispatcher.Invoke(StandbyWithPc);

    private void StandbyWithPc()
    {
        if (!Settings.StandbyWithPc || !Receiver.IsReady || !Receiver.Main.DevicePower) return;
        if (Settings.PcInputId.Length > 0 && Receiver.Main.InputId != Settings.PcInputId) return;
        Receiver.StandbyNow();
    }

    private async Task ResumeAsync()
    {
        // The old socket is dead after sleep; don't wait for keep-alive to notice.
        Receiver.Reconnect();
        if (!Settings.PowerOnWithPc) return;
        await Task.Delay(3000);
        await Receiver.PowerOnAsync(Settings.PcInputId);
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // A background app should survive a UI glitch; keep a trace for diagnosis.
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.AppendAllText(Path.Combine(AppSettings.Folder, "error.log"), $"{DateTime.Now:u}\n{e.Exception}\n\n");
        }
        catch (IOException)
        {
        }
        e.Handled = true;
    }

    // ---- development aid: render the windows to PNG files (--snapshot <dir>) ------------------

    private async Task RenderSnapshotsAsync(string directory, int waitSeconds, List<string> commands)
    {
        try
        {
            Directory.CreateDirectory(directory);
            for (var i = 0; i < 50 && !Receiver.IsReady; i++) await Task.Delay(200);
            foreach (var command in commands) Execute(command);
            await Task.Delay(waitSeconds * 1000);

            var flyout = new FlyoutWindow(Receiver, Settings) { StayOpen = true, Left = -4000, Top = 0 };
            flyout.Show();
            await Task.Delay(700);
            SavePng(flyout, Path.Combine(directory, "flyout.png"));
            flyout.Close();

            var main = new MainWindow(Receiver, Settings)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -4000,
                Top = 0,
                AllowClose = true,
            };
            main.Show();
            for (var i = 0; i < main.TabCount; i++)
            {
                main.SelectTab(i);
                await Task.Delay(600);
                SavePng(main, Path.Combine(directory, $"main-{i}.png"));

                // Pages longer than the window get a second picture, scrolled to the end.
                if (FindScrollable(main) is not { } scroller) continue;
                scroller.ScrollToEnd();
                await Task.Delay(400);
                SavePng(main, Path.Combine(directory, $"main-{i}-end.png"));
            }
            main.Close();

            var osd = new VolumeOsd();
            osd.ShowFor(Receiver.Main);
            await Task.Delay(400);
            SavePng(osd, Path.Combine(directory, "osd.png"));
            File.WriteAllLines(Path.Combine(directory, "log.txt"), Receiver.Log);
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(directory, "error.txt"), ex.ToString());
        }
        Shutdown();
    }

    private static System.Windows.Controls.ScrollViewer? FindScrollable(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is System.Windows.Controls.ScrollViewer { ScrollableHeight: > 0 } scroller) return scroller;
            if (FindScrollable(child) is { } nested) return nested;
        }
        return null;
    }

    private static void SavePng(Window window, string path)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render((Visual)window.Content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
