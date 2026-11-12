using System.Collections.ObjectModel;
using System.Globalization;
using OnkyoMaster.Protocol;

namespace OnkyoMaster.Model;

public sealed record InputOption(string Id, string Name);

/// <summary>Power, volume, mute and input of one zone. Main, Zone 2 and Zone 3 differ only in command names.</summary>
public sealed class ZoneState : ObservableObject
{
    private readonly Receiver _owner;
    private readonly string _pwr;
    private readonly string _vol;
    private readonly string _mute;
    private readonly string _sel;
    private int _scale = 1;
    private int? _rawVolume;
    private bool _configured;
    private bool _power;
    private bool _muted;
    private bool _isAvailable = true;
    private string? _inputId;

    public ZoneState(Receiver owner, string name, int bit, string pwr, string vol, string mute, string sel)
    {
        _owner = owner;
        Name = name;
        Bit = bit;
        _pwr = pwr;
        _vol = vol;
        _mute = mute;
        _sel = sel;
        Volume = new LevelControl(name, vol, 0, 100, 1,
            v => $"{_vol}{(int)Math.Round(v * _scale):X2}",
            v => VolumeScale.Format(v, halfSteps: _scale > 1),
            owner.Send);
    }

    /// <summary>Re-reads the volume text after the display scale has changed.</summary>
    internal void RefreshVolumeText()
    {
        Volume.RefreshText();
        // Listeners that show the volume outside of bindings (the tray icon) watch the zone, not the control.
        Raise(nameof(Volume));
    }

    public string Name { get; }

    /// <summary>This zone's bit in the zone masks of the receiver description (1, 2, 4).</summary>
    public int Bit { get; }

    public LevelControl Volume { get; }
    public ObservableCollection<InputOption> Inputs { get; } = [];

    /// <summary>Power as last reported by the receiver, as opposed to <see cref="Power"/> which is set optimistically.</summary>
    public bool DevicePower { get; private set; }

    public bool IsAvailable { get => _isAvailable; private set => Set(ref _isAvailable, value); }

    public bool Power
    {
        get => _power;
        set
        {
            if (!Set(ref _power, value)) return;
            _owner.Send(_pwr + (value ? "01" : "00"));
            _owner.Query(_pwr + "QSTN");
        }
    }

    public bool Muted
    {
        get => _muted;
        set
        {
            if (!Set(ref _muted, value)) return;
            _owner.Send(_mute + (value ? "01" : "00"));
            _owner.Query(_mute + "QSTN");
        }
    }

    public string? InputId => _inputId;

    public string InputName => Inputs.FirstOrDefault(i => i.Id == _inputId)?.Name ?? _inputId ?? "";

    public InputOption? SelectedInput
    {
        get => Inputs.FirstOrDefault(i => i.Id == _inputId);
        set
        {
            // The list control writes null back while the list is being rebuilt; that is not a user choice.
            if (value is null || value.Id == _inputId) return;
            SelectInput(value.Id);
        }
    }

    public void SelectInput(string id)
    {
        SetInput(id);
        _owner.Send(_sel + id);
        _owner.Query(_sel + "QSTN");
    }

    /// <summary>Applies the zone's part of the receiver description.</summary>
    internal void Configure(ZoneInfo? zone, IReadOnlyList<InputOption> inputs, double volumeLimit)
    {
        IsAvailable = zone?.Enabled ?? false;
        // volstep 0 means the receiver counts volume in half steps: raw 0x5E is shown as 47.0.
        _scale = zone is { VolumeStep: 0 } ? 2 : 1;
        Volume.Step = 1.0 / _scale;
        var deviceMax = zone is { VolumeMax: > 0 } ? zone.VolumeMax : 100;
        Volume.Maximum = Math.Min(volumeLimit, deviceMax);
        _configured = true;

        if (!Inputs.SequenceEqual(inputs))
        {
            Inputs.Clear();
            foreach (var input in inputs) Inputs.Add(input);
            Raise(nameof(SelectedInput));
            Raise(nameof(InputName));
        }

        if (_rawVolume is { } raw) Volume.ApplyFromDevice((double)raw / _scale);
    }

    internal bool Handle(string command, string arg)
    {
        if (command == _pwr)
        {
            DevicePower = arg != "00";
            Set(ref _power, DevicePower, nameof(Power));
            return true;
        }
        if (command == _mute)
        {
            Set(ref _muted, arg == "01", nameof(Muted));
            return true;
        }
        if (command == _sel)
        {
            SetInput(arg.ToUpperInvariant());
            return true;
        }
        if (command == _vol)
        {
            if (int.TryParse(arg, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var raw))
            {
                _rawVolume = raw;
                // Without the description the scale is unknown, and guessing it could double the volume.
                if (_configured) Volume.ApplyFromDevice((double)raw / _scale);
            }
            return true;
        }
        return false;
    }

    private void SetInput(string id)
    {
        if (id == _inputId) return;
        _inputId = id;
        Raise(nameof(InputId));
        Raise(nameof(InputName));
        Raise(nameof(SelectedInput));
    }
}
