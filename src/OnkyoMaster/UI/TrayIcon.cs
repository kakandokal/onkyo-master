using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using OnkyoMaster.Model;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace OnkyoMaster.UI;

/// <summary>
/// The notification-area icon: a volume dial that shows power, volume and connection state.
/// Left click opens the quick panel, right click a menu.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly App _app;
    private readonly Receiver _receiver;
    private readonly Forms.NotifyIcon _icon;
    private readonly DispatcherTimer _refresh;
    private Window? _menuHost;

    public TrayIcon(App app)
    {
        _app = app;
        _receiver = app.Receiver;
        _icon = new Forms.NotifyIcon { Text = "Onkyo Master", Visible = true };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) _app.ToggleFlyout();
            else if (e.Button == Forms.MouseButtons.Right) ShowMenu();
        };

        // State changes arrive in bursts; redraw once they settle.
        _refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _refresh.Tick += (_, _) =>
        {
            _refresh.Stop();
            Update();
        };
        _receiver.PropertyChanged += (_, e) =>
        {
            // The receiver also reports things like play time every second; only these affect the icon.
            if (e.PropertyName is nameof(Receiver.IsReady) or nameof(Receiver.ModelName)) Invalidate();
        };
        _receiver.Main.PropertyChanged += (_, _) => Invalidate();
        _receiver.Main.Volume.Changed += Invalidate;
        Update();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Icon?.Dispose();
        _icon.Dispose();
        _menuHost?.Close();
    }

    private void Invalidate()
    {
        if (!_refresh.IsEnabled) _refresh.Start();
    }

    private void Update()
    {
        var main = _receiver.Main;
        var ready = _receiver.IsReady;
        var on = ready && main.Power;

        var previous = _icon.Icon;
        _icon.Icon = RenderDial(Forms.SystemInformation.SmallIconSize.Width, ready, on, main.Muted, main.Volume.Value / 100.0);
        previous?.Dispose();

        var state = !ready ? "нет связи"
            : !on ? "выключен"
            : main.Muted ? $"без звука · {main.InputName}"
            : $"{main.Volume.Text} · {main.InputName}";
        var text = $"{_receiver.ModelName} — {state}";
        _icon.Text = text.Length > 120 ? text[..120] : text;
    }

    private void ShowMenu()
    {
        var main = _receiver.Main;
        var ready = _receiver.IsReady;
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };

        menu.Items.Add(new MenuItem { Header = _icon.Text, IsEnabled = false });
        menu.Items.Add(new Separator());

        var power = new MenuItem { Header = "Питание", IsCheckable = true, IsChecked = main.Power, IsEnabled = ready };
        power.Click += (_, _) => main.Power = power.IsChecked;
        menu.Items.Add(power);

        var mute = new MenuItem { Header = "Без звука", IsCheckable = true, IsChecked = main.Muted, IsEnabled = ready && main.Power };
        mute.Click += (_, _) => main.Muted = mute.IsChecked;
        menu.Items.Add(mute);

        var inputs = new MenuItem { Header = "Вход", IsEnabled = ready && main.Power && main.Inputs.Count > 0 };
        foreach (var input in main.Inputs)
        {
            var item = new MenuItem { Header = input.Name, IsChecked = input.Id == main.InputId };
            item.Click += (_, _) => main.SelectInput(input.Id);
            inputs.Items.Add(item);
        }
        menu.Items.Add(inputs);

        if (_app.Settings.Scenes.Count > 0)
        {
            var scenes = new MenuItem { Header = "Сцены", IsEnabled = ready };
            foreach (var scene in _app.Settings.Scenes)
            {
                var item = new MenuItem { Header = scene.Name };
                item.Click += (_, _) => _ = _receiver.ApplySceneAsync(scene);
                scenes.Items.Add(item);
            }
            menu.Items.Add(scenes);
        }

        menu.Items.Add(new Separator());
        var open = new MenuItem { Header = "Панель управления" };
        open.Click += (_, _) => _app.ShowMainWindow();
        menu.Items.Add(open);
        var exit = new MenuItem { Header = "Выход" };
        exit.Click += (_, _) => _app.Quit();
        menu.Items.Add(exit);

        // A popup only closes on an outside click while its process is in the foreground,
        // so an invisible window is activated for as long as the menu is open.
        _menuHost ??= new Window
        {
            Width = 0,
            Height = 0,
            Left = -10000,
            Top = -10000,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = true,
        };
        _menuHost.Show();
        _menuHost.Activate();
        menu.Closed += (_, _) => _menuHost.Hide();
        menu.IsOpen = true;
    }

    /// <summary>Draws a dial: a 270° track with an accent arc for the volume.</summary>
    private static Drawing.Icon RenderDial(int size, bool connected, bool on, bool muted, double volume)
    {
        using var bitmap = new Drawing.Bitmap(size, size, Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);

            var stroke = Math.Max(2f, size * 0.16f);
            var inset = stroke / 2 + size * 0.04f;
            var rect = new Drawing.RectangleF(inset, inset, size - 2 * inset, size - 2 * inset);

            using var track = new Drawing.Pen(Drawing.Color.FromArgb(on ? 110 : 200, 150, 150, 150), stroke);
            track.StartCap = track.EndCap = Drawing.Drawing2D.LineCap.Round;
            g.DrawArc(track, rect, 135, 270);

            if (on)
            {
                var color = muted ? Drawing.Color.FromArgb(255, 150, 150, 150) : Drawing.Color.FromArgb(255, 255, 140, 30);
                using var arc = new Drawing.Pen(color, stroke);
                arc.StartCap = arc.EndCap = Drawing.Drawing2D.LineCap.Round;
                g.DrawArc(arc, rect, 135, (float)(270 * Math.Clamp(volume, 0.03, 1)));

                var dot = size * 0.22f;
                using var brush = new Drawing.SolidBrush(color);
                g.FillEllipse(brush, (size - dot) / 2, (size - dot) / 2, dot, dot);
            }

            if (!connected)
            {
                var dot = size * 0.42f;
                using var brush = new Drawing.SolidBrush(Drawing.Color.FromArgb(255, 220, 60, 50));
                g.FillEllipse(brush, size - dot, size - dot, dot, dot);
            }
        }

        var handle = bitmap.GetHicon();
        try
        {
            // Clone so the icon owns its own handle and the temporary one can be released.
            using var temporary = Drawing.Icon.FromHandle(handle);
            return (Drawing.Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
