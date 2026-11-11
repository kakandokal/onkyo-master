namespace OnkyoMaster.Model;

/// <summary>LMD codes from the ISCP specification that apply to current Onkyo receivers.</summary>
public static class ListeningModes
{
    /// <summary>Modes offered for direct selection.</summary>
    public static readonly IReadOnlyList<Option> Selectable =
    [
        new("00", "Stereo"),
        new("01", "Direct"),
        new("11", "Pure Audio"),
        new("0C", "All Ch Stereo"),
        new("80", "Dolby Surround"),
        new("82", "DTS Neural:X"),
        new("17", "DTS Virtual:X"),
        new("40", "Straight Decode"),
        new("42", "THX Cinema"),
        new("44", "THX Music"),
        new("45", "THX Games"),
        new("84", "Dolby Surround + THX Cinema"),
        new("8B", "Dolby Surround + THX Music"),
        new("89", "Dolby Surround + THX Games"),
        new("85", "DTS Neural:X + THX Cinema"),
        new("8C", "DTS Neural:X + THX Music"),
        new("8A", "DTS Neural:X + THX Games"),
        new("08", "Orchestra"),
        new("09", "Unplugged"),
        new("0A", "Studio-Mix"),
        new("0B", "TV Logic"),
        new("0D", "Theater-Dimensional"),
        new("03", "Game-RPG"),
        new("05", "Game-Action"),
        new("06", "Game-Rock"),
        new("0E", "Game-Sports"),
        new("0F", "Mono"),
        new("13", "Full Mono"),
    ];

    private static readonly Dictionary<string, string> Names = Selectable.ToDictionary(o => o.Code, o => o.Label);

    public static string NameOf(string code) =>
        Names.TryGetValue(code, out var name) ? name : $"Режим {code}";
}
