using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OnkyoMaster.Protocol;
using OnkyoMaster.Services;

namespace OnkyoMaster.Model;

/// <summary>One line of the receiver's on-screen list for network sources (it shows ten at a time).</summary>
public sealed class NetListLine(int index) : ObservableObject
{
    private string _text = "";
    private bool _isCursor;

    public int Index { get; } = index;
    public string Text { get => _text; set => Set(ref _text, value); }
    public bool IsCursor { get => _isCursor; set => Set(ref _isCursor, value); }
}

/// <summary>
/// The receiver as the UI sees it: observable state kept in sync over eISCP, plus the commands to change it.
/// Must be created and used on the UI thread.
/// </summary>
public sealed class Receiver : ObservableObject, IDisposable
{
    private static readonly string[] EssentialQueries = ["PWRQSTN", "MVLQSTN", "AMTQSTN", "SLIQSTN", "LMDQSTN"];

    private static readonly string[] DetailQueries =
    [
        "IFAQSTN", "IFVQSTN",
        "ZPWQSTN", "ZVLQSTN", "ZMTQSTN", "SLZQSTN", "PW3QSTN", "VL3QSTN", "MT3QSTN", "SL3QSTN",
        "TFRQSTN", "CTLQSTN", "SWLQSTN", "DIMQSTN", "SLPQSTN", "LTNQSTN", "RASQSTN", "MOTQSTN", "HDOQSTN",
        "TUNQSTN", "PRSQSTN", "NSTQSTN", "NTIQSTN", "NATQSTN", "NALQSTN",
    ];

    private const int MaxLogLines = 400;
    private const int StableSessionMs = 30_000;
    private const string ContentionHint = ". Похоже, с этого компьютера к нему подключается ещё одна программа управления";
    private const string FmInput = "24";
    private const string DabInput = "33";

    private readonly AppSettings _settings;
    private readonly EiscpClient _client = new();
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Dictionary<string, Action<string>> _handlers;
    private readonly HttpClient _http;
    private readonly DispatcherTimer _signalTimer;
    private readonly DispatcherTimer _artTimer;
    private readonly DispatcherTimer _resyncTimer;
    private readonly DispatcherTimer _stableTimer;
    private string _connectedText = "";

    private bool _isConnected;
    private bool _hasProblem;
    private string _status = "";
    private ReceiverInfo? _info;
    private int _failures;
    private int _sessionsCutShort;
    private long _connectedAt;
    private bool _powerFresh;
    private long _quietUntil;
    private string _audioInfo = "";
    private string _videoInfo = "";
    private string _signalMode = "";
    private int _tunerRaw;
    private int _preset;
    private string _dabStation = "";
    private string _netTitle = "";
    private string _netArtist = "";
    private string _netAlbum = "";
    private string _netTime = "";
    private string _netListTitle = "";
    private bool _isPlaying;
    private ImageSource? _netArt;
    private string? _artUrl;
    private int _artRequest;

    public Receiver(AppSettings settings)
    {
        _settings = settings;
        _http = CreateHttpClient();

        Main = new ZoneState(this, "Main", 1, "PWR", "MVL", "AMT", "SLI");
        Zone2 = new ZoneState(this, "Zone 2", 2, "ZPW", "ZVL", "ZMT", "SLZ");
        Zone3 = new ZoneState(this, "Zone 3", 4, "PW3", "VL3", "MT3", "SL3");

        ListeningMode = new ChoiceControl("LMD", ListeningModes.Selectable, Send);
        Dimmer = new ChoiceControl("DIM", [new("00", "Яркий"), new("01", "Приглушённый"), new("02", "Тёмный")], Send);
        Sleep = new ChoiceControl("SLP",
        [
            new("OFF", "Выключен", "Выкл"), new("0F", "15 минут", "15"), new("1E", "30 минут", "30"),
            new("2D", "45 минут", "45"), new("3C", "60 минут", "60"), new("5A", "90 минут", "90"),
        ], Send);
        LateNight = new ChoiceControl("LTN",
            [new("00", "Выключен", "Выкл"), new("01", "Слабый"), new("02", "Сильный"), new("03", "Авто")], Send);
        HdmiOut = new ChoiceControl("HDO",
            [new("01", "MAIN"), new("02", "SUB"), new("03", "MAIN + SUB"), new("00", "Выключен", "Выкл")], Send);
        MusicOptimizer = new ToggleControl("MOT", Send);
        CinemaFilter = new ToggleControl("RAS", Send);
        Bass = new LevelControl("Низкие частоты", "TFRB", -10, 10, 1, v => "TFRB" + Signed(v, 1), FormatDb, Send);
        Treble = new LevelControl("Высокие частоты", "TFRT", -10, 10, 1, v => "TFRT" + Signed(v, 1), FormatDb, Send);
        CenterLevel = new LevelControl("Центральный канал", "CTL", -12, 12, 0.5, v => "CTL" + Signed(v * 2, 2), FormatDb, Send);
        SubwooferLevel = new LevelControl("Сабвуфер", "SWL", -15, 12, 0.5, v => "SWL" + Signed(v * 2, 2), FormatDb, Send);

        for (var i = 0; i < 10; i++) NetList.Add(new NetListLine(i));

        _handlers = new Dictionary<string, Action<string>>
        {
            ["LMD"] = code =>
            {
                ListeningMode.Apply(code);
                _signalMode = "";
                Raise(nameof(ListeningModeName));
                RefreshSignalInfoSoon();
            },
            ["DIM"] = Dimmer.Apply,
            ["SLP"] = Sleep.Apply,
            ["LTN"] = LateNight.Apply,
            ["HDO"] = HdmiOut.Apply,
            ["MOT"] = MusicOptimizer.Apply,
            ["RAS"] = CinemaFilter.Apply,
            ["TFR"] = ApplyTone,
            ["CTL"] = arg => ApplyHalfSteps(CenterLevel, arg),
            ["SWL"] = arg => ApplyHalfSteps(SubwooferLevel, arg),
            ["IFA"] = ApplyAudioInfo,
            ["IFV"] = ApplyVideoInfo,
            ["TUN"] = arg =>
            {
                if (int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var raw)) _tunerRaw = raw;
                Raise(nameof(TunerText));
            },
            ["PRS"] = arg =>
            {
                if (int.TryParse(arg, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var preset)) _preset = preset;
                Raise(nameof(PresetText));
            },
            ["DSN"] = arg =>
            {
                _dabStation = arg.Trim();
                Raise(nameof(TunerText));
            },
            ["NTI"] = arg => NetTitle = arg.Trim(),
            ["NAT"] = arg => NetArtist = arg.Trim(),
            ["NAL"] = arg => NetAlbum = arg.Trim(),
            ["NTM"] = arg => NetTime = arg.Contains("--", StringComparison.Ordinal) ? "" : arg,
            ["NST"] = arg => IsPlaying = arg.Length > 0 && arg[0] == 'P',
            ["NJA"] = ApplyArt,
            ["NLT"] = arg => NetListTitle = arg.Length > 22 ? arg[22..].Trim() : "",
            ["NLS"] = ApplyNetList,
        };

        _signalTimer = OneShot(1000, () =>
        {
            Query("IFAQSTN");
            Query("IFVQSTN");
        });
        _artTimer = OneShot(700, FetchArt);
        _resyncTimer = OneShot(2500, () => StartSync(includeInfo: false));
        _stableTimer = OneShot(StableSessionMs, () =>
        {
            // The connection has held: whatever was cutting it off is gone.
            if (!IsConnected) return;
            _sessionsCutShort = 0;
            SetStatus(_connectedText);
        });

        Main.Volume.Changed += RaiseVolumeFeedback;
        Main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ZoneState.Muted)) RaiseVolumeFeedback();
            if (e.PropertyName == nameof(ZoneState.Power)) Raise(nameof(ListeningModeName));
            if (e.PropertyName == nameof(ZoneState.InputId)) Raise(nameof(TunerText));
        };

        _client.BindResolver = remote => NetUtil.ResolveBind(_settings.NetworkInterface, remote);
        _client.MessageReceived += message => _dispatcher.BeginInvoke(() => OnMessage(message));
        _client.MessageSent += command => _dispatcher.BeginInvoke(() => AddLog("→ " + command));
        _client.ConnectionChanged += (connected, detail) => _dispatcher.BeginInvoke(() => OnConnectionChanged(connected, detail));

        LoadCachedInfo();
    }

    /// <summary>Main volume or mute changed after the initial sync; used for the on-screen indicator.</summary>
    public event Action? VolumeFeedback;

    public ZoneState Main { get; }
    public ZoneState Zone2 { get; }
    public ZoneState Zone3 { get; }

    public ChoiceControl ListeningMode { get; }
    public ChoiceControl Dimmer { get; }
    public ChoiceControl Sleep { get; }
    public ChoiceControl LateNight { get; }
    public ChoiceControl HdmiOut { get; }
    public ToggleControl MusicOptimizer { get; }
    public ToggleControl CinemaFilter { get; }
    public LevelControl Bass { get; }
    public LevelControl Treble { get; }
    public LevelControl CenterLevel { get; }
    public LevelControl SubwooferLevel { get; }

    /// <summary>The listening-mode buttons this model has (Movie/TV, Music, Game, THX); Code is the LMD parameter.</summary>
    public ObservableCollection<Option> ModeButtons { get; } = [];

    public ObservableCollection<Option> NetServices { get; } = [];
    public ObservableCollection<Option> Presets { get; } = [];
    public ObservableCollection<NetListLine> NetList { get; } = [];
    public ObservableCollection<string> Log { get; } = [];

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (Set(ref _isConnected, value)) Raise(nameof(IsReady));
        }
    }

    /// <summary>Connected and the receiver's description is known, so values can be interpreted safely.</summary>
    public bool IsReady => _isConnected && _info is not null;

    public string Status { get => _status; private set => Set(ref _status, value); }

    /// <summary>
    /// The status describes something the user has to look at (no receiver, no link for a while),
    /// as opposed to a normal state or a reconnect in progress. The UI only spends space on the status then.
    /// </summary>
    public bool HasProblem { get => _hasProblem; private set => Set(ref _hasProblem, value); }

    public ReceiverInfo? Info => _info;

    public string ModelName =>
        _info is { Model.Length: > 0 } ? _info.Model :
        _settings.ReceiverModel.Length > 0 ? _settings.ReceiverModel : "Onkyo";

    /// <summary>The mode as the receiver names it for the current signal, falling back to the code table.</summary>
    public string ListeningModeName
    {
        get
        {
            if (!Main.Power) return "—";
            if (_signalMode.Length > 0) return _signalMode;
            return ListeningMode.Value is { } code ? ListeningModes.NameOf(code) : "—";
        }
    }

    public string AudioInfo { get => _audioInfo; private set => Set(ref _audioInfo, value); }
    public string VideoInfo { get => _videoInfo; private set => Set(ref _videoInfo, value); }

    public string TunerText
    {
        get
        {
            if (Main.InputId == DabInput) return _dabStation.Length > 0 ? $"DAB · {_dabStation}" : "DAB";
            if (_tunerRaw <= 0) return "—";
            return $"FM {(_tunerRaw / 100.0).ToString("0.00", CultureInfo.InvariantCulture)} МГц";
        }
    }

    public string PresetText => _preset > 0 ? $"Пресет {_preset}" : "Пресет не выбран";

    public string NetTitle
    {
        get => _netTitle;
        private set
        {
            if (!Set(ref _netTitle, value)) return;
            Raise(nameof(HasNowPlaying));
            _artTimer.Stop();
            _artTimer.Start();
        }
    }

    public string NetArtist { get => _netArtist; private set => Set(ref _netArtist, value); }
    public string NetAlbum { get => _netAlbum; private set => Set(ref _netAlbum, value); }
    public string NetTime { get => _netTime; private set => Set(ref _netTime, value); }
    public string NetListTitle { get => _netListTitle; private set => Set(ref _netListTitle, value); }
    public bool IsPlaying { get => _isPlaying; private set => Set(ref _isPlaying, value); }
    public ImageSource? NetArt { get => _netArt; private set => Set(ref _netArt, value); }
    public bool HasNowPlaying => _netTitle.Length > 0;

    // ---- connection -------------------------------------------------------------------------

    /// <summary>(Re)connects to the address in settings.</summary>
    public void Connect()
    {
        IsConnected = false;
        if (!IPAddress.TryParse(_settings.ReceiverAddress, out var address))
        {
            _client.Stop();
            SetStatus("Ресивер не выбран — укажите адрес в настройках", problem: true);
            return;
        }
        _failures = 0;
        SetStatus($"Подключение к {address}…");
        _client.Start(address);
    }

    /// <summary>Drops the socket so the client reconnects; used after the PC wakes up.</summary>
    public void Reconnect() => _client.DropConnection();

    /// <summary>Searches the network and, when a receiver answers, stores it in settings and connects.</summary>
    public async Task<List<DiscoveredReceiver>> DiscoverAndConnectAsync()
    {
        SetStatus("Поиск ресивера в сети…");
        var found = await EiscpDiscovery.DiscoverAsync(DiscoveryInterface());
        var pick = found.FirstOrDefault(f => f.Id == _settings.ReceiverId) ?? found.FirstOrDefault();
        if (pick is null)
        {
            SetStatus("Ресивер в сети не найден — укажите адрес в настройках", problem: true);
            return found;
        }
        _settings.ReceiverAddress = pick.Address.ToString();
        _settings.ReceiverId = pick.Id;
        _settings.ReceiverModel = pick.Model;
        _settings.Save();
        Raise(nameof(ModelName));
        Connect();
        return found;
    }

    public void Send(string command, string? coalesceKey)
    {
        command = command.Trim();
        if (command.Length >= 3) _client.Send(command, coalesceKey);
    }

    public void Send(string command) => Send(command, null);

    public void Query(string command) => _client.Query(command);

    /// <summary>Re-applies settings that affect the model (volume limit).</summary>
    public void ApplySettings()
    {
        if (_info is not null) ConfigureZones(_info);
    }

    // ---- actions ----------------------------------------------------------------------------

    public void TuneTo(double megahertz)
    {
        if (megahertz is < 60 or > 120) return;
        Send($"TUN{(int)Math.Round(megahertz * 100):D5}");
    }

    public void SelectNetService(string id) => Send($"NSV{id}0");

    public void SelectNetListLine(int index) => Send($"NLSL{index}");

    /// <summary>Puts the main zone into standby without going through the queue; for PC sleep or shutdown.</summary>
    public void StandbyNow() => _client.SendNow("PWR00");

    /// <summary>Waits for the connection, turns the main zone on and optionally selects an input.</summary>
    public async Task PowerOnAsync(string? inputId)
    {
        // After a reconnect the remembered power state is stale until the receiver reports it again.
        if (!await WaitForAsync(() => IsReady && _powerFresh, 60_000)) return;
        if (!Main.DevicePower)
        {
            Main.Power = true;
            if (!await WaitForAsync(() => Main.DevicePower, 10_000)) return;
            await Task.Delay(2000);
        }
        if (!string.IsNullOrEmpty(inputId) && Main.InputId != inputId) Main.SelectInput(inputId);
    }

    public async Task ApplySceneAsync(Scene scene)
    {
        if (!IsReady) return;
        await PowerOnAsync(scene.InputId);
        if (scene.Volume is { } volume)
        {
            await Task.Delay(400);
            Main.Volume.Value = volume;
        }
        if (scene.ListeningMode.Length > 0)
        {
            await Task.Delay(800);
            Send("LMD" + scene.ListeningMode);
        }
    }

    public Scene CaptureScene(string name) => new()
    {
        Name = name,
        InputId = Main.InputId ?? "",
        InputName = Main.InputName,
        Volume = Main.Volume.IsKnown ? Main.Volume.Value : null,
        ListeningMode = ListeningMode.Value ?? "",
        ListeningModeName = ListeningMode.Value is { } code ? ListeningModes.NameOf(code) : "",
    };

    public void Dispose()
    {
        _client.Dispose();
        _http.Dispose();
    }

    // ---- connection events ------------------------------------------------------------------

    private void OnConnectionChanged(bool connected, string? detail)
    {
        IsConnected = connected;
        _powerFresh = false;
        if (connected)
        {
            _failures = 0;
            _connectedAt = Environment.TickCount64;
            _quietUntil = Environment.TickCount64 + 5000;
            _connectedText = detail is null ? "Подключено" : $"Подключено через {detail}";
            // After repeated cut-offs a fresh connection is not yet good news; say so until it has held for a while.
            if (_sessionsCutShort >= 2) SetStatus(_connectedText + ", но ресивер раз за разом разрывает соединение" + ContentionHint, problem: true);
            else SetStatus(_connectedText);
            _stableTimer.Stop();
            _stableTimer.Start();
            AddLog($"—— подключено к {_settings.ReceiverAddress}" + (detail is null ? "" : $" с адреса {detail}"));
            StartSync(includeInfo: true);
            return;
        }

        _failures++;
        _stableTimer.Stop();
        // Count sessions the receiver cut short; a long one in between clears the suspicion.
        var cutShort = detail == EiscpClient.ClosedByReceiver && Environment.TickCount64 - _connectedAt < StableSessionMs;
        _sessionsCutShort = cutShort ? _sessionsCutShort + 1 : 0;
        AddLog($"—— нет связи: {detail}");
        var contended = _sessionsCutShort >= 2;
        var hint = contended ? ": он сам разрывает соединение" + ContentionHint : ConnectionHint();
        // A single dropped connection is usually back within a second; don't alarm the user over it.
        SetStatus("Нет связи с ресивером" + hint, problem: _failures >= 2 || contended);
        // The receiver may have got a new address from DHCP: look it up by its id, now and then every ten minutes or so.
        if (_failures == 3 || _failures % 75 == 0) _ = RediscoverAsync();
    }

    private string ConnectionHint()
    {
        if (!IPAddress.TryParse(_settings.ReceiverAddress, out var address)) return "";
        var mode = _settings.NetworkInterface;
        if (mode == NetUtil.BindNone)
        {
            return _failures >= 2 && NetUtil.GetInterfaces().Any(i => i.Contains(address))
                ? ". Он в локальной подсети, но не отвечает: если включён VPN, выберите сетевой интерфейс в настройках"
                : "";
        }
        if (NetUtil.ResolveBind(mode, address).Available) return "";
        return mode == NetUtil.BindAuto
            ? ": ни один сетевой адаптер не находится в его подсети. Если ресивер за маршрутизатором, выберите в настройках «Не привязывать»"
            : ": выбранный сетевой адаптер не подключён";
    }

    /// <summary>With a specific adapter chosen, searching the others would only find receivers the app may not reach.</summary>
    private string? DiscoveryInterface() =>
        _settings.NetworkInterface is NetUtil.BindNone or NetUtil.BindAuto ? null : _settings.NetworkInterface;

    private async Task RediscoverAsync()
    {
        if (_settings.ReceiverId.Length == 0) return;
        var found = await EiscpDiscovery.DiscoverAsync(DiscoveryInterface());
        var match = found.FirstOrDefault(f => f.Id == _settings.ReceiverId);
        if (match is null || match.Address.ToString() == _settings.ReceiverAddress || IsConnected) return;
        _settings.ReceiverAddress = match.Address.ToString();
        _settings.Save();
        Connect();
    }

    private void SetStatus(string text, bool problem = false)
    {
        Status = text;
        HasProblem = problem;
    }

    private void StartSync(bool includeInfo)
    {
        // Without the description values cannot be interpreted, so it goes first on a fresh install.
        if (_info is null) Query("NRIQSTN");
        foreach (var query in EssentialQueries) Query(query);
        if (_info is not null && includeInfo) Query("NRIQSTN");
        foreach (var query in DetailQueries) Query(query);
    }

    // ---- incoming messages ------------------------------------------------------------------

    private void OnMessage(string message)
    {
        var command = message[..3];
        var arg = message[3..];
        LogReceived(command, arg);

        if (command == "NRI")
        {
            ApplyInfo(arg, cache: true);
            return;
        }
        if (arg == "N/A") return;

        var wasOn = Main.DevicePower;
        if (Main.Handle(command, arg))
        {
            if (command == "PWR") _powerFresh = true;
            if (command == "PWR" && !wasOn && Main.DevicePower && Environment.TickCount64 > _quietUntil)
            {
                // Just switched on: most values were unavailable in standby.
                _resyncTimer.Stop();
                _resyncTimer.Start();
            }
            if (command == "SLI") RefreshSignalInfoSoon();
            return;
        }
        if (Zone2.Handle(command, arg) || Zone3.Handle(command, arg)) return;
        if (_handlers.TryGetValue(command, out var handler)) handler(arg);
    }

    private void LoadCachedInfo()
    {
        try
        {
            if (File.Exists(InfoCachePath)) ApplyInfo(File.ReadAllText(InfoCachePath), cache: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string InfoCachePath => Path.Combine(AppSettings.Folder, "receiver-info.xml");

    private void ApplyInfo(string xml, bool cache)
    {
        var info = ReceiverInfo.Parse(xml);
        if (info is null) return;
        _info = info;
        ConfigureZones(info);

        if (info.Controls.TryGetValue("Bass", out var bass)) SetRange(Bass, bass);
        if (info.Controls.TryGetValue("Treble", out var treble)) SetRange(Treble, treble);
        if (info.Controls.TryGetValue("Center Level", out var center)) SetRange(CenterLevel, center);
        if (info.Controls.TryGetValue("Subwoofer Level", out var sub)) SetRange(SubwooferLevel, sub);

        Replace(ModeButtons, info.Controls.Values
            .Where(c => c.Enabled && c.Code is { Length: > 0 } && c.Id.StartsWith("LMD ", StringComparison.Ordinal))
            .Select(c => new Option(c.Code!, c.Id[4..])));
        Replace(NetServices, info.NetServices.Where(s => s.Enabled && (s.ZoneMask & 1) != 0).Select(s => new Option(s.Id, s.Name)));
        Replace(Presets, info.Presets.Where(p => p.Frequency > 0 || p.Name.Length > 0).Select(p => new Option(
            p.Number.ToString("X2", CultureInfo.InvariantCulture),
            p.Name.Length > 0 ? p.Name : (p.Frequency / 1000.0).ToString("0.00", CultureInfo.InvariantCulture))));

        Raise(nameof(Info));
        Raise(nameof(ModelName));
        Raise(nameof(IsReady));

        if (!cache) return;
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.WriteAllText(InfoCachePath, xml);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void ConfigureZones(ReceiverInfo info)
    {
        ZoneState[] zones = [Main, Zone2, Zone3];
        for (var i = 0; i < zones.Length; i++)
        {
            var zone = zones[i];
            var inputs = info.Selectors
                .Where(s => s.Enabled && (s.ZoneMask & zone.Bit) != 0)
                .Select(s => new InputOption(s.Id, s.Name))
                .ToList();
            zone.Configure(info.Zones.FirstOrDefault(z => z.Id == i + 1), inputs, _settings.MaxVolume);
        }
    }

    private static void SetRange(LevelControl control, ControlInfo info)
    {
        if (info.Min is { } min) control.Minimum = min;
        if (info.Max is { } max) control.Maximum = max;
    }

    private static void Replace(ObservableCollection<Option> target, IEnumerable<Option> items)
    {
        var list = items.ToList();
        if (target.SequenceEqual(list)) return;
        target.Clear();
        foreach (var item in list) target.Add(item);
    }

    private void ApplyTone(string arg)
    {
        // "B+4T00": bass and treble, each a sign and one hex digit ("00" for zero).
        var b = arg.IndexOf('B');
        var t = arg.IndexOf('T');
        if (b >= 0 && arg.Length >= b + 3 && TryParseSigned(arg.AsSpan(b + 1, 2), out var bass)) Bass.ApplyFromDevice(bass);
        if (t >= 0 && arg.Length >= t + 3 && TryParseSigned(arg.AsSpan(t + 1, 2), out var treble)) Treble.ApplyFromDevice(treble);
    }

    private static void ApplyHalfSteps(LevelControl control, string arg)
    {
        if (TryParseSigned(arg, out var raw)) control.ApplyFromDevice(raw / 2.0);
    }

    private void ApplyAudioInfo(string arg)
    {
        // "HDMI 2,PCM,48 kHz,2.0 ch,Dolby Audio - Surr,5.1 ch,": terminal, format, rate, channels, mode, output channels
        var fields = arg.Split(',').Select(f => f.Trim()).ToArray();
        string Field(int i) => i < fields.Length ? fields[i] : "";

        var input = Join(Field(0), Field(1), Field(2), Field(3));
        var output = Join(Field(4), Field(5));
        AudioInfo = input.Length > 0 && output.Length > 0 ? $"{input}  →  {output}" : input + output;
        _signalMode = Field(4);
        Raise(nameof(ListeningModeName));
    }

    private void ApplyVideoInfo(string arg)
    {
        // terminal, resolution, colour, depth for the input, then the same four for the output, then picture mode
        var fields = arg.Split(',').Select(f => f.Trim()).ToArray();
        string Field(int i) => i < fields.Length && fields[i] != "Unknown" ? fields[i] : "";

        if (Field(0).Length == 0 || Field(0) == "No Video")
        {
            VideoInfo = "Нет видеосигнала";
            return;
        }
        var input = Join(Field(0), Field(1), Field(2), Field(3));
        var output = Join(Field(4), Field(5), Field(6), Field(7));
        VideoInfo = output.Length > 0 ? $"{input}  →  {output}" : input;
    }

    private void ApplyArt(string arg)
    {
        // Only the URL form ("2-http://…") is handled; this model offers it.
        if (arg.StartsWith("2-", StringComparison.Ordinal))
        {
            _artUrl = arg[2..].Trim();
            _artTimer.Stop();
            _artTimer.Start();
        }
        else if (arg.StartsWith("n-", StringComparison.OrdinalIgnoreCase))
        {
            NetArt = null;
        }
    }

    private async void FetchArt()
    {
        var url = _artUrl;
        var request = ++_artRequest;
        if (url is null || !HasNowPlaying)
        {
            NetArt = null;
            return;
        }
        try
        {
            var bytes = await _http.GetByteArrayAsync(url);
            if (request != _artRequest) return;
            NetArt = Decode(bytes);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or SocketException)
        {
            if (request == _artRequest) NetArt = null;
        }
    }

    private static ImageSource? Decode(byte[] bytes)
    {
        if (bytes.Length < 64) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 320;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or ArgumentException)
        {
            return null;
        }
    }

    private void ApplyNetList(string arg)
    {
        if (arg.Length < 3) return;
        if (arg[0] == 'C')
        {
            // "C3P": cursor on line 3; P means a new page follows, C means only the cursor moved.
            if (arg[2] == 'P')
            {
                foreach (var line in NetList) line.Text = "";
            }
            var cursor = arg[1] - '0';
            foreach (var line in NetList) line.IsCursor = line.Index == cursor;
        }
        else if (arg[0] is 'U' or 'A' && arg[1] is >= '0' and <= '9')
        {
            NetList[arg[1] - '0'].Text = arg[3..];
        }
    }

    private void RefreshSignalInfoSoon()
    {
        _signalTimer.Stop();
        _signalTimer.Start();
    }

    private void RaiseVolumeFeedback()
    {
        if (Environment.TickCount64 > _quietUntil) VolumeFeedback?.Invoke();
    }

    // ---- helpers ----------------------------------------------------------------------------

    private void LogReceived(string command, string arg)
    {
        // The play-time counter and the front display tick every second and would drown everything else.
        if (command is "NTM" or "FLD") return;
        var text = command == "NRI" ? $"<описание ресивера, {arg.Length} символов>" :
            arg.Length > 120 ? arg[..120] + "…" : arg;
        AddLog($"← {command}{text}");
    }

    private void AddLog(string line)
    {
        Log.Add($"{DateTime.Now:HH:mm:ss.fff}  {line}");
        while (Log.Count > MaxLogLines) Log.RemoveAt(0);
    }

    private DispatcherTimer OneShot(int milliseconds, Action action)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        return timer;
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) return false;
            await Task.Delay(200);
        }
        return true;
    }

    private HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(4),
            // Cover art comes from the receiver itself, so it needs the same adapter binding as the control socket.
            ConnectCallback = async (context, ct) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    if (IPAddress.TryParse(context.DnsEndPoint.Host, out var remote))
                    {
                        var bind = NetUtil.ResolveBind(_settings.NetworkInterface, remote);
                        if (!bind.Available) throw new SocketException((int)SocketError.NetworkUnreachable);
                        if (bind.Local is not null) socket.Bind(new IPEndPoint(bind.Local, 0));
                        await socket.ConnectAsync(new IPEndPoint(remote, context.DnsEndPoint.Port), ct);
                    }
                    else
                    {
                        await socket.ConnectAsync(context.DnsEndPoint, ct);
                    }
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
    }

    private static string Join(params string[] parts) => string.Join(" ", parts.Where(p => p.Length > 0));

    private static string FormatDb(double value) =>
        value == 0 ? "0 дБ" : value.ToString("+0.#;−0.#", CultureInfo.InvariantCulture) + " дБ";

    /// <summary>ISCP signed hex: "+4", "-0C", and all zeros for zero.</summary>
    private static string Signed(double value, int digits)
    {
        var n = (int)Math.Round(value);
        if (n == 0) return new string('0', digits + 1);
        return (n > 0 ? "+" : "-") + Math.Abs(n).ToString("X" + digits, CultureInfo.InvariantCulture);
    }

    private static bool TryParseSigned(ReadOnlySpan<char> text, out int value)
    {
        value = 0;
        text = text.Trim();
        if (text.IsEmpty) return false;
        var sign = 1;
        if (text[0] == '+')
        {
            text = text[1..];
        }
        else if (text[0] == '-')
        {
            sign = -1;
            text = text[1..];
        }
        if (!int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var magnitude)) return false;
        value = sign * magnitude;
        return true;
    }
}
