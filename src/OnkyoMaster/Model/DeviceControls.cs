using System.Windows.Threading;

namespace OnkyoMaster.Model;

/// <summary>One value of a setting. <paramref name="Short"/> is a compact label for switch segments.</summary>
public sealed record Option(string Code, string Label, string? Short = null)
{
    public string ShortLabel => Short ?? Label;
}

/// <summary>A receiver setting with a fixed list of values, e.g. the display dimmer.</summary>
public sealed class ChoiceControl(string command, IReadOnlyList<Option> options, Action<string, string?> send) : ObservableObject
{
    private string? _value;
    private string? _deviceValue;

    /// <summary>The ISCP command name; a value is set by sending Command + option code.</summary>
    public string Command => command;

    public IReadOnlyList<Option> Options { get; } = options;

    /// <summary>Selects the option after the current one, wrapping around.</summary>
    public void Cycle()
    {
        if (Options.Count == 0) return;
        var index = -1;
        for (var i = 0; i < Options.Count; i++)
        {
            if (Options[i].Code == _value) index = i;
        }
        Value = Options[(index + 1) % Options.Count].Code;
    }

    public string? Value
    {
        get => _value;
        set
        {
            if (string.IsNullOrEmpty(value) || value == _value) return;
            _value = value;
            Raise();
            Raise(nameof(Label));
            send(command + value, null);
        }
    }

    public string Label => Options.FirstOrDefault(o => o.Code == _value)?.Label ?? _value ?? "";

    public void Apply(string code)
    {
        _deviceValue = code;
        if (code == _value) return;
        _value = code;
        Raise(nameof(Value));
        Raise(nameof(Label));
    }

    /// <summary>The receiver refused the last change ("N/A"): show again what it actually has.</summary>
    public void Revert()
    {
        if (_deviceValue is { } code) Apply(code);
    }
}

/// <summary>An on/off receiver setting ("00"/"01").</summary>
public sealed class ToggleControl(string command, Action<string, string?> send) : ObservableObject
{
    private bool _value;
    private bool? _deviceValue;

    public bool Value
    {
        get => _value;
        set
        {
            if (!Set(ref _value, value)) return;
            send(command + (value ? "01" : "00"), null);
        }
    }

    public void Apply(string code)
    {
        _deviceValue = code != "00";
        Set(ref _value, code != "00", nameof(Value));
    }

    /// <summary>The receiver refused the last change ("N/A"): show again what it actually has.</summary>
    public void Revert()
    {
        if (_deviceValue is { } value) Set(ref _value, value, nameof(Value));
    }
}

/// <summary>
/// A numeric receiver setting driven by a slider: volume, tone, channel levels.
/// While the user is changing it, values echoed back by the receiver are held so the slider does not jump.
/// </summary>
public sealed class LevelControl : ObservableObject
{
    private readonly string _coalesceKey;
    private readonly Func<double, string> _encode;
    private readonly Func<double, string> _format;
    private readonly Action<string, string?> _send;
    private readonly DispatcherTimer _hold;
    private double _value;
    private double _minimum;
    private double _maximum;
    private double _step;
    private double? _heldDeviceValue;
    private double? _deviceValue;
    private bool _isKnown;

    public LevelControl(string title, string coalesceKey, double minimum, double maximum, double step,
        Func<double, string> encode, Func<double, string> format, Action<string, string?> send)
    {
        Title = title;
        _coalesceKey = coalesceKey;
        _minimum = minimum;
        _maximum = maximum;
        _step = step;
        _encode = encode;
        _format = format;
        _send = send;
        _hold = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _hold.Tick += (_, _) =>
        {
            _hold.Stop();
            if (_heldDeviceValue is not { } held) return;
            _heldDeviceValue = null;
            Commit(held);
        };
    }

    /// <summary>Raised whenever the value changes, by the user or by the receiver.</summary>
    public event Action? Changed;

    public string Title { get; }
    public double Minimum { get => _minimum; set => Set(ref _minimum, value); }
    public double Maximum { get => _maximum; set => Set(ref _maximum, value); }
    public double Step { get => _step; set => Set(ref _step, value); }

    /// <summary>False until the receiver has reported a value; user changes are ignored until then.</summary>
    public bool IsKnown => _isKnown;

    public string Text => _isKnown ? _format(_value) : "—";

    /// <summary>For when the way the value is written out has changed, not the value itself.</summary>
    public void RefreshText() => Raise(nameof(Text));

    public double Value
    {
        get => _value;
        set => SetByUser(value);
    }

    /// <summary>Moves the value by a number of steps; never raises a value that is already above the maximum.</summary>
    public void Nudge(int steps, double? step = null)
    {
        if (!_isKnown) return;
        if (steps > 0 && _value >= _maximum) return;
        SetByUser(_value + steps * (step ?? _step));
    }

    public void ApplyFromDevice(double value)
    {
        _deviceValue = value;
        if (_hold.IsEnabled)
        {
            _heldDeviceValue = value;
            return;
        }
        Commit(value);
    }

    /// <summary>The receiver refused the last change ("N/A"): show again what it actually has.</summary>
    public void Revert()
    {
        if (_deviceValue is not { } value) return;
        _hold.Stop();
        _heldDeviceValue = null;
        Commit(value);
    }

    private void SetByUser(double value)
    {
        if (!_isKnown) return;
        var snapped = Math.Clamp(Math.Round(value / _step) * _step, _minimum, _maximum);
        if (snapped == _value) return;
        _value = snapped;
        Raise(nameof(Value));
        Raise(nameof(Text));
        _heldDeviceValue = null;
        _hold.Stop();
        _hold.Start();
        _send(_encode(snapped), _coalesceKey);
        Changed?.Invoke();
    }

    private void Commit(double value)
    {
        var changed = !_isKnown || value != _value;
        if (!changed) return;
        _isKnown = true;
        _value = value;
        Raise(nameof(Value));
        Raise(nameof(Text));
        Raise(nameof(IsKnown));
        Changed?.Invoke();
    }
}
