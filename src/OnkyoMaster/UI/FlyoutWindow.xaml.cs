using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using OnkyoMaster.Model;
using OnkyoMaster.Services;
using Forms = System.Windows.Forms;

namespace OnkyoMaster.UI;

/// <summary>The quick panel that opens from the tray icon and closes when it loses focus.</summary>
public partial class FlyoutWindow : Window
{
    private readonly Receiver _receiver;
    private readonly AppSettings _settings;
    private long _hiddenAt;

    public FlyoutWindow(Receiver receiver, AppSettings settings)
    {
        InitializeComponent();
        _receiver = receiver;
        _settings = settings;
        DataContext = receiver;
        ScenesList.ItemsSource = settings.Scenes;
        settings.Scenes.CollectionChanged += (_, _) => ApplyLayout();
        receiver.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Receiver.HasVocal)) ApplyLayout();
        };
        ApplyLayout();
        SizeChanged += (_, _) => PlaceNearTray();
    }

    /// <summary>
    /// Shows the parts chosen in the settings (see <see cref="FlyoutLayout"/>), as far as this receiver
    /// has them. Power, volume and mute are always there.
    /// </summary>
    private void ApplyLayout()
    {
        bool Has(string item) => _settings.FlyoutItems.Contains(item);
        static void Show(UIElement element, bool visible) => element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        MainZone.ShowInputs = Has(FlyoutLayout.Inputs);
        Show(SignalPanel, Has(FlyoutLayout.Signal));
        Show(ModePanel, Has(FlyoutLayout.Mode));

        var vocal = Has(FlyoutLayout.Vocal) && _receiver.HasVocal;
        Show(LateNightRow, Has(FlyoutLayout.LateNight));
        Show(CenterRow, Has(FlyoutLayout.Center));
        Show(VocalRow, vocal);
        Show(SubwooferRow, Has(FlyoutLayout.Subwoofer));
        Show(SoundPanel, Has(FlyoutLayout.LateNight) || Has(FlyoutLayout.Center) || vocal || Has(FlyoutLayout.Subwoofer));

        Show(NowPlayingPanel, Has(FlyoutLayout.NowPlaying));
        Show(ScenesPanel, Has(FlyoutLayout.Scenes) && _settings.Scenes.Count > 0);
        Show(ZonesPanel, Has(FlyoutLayout.Zones));
    }

    /// <summary>Keeps the panel open when it loses focus; used when rendering it to an image.</summary>
    public bool StayOpen { get; set; }

    /// <summary>Opens the panel, or closes it if it is open. A click on the tray icon lands here.</summary>
    public void Toggle()
    {
        if (IsVisible)
        {
            Hide();
            return;
        }
        // Clicking the tray icon first deactivates the open panel (which hides it) and then arrives here;
        // without this check that click would reopen it.
        if (Environment.TickCount64 - _hiddenAt < 300) return;
        // The settings may have changed since the last time: which parts to show, and the scale
        // of the volume level that scene tooltips mention.
        ApplyLayout();
        ScenesList.Items.Refresh();
        Show();
        PlaceNearTray();
        Activate();
    }

    private void PlaceNearTray()
    {
        if (StayOpen) return;
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var area = Forms.Screen.FromPoint(Forms.Cursor.Position).WorkingArea;
        // The 16 px shadow margin may hang over the screen edge so the visible panel sits 8 px from it.
        Left = area.Right / scale - ActualWidth + 8;
        Top = area.Bottom / scale - ActualHeight + 8;
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        if (StayOpen || !IsVisible) return;
        _hiddenAt = Environment.TickCount64;
        Hide();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Hide();
    }

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // The wheel changes volume anywhere on the panel.
        if (!_receiver.Main.Power) return;
        _receiver.Main.Volume.Nudge(e.Delta > 0 ? 1 : -1, 1);
        e.Handled = true;
    }

    private void OpenMain_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        App.Current.ShowMainWindow();
    }

    private void Mode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string code }) _receiver.Send("LMD" + code);
    }

    private void Command_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string command }) _receiver.Send(command);
    }

    private void Scene_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Scene scene }) _ = _receiver.ApplySceneAsync(scene);
    }
}
