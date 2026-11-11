using System.Globalization;
using System.Xml.Linq;

namespace OnkyoMaster.Protocol;

public sealed record ZoneInfo(int Id, string Name, bool Enabled, int VolumeMax, int VolumeStep);
public sealed record SelectorInfo(string Id, string Name, int ZoneMask, bool Enabled);
public sealed record NetServiceInfo(string Id, string Name, int ZoneMask, bool Enabled);
public sealed record ControlInfo(string Id, bool Enabled, double? Min, double? Max, string? Code);
public sealed record PresetInfo(int Number, int Band, int Frequency, string Name);

/// <summary>The receiver's self-description, returned as XML by the NRI query.</summary>
public sealed class ReceiverInfo
{
    public string Model { get; private set; } = "";
    public string Brand { get; private set; } = "";
    public string Firmware { get; private set; } = "";
    public string Id { get; private set; } = "";
    public List<ZoneInfo> Zones { get; } = [];
    public List<SelectorInfo> Selectors { get; } = [];
    public List<NetServiceInfo> NetServices { get; } = [];
    public List<PresetInfo> Presets { get; } = [];
    public Dictionary<string, ControlInfo> Controls { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool HasControl(string id) => Controls.TryGetValue(id, out var control) && control.Enabled;

    public static ReceiverInfo? Parse(string xml)
    {
        try
        {
            var start = xml.IndexOf('<');
            if (start < 0) return null;
            var device = XDocument.Parse(xml[start..]).Root?.Element("device");
            if (device is null) return null;

            var info = new ReceiverInfo
            {
                Model = (string?)device.Element("model") ?? (string?)device.Attribute("id") ?? "",
                Brand = (string?)device.Element("brand") ?? "",
                Firmware = (string?)device.Element("firmwareversion") ?? "",
                Id = (string?)device.Element("macaddress") ?? "",
            };

            foreach (var e in device.Element("zonelist")?.Elements("zone") ?? [])
            {
                info.Zones.Add(new ZoneInfo(Int(e, "id"), Str(e, "name"), Str(e, "value") != "0", Int(e, "volmax"), Int(e, "volstep")));
            }
            foreach (var e in device.Element("selectorlist")?.Elements("selector") ?? [])
            {
                info.Selectors.Add(new SelectorInfo(Str(e, "id").ToUpperInvariant(), Str(e, "name"), Hex(e, "zone"), Str(e, "value") != "0"));
            }
            foreach (var e in device.Element("netservicelist")?.Elements("netservice") ?? [])
            {
                info.NetServices.Add(new NetServiceInfo(Str(e, "id").ToUpperInvariant(), Str(e, "name"), Hex(e, "zone"), Str(e, "value") != "0"));
            }
            foreach (var e in device.Element("presetlist")?.Elements("preset") ?? [])
            {
                info.Presets.Add(new PresetInfo(Hex(e, "id"), Int(e, "band"), Int(e, "freq"), Str(e, "name")));
            }
            foreach (var e in device.Element("controllist")?.Elements("control") ?? [])
            {
                var id = Str(e, "id");
                if (id.Length == 0) continue;
                info.Controls[id] = new ControlInfo(id, Str(e, "value") != "0", Num(e, "min"), Num(e, "max"), (string?)e.Attribute("code"));
            }
            return info;
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or FormatException)
        {
            return null;
        }
    }

    private static string Str(XElement e, string name) => ((string?)e.Attribute(name) ?? "").Trim();

    private static int Int(XElement e, string name) =>
        int.TryParse(Str(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static int Hex(XElement e, string name) =>
        int.TryParse(Str(e, name), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static double? Num(XElement e, string name) =>
        double.TryParse(Str(e, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
}
