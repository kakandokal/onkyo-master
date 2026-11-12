using System.Globalization;

namespace OnkyoMaster.Model;

/// <summary>
/// How a volume level is written out: as the receiver's absolute number or in decibels relative to
/// the reference level. The receiver's own "Volume Display" setting cannot be read over the network,
/// so the app has a matching setting of its own. Levels are always stored and sent as absolute values.
/// </summary>
public static class VolumeScale
{
    /// <summary>The absolute level that Onkyo receivers show as 0 dB.</summary>
    public const double Reference = 82;

    public static bool Decibels { get; set; }

    /// <param name="absolute">The level on the receiver's absolute scale.</param>
    /// <param name="halfSteps">Show the half-step digit, as receivers that count in 0.5 steps do.</param>
    public static string Format(double absolute, bool halfSteps)
    {
        var digits = halfSteps ? "0.0" : "0";
        if (!Decibels) return absolute.ToString(digits, CultureInfo.InvariantCulture);

        // The bottom of the scale is silence, which the receiver shows as minus infinity.
        if (absolute <= 0) return "−∞ дБ";
        return (absolute - Reference).ToString($"+{digits};−{digits};{digits}", CultureInfo.InvariantCulture) + " дБ";
    }
}
