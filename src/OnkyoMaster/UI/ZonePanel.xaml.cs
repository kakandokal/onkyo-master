using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OnkyoMaster.Model;

namespace OnkyoMaster.UI;

/// <summary>Power, mute, volume and input chips for one zone.</summary>
public partial class ZonePanel : UserControl
{
    public static readonly DependencyProperty ShowInputsProperty = DependencyProperty.Register(
        nameof(ShowInputs), typeof(bool), typeof(ZonePanel), new PropertyMetadata(true));

    public ZonePanel() => InitializeComponent();

    /// <summary>Whether the row of input chips is shown under the volume.</summary>
    public bool ShowInputs
    {
        get => (bool)GetValue(ShowInputsProperty);
        set => SetValue(ShowInputsProperty, value);
    }

    private void Volume_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (DataContext is not ZoneState { Power: true } zone) return;
        zone.Volume.Nudge(e.Delta > 0 ? 1 : -1, 1);
        e.Handled = true;
    }
}
