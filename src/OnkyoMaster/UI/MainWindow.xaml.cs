using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OnkyoMaster.Model;
using OnkyoMaster.Protocol;
using OnkyoMaster.Services;

namespace OnkyoMaster.UI;

/// <summary>The full control panel. Closing it only hides it; the app keeps running in the tray.</summary>
public partial class MainWindow : Window
{
    private readonly Receiver _receiver;
    private readonly AppSettings _settings;
    // True while controls are being filled in, including the XAML load itself, so that
    // change handlers don't mistake that for the user editing a setting.
    private bool _loading = true;

    public MainWindow(Receiver receiver, AppSettings settings)
    {
        InitializeComponent();
        _receiver = receiver;
        _settings = settings;
        DataContext = receiver;
        ScenesList.ItemsSource = settings.Scenes;
        HotkeysTab.Attach(receiver, settings);

        LoadSettings();
        receiver.Log.CollectionChanged += Log_Changed;
        receiver.Main.Inputs.CollectionChanged += (_, _) => LoadPcInputs();
    }

    /// <summary>Set when the app is exiting, so the window really closes.</summary>
    public bool AllowClose { get; set; }

    /// <summary>Number of tabs; used when rendering every tab to an image.</summary>
    public int TabCount => Tabs.Items.Count;

    public void SelectTab(int index) => Tabs.SelectedIndex = index;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose && _settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        // With "close to tray" switched off, closing the window ends the whole app.
        if (!AllowClose) App.Current.Quit();
    }

    // ---- receiver commands ------------------------------------------------------------------

    private void Command_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string command }) _receiver.Send(command);
    }

    private void Mode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string code }) _receiver.Send("LMD" + code);
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string preset }) _receiver.Send("PRS" + preset);
    }

    private void NetService_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id }) _receiver.SelectNetService(id);
    }

    private void NetLine_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: int index }) _receiver.SelectNetListLine(index);
    }

    private void Tune_Click(object sender, RoutedEventArgs e)
    {
        var text = FrequencyBox.Text.Trim().Replace(',', '.');
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var megahertz)) _receiver.TuneTo(megahertz);
    }

    private void FrequencyBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Tune_Click(sender, e);
    }

    // ---- console ----------------------------------------------------------------------------

    private void ConsoleSend_Click(object sender, RoutedEventArgs e)
    {
        var command = ConsoleBox.Text.Trim();
        if (command.Length < 3) return;
        _receiver.Send(command);
        ConsoleBox.SelectAll();
    }

    private void ConsoleBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ConsoleSend_Click(sender, e);
    }

    private void Log_Changed(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || !LogList.IsVisible || LogList.Items.Count == 0) return;
        LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
    }

    // ---- settings: connection ---------------------------------------------------------------

    private void LoadSettings()
    {
        _loading = true;
        AddressBox.Text = _settings.ReceiverAddress;
        LoadInterfaces();
        AutoStartBox.IsChecked = AutoStart.IsEnabled;
        CloseToTrayBox.IsChecked = _settings.CloseToTray;
        OsdBox.IsChecked = _settings.ShowOsd;
        MaxVolumeSlider.Value = _settings.MaxVolume;
        MaxVolumeText.Text = _settings.MaxVolume.ToString("0");
        StepBox.ItemsSource = new[] { 0.5, 1.0, 2.0, 3.0 };
        StepBox.SelectedItem = _settings.VolumeStep;
        PowerOnBox.IsChecked = _settings.PowerOnWithPc;
        StandbyBox.IsChecked = _settings.StandbyWithPc;
        LoadPcInputs();
        _loading = false;
    }

    private void LoadInterfaces()
    {
        var wasLoading = _loading;
        _loading = true;
        var options = new List<Option>
        {
            new(NetUtil.BindAuto, "Авто — адаптер в подсети ресивера"),
            new(NetUtil.BindNone, "Не привязывать — путь выбирает Windows"),
        };
        options.AddRange(NetUtil.GetInterfaces()
            .GroupBy(i => i.Id)
            .Select(g => new Option(g.Key, $"{g.First().Name} ({g.First().Address})")));
        if (options.All(o => o.Code != _settings.NetworkInterface))
        {
            options.Add(new Option(_settings.NetworkInterface, "Выбранный адаптер сейчас не подключён"));
        }
        InterfaceBox.ItemsSource = options;
        InterfaceBox.SelectedValue = _settings.NetworkInterface;
        _loading = wasLoading;
    }

    private void Interface_DropDownOpened(object sender, EventArgs e) => LoadInterfaces();

    private void Interface_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || InterfaceBox.SelectedValue is not string mode || mode == _settings.NetworkInterface) return;
        _settings.NetworkInterface = mode;
        _settings.Save();
        _receiver.Connect();
    }

    private void ConnectAddress_Click(object sender, RoutedEventArgs e)
    {
        var text = AddressBox.Text.Trim();
        if (!IPAddress.TryParse(text, out _))
        {
            ShowConnectionNote("Адрес должен быть вида 192.168.1.9.");
            return;
        }
        ShowConnectionNote("");
        if (text != _settings.ReceiverAddress)
        {
            // A different address may be a different receiver; let rediscovery learn its id afresh.
            _settings.ReceiverAddress = text;
            _settings.ReceiverId = "";
        }
        _settings.Save();
        _receiver.Connect();
    }

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ConnectAddress_Click(sender, e);
    }

    private async void Discover_Click(object sender, RoutedEventArgs e)
    {
        DiscoverButton.IsEnabled = false;
        ShowConnectionNote("Поиск…");
        try
        {
            var found = await _receiver.DiscoverAndConnectAsync();
            ShowConnectionNote(found.Count == 0
                ? "Никто не ответил. Проверьте, что ресивер в той же сети и в его настройках включён Network Standby."
                : "Найдено: " + string.Join(", ", found.Select(f => $"{f.Model} — {f.Address} (через {f.InterfaceName})")));
            AddressBox.Text = _settings.ReceiverAddress;
        }
        finally
        {
            DiscoverButton.IsEnabled = true;
        }
    }

    private void ShowConnectionNote(string text)
    {
        DiscoverResult.Text = text;
        DiscoverResult.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- settings: behaviour ----------------------------------------------------------------

    private void AutoStart_Click(object sender, RoutedEventArgs e) => AutoStart.Set(AutoStartBox.IsChecked == true);

    private void Option_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowOsd = OsdBox.IsChecked == true;
        _settings.CloseToTray = CloseToTrayBox.IsChecked == true;
        _settings.PowerOnWithPc = PowerOnBox.IsChecked == true;
        _settings.StandbyWithPc = StandbyBox.IsChecked == true;
        App.Current.ApplySettings();
    }

    private void MaxVolume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _settings.MaxVolume = Math.Round(e.NewValue);
        MaxVolumeText.Text = _settings.MaxVolume.ToString("0");
        App.Current.ApplySettings();
    }

    private void Step_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || StepBox.SelectedItem is not double step) return;
        _settings.VolumeStep = step;
        App.Current.ApplySettings();
    }

    private void LoadPcInputs()
    {
        var wasLoading = _loading;
        _loading = true;
        var options = new List<Option> { new("", "Не указан") };
        options.AddRange(_receiver.Main.Inputs.Select(i => new Option(i.Id, i.Name)));
        PcInputBox.ItemsSource = options;
        PcInputBox.SelectedValue = _settings.PcInputId;
        _loading = wasLoading;
    }

    private void PcInput_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PcInputBox.SelectedValue is not string id) return;
        _settings.PcInputId = id;
        App.Current.ApplySettings();
    }

    // ---- settings: scenes -------------------------------------------------------------------

    private void SaveScene_Click(object sender, RoutedEventArgs e)
    {
        var name = SceneNameBox.Text.Trim();
        if (name.Length == 0) name = $"Сцена {_settings.Scenes.Count + 1}";
        _settings.Scenes.Add(_receiver.CaptureScene(name));
        SceneNameBox.Clear();
        App.Current.ApplySettings();
    }

    private void SceneNameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SaveScene_Click(sender, e);
    }

    private void ApplyScene_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Scene scene }) _ = _receiver.ApplySceneAsync(scene);
    }

    private void DeleteScene_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Scene scene }) return;
        // The scene's hotkey goes with it, unless another scene of the same name remains.
        if (_settings.Scenes.Count(s => s.Name == scene.Name) == 1) _settings.SetHotkey("scene:" + scene.Name, "");
        _settings.Scenes.Remove(scene);
        App.Current.ApplySettings();
    }
}
