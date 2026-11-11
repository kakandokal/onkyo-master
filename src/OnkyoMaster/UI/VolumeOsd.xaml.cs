using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using OnkyoMaster.Model;

namespace OnkyoMaster.UI;

/// <summary>A small volume indicator above the taskbar that never takes focus or mouse input.</summary>
public partial class VolumeOsd : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000;

    private readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromSeconds(1.6) };

    public VolumeOsd()
    {
        InitializeComponent();
        _hide.Tick += (_, _) =>
        {
            _hide.Stop();
            Hide();
        };
    }

    public void ShowFor(ZoneState zone)
    {
        Glyph.Text = zone.Muted ? "" : "";
        Bar.Maximum = 100;
        Bar.Value = zone.Volume.Value;
        Bar.Opacity = zone.Muted ? 0.4 : 1;
        ValueText.Text = zone.Volume.Text;

        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Bottom - Height - 40;
        if (!IsVisible) Show();
        _hide.Stop();
        _hide.Start();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style | WsExTransparent | WsExToolWindow | WsExNoActivate));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
}
