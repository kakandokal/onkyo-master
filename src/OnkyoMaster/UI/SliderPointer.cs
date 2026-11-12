using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace OnkyoMaster.UI;

/// <summary>
/// Makes a slider go to the point where it is pressed and follow the pointer from there.
/// A stock WPF slider pressed beside its thumb moves by LargeChange, waits, and then repeats that step
/// until it has passed the pointer; for a volume control that is a late, jerky and imprecise answer.
/// </summary>
public static class SliderPointer
{
    public static readonly DependencyProperty FollowProperty = DependencyProperty.RegisterAttached(
        "Follow", typeof(bool), typeof(SliderPointer), new PropertyMetadata(false, OnFollowChanged));

    public static bool GetFollow(Slider slider) => (bool)slider.GetValue(FollowProperty);

    public static void SetFollow(Slider slider, bool value) => slider.SetValue(FollowProperty, value);

    private static void OnFollowChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not Slider slider) return;
        slider.PreviewMouseLeftButtonDown -= OnDown;
        slider.PreviewMouseMove -= OnMove;
        slider.PreviewMouseLeftButtonUp -= OnUp;
        if (e.NewValue is not true) return;
        slider.PreviewMouseLeftButtonDown += OnDown;
        slider.PreviewMouseMove += OnMove;
        slider.PreviewMouseLeftButtonUp += OnUp;
    }

    private static void OnDown(object sender, MouseButtonEventArgs e)
    {
        var slider = (Slider)sender;
        if (FindTrack(slider) is not { } track) return;
        // Pressed on the thumb itself: the stock drag already follows the pointer.
        if (track.Thumb is { IsMouseOver: true }) return;
        if (!slider.CaptureMouse()) return;

        slider.Focus();
        MoveTo(slider, track, e.GetPosition(track));
        // Handled, so that the track's own buttons do not start stepping as well.
        e.Handled = true;
    }

    private static void OnMove(object sender, MouseEventArgs e)
    {
        // Only while this class holds the capture; during a stock thumb drag the thumb holds it.
        var slider = (Slider)sender;
        if (!slider.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
        if (FindTrack(slider) is { } track) MoveTo(slider, track, e.GetPosition(track));
    }

    private static void OnUp(object sender, MouseButtonEventArgs e)
    {
        var slider = (Slider)sender;
        if (slider.IsMouseCaptured) slider.ReleaseMouseCapture();
    }

    private static void MoveTo(Slider slider, Track track, Point point)
    {
        var value = track.ValueFromPoint(point);
        if (!double.IsFinite(value)) return;
        // SetCurrentValue keeps the binding and writes through it, as the slider's own drag does.
        slider.SetCurrentValue(RangeBase.ValueProperty, Math.Clamp(value, slider.Minimum, slider.Maximum));
    }

    private static Track? FindTrack(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Track track) return track;
            if (FindTrack(child) is { } nested) return nested;
        }
        return null;
    }
}
