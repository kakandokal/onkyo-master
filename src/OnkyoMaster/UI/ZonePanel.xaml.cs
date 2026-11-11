using System.Windows.Controls;
using System.Windows.Input;
using OnkyoMaster.Model;

namespace OnkyoMaster.UI;

/// <summary>Power, mute, volume and input chips for one zone.</summary>
public partial class ZonePanel : UserControl
{
    public ZonePanel() => InitializeComponent();

    private void Volume_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (DataContext is not ZoneState { Power: true } zone) return;
        zone.Volume.Nudge(e.Delta > 0 ? 1 : -1, 1);
        e.Handled = true;
    }
}
