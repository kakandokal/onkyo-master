using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OnkyoMaster.Model;
using OnkyoMaster.Services;

namespace OnkyoMaster.UI;

/// <summary>A row of the hotkey list: one action and the key combination stored for its command.</summary>
public sealed class HotkeyRowView(HotkeyAction action, AppSettings settings, bool canDelete) : ObservableObject
{
    private bool _failed;
    private bool _isVisible = true;

    public string Title => action.Title;
    public string Command => action.Command;

    /// <summary>User-defined commands can be removed from the list.</summary>
    public bool CanDelete => canDelete;

    public string Gesture
    {
        get => settings.HotkeyFor(Command);
        set
        {
            settings.SetHotkey(Command, value);
            Refresh();
        }
    }

    public bool HasGesture => Gesture.Length > 0;

    /// <summary>Windows refused the combination: another program holds it.</summary>
    public bool Failed { get => _failed; set => Set(ref _failed, value); }

    public bool IsVisible { get => _isVisible; set => Set(ref _isVisible, value); }

    /// <summary>Re-reads the gesture from settings; rows that share a command change together.</summary>
    public void Refresh()
    {
        Raise(nameof(Gesture));
        Raise(nameof(HasGesture));
    }
}

public sealed class HotkeyGroupView(string title, IEnumerable<HotkeyRowView> rows) : ObservableObject
{
    private bool _isExpanded;
    private bool _isVisible = true;

    public string Title { get; } = title;
    public IReadOnlyList<HotkeyRowView> Rows { get; } = rows.ToList();
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }
    public bool IsVisible { get => _isVisible; set => Set(ref _isVisible, value); }
    public int AssignedCount => Rows.Count(r => r.HasGesture);
    public string Summary => AssignedCount > 0 ? $"назначено: {AssignedCount}" : "";

    public void Refresh() => Raise(nameof(Summary));
}

/// <summary>The "Hotkeys" tab: every action the app knows, grouped and searchable, each with a key combination box.</summary>
public partial class HotkeysPage : UserControl
{
    private readonly ObservableCollection<HotkeyGroupView> _groups = [];
    private Receiver? _receiver;
    private AppSettings? _settings;
    private bool _wasFiltering;

    public HotkeysPage()
    {
        InitializeComponent();
        GroupsList.ItemsSource = _groups;
    }

    public void Attach(Receiver receiver, AppSettings settings)
    {
        _receiver = receiver;
        _settings = settings;
        MediaKeysBox.IsChecked = settings.MediaKeys;

        // Inputs, services and presets come with the receiver's description; scenes and custom commands are the user's.
        receiver.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Receiver.Info)) Rebuild();
        };
        settings.Scenes.CollectionChanged += (_, _) => Rebuild();
        settings.CustomCommands.CollectionChanged += (_, _) => Rebuild();
        Rebuild();
    }

    private IEnumerable<HotkeyRowView> AllRows => _groups.SelectMany(g => g.Rows);

    private void Rebuild()
    {
        if (_receiver is null || _settings is null) return;
        var expanded = _groups.ToDictionary(g => g.Title, g => g.IsExpanded);
        _groups.Clear();
        foreach (var group in HotkeyCatalog.Build(_receiver, _settings))
        {
            var canDelete = group.Title == HotkeyCatalog.CustomGroup;
            var view = new HotkeyGroupView(group.Title, group.Actions.Select(a => new HotkeyRowView(a, _settings, canDelete)));
            // A new group starts open only if it has something to show.
            view.IsExpanded = expanded.TryGetValue(group.Title, out var wasExpanded) ? wasExpanded : view.AssignedCount > 0;
            _groups.Add(view);
        }
        RefreshState();
        ApplyFilter();
    }

    /// <summary>Updates everything derived from the assignments: failure marks, group counters, the total.</summary>
    private void RefreshState()
    {
        var failed = App.Current.FailedHotkeys;
        foreach (var row in AllRows) row.Failed = row.HasGesture && failed.Contains(row.Command);
        foreach (var group in _groups) group.Refresh();
        var total = _settings?.Hotkeys.Count ?? 0;
        SummaryText.Text = total == 0 ? "Сочетаний не назначено" : $"Назначено сочетаний: {total}";
    }

    private void ApplyFilter()
    {
        var text = SearchBox.Text.Trim();
        var assignedOnly = AssignedOnlyBox.IsChecked == true;
        var filtering = text.Length > 0 || assignedOnly;
        foreach (var group in _groups)
        {
            var groupMatches = text.Length > 0 && group.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase);
            foreach (var row in group.Rows)
            {
                var matches = text.Length == 0 || groupMatches
                    || row.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                    || row.Gesture.Contains(text, StringComparison.CurrentCultureIgnoreCase);
                row.IsVisible = matches && (!assignedOnly || row.HasGesture);
            }
            group.IsVisible = group.Rows.Any(r => r.IsVisible);
            // While filtering, open the groups with matches; when the filter is cleared,
            // go back to "open if it has hotkeys".
            if (filtering) group.IsExpanded = group.IsVisible;
            else if (_wasFiltering) group.IsExpanded = group.AssignedCount > 0;
        }
        _wasFiltering = filtering;
    }

    private void Assign(HotkeyRowView row, string gesture)
    {
        if (_settings is null || gesture == row.Gesture) return;
        string? note = null;
        if (gesture.Length > 0)
        {
            // Windows gives a combination to one handler only, so take it away from whoever had it.
            var previous = AllRows.FirstOrDefault(r => r.Command != row.Command && r.Gesture == gesture);
            if (previous is not null)
            {
                _settings.SetHotkey(previous.Command, "");
                note = $"Сочетание {gesture} снято с действия «{previous.Title}».";
            }
        }
        _settings.SetHotkey(row.Command, gesture);
        App.Current.ApplySettings();
        ShowNote(note);

        if (_groups.Any(g => g.Title == HotkeyCatalog.UnknownGroup))
        {
            Rebuild(); // a cleared hotkey for a vanished action should leave the list
            return;
        }
        foreach (var each in AllRows) each.Refresh();
        RefreshState();
        if (AssignedOnlyBox.IsChecked == true) ApplyFilter();
    }

    private void ShowNote(string? text)
    {
        NoteText.Text = text ?? "";
        NoteText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Hotkey_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is TextBox { Tag: HotkeyRowView row } && HotkeyManager.Capture(e) is { } gesture) Assign(row, gesture);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: HotkeyRowView row }) Assign(row, "");
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (_settings is not null) ApplyFilter();
    }

    private void MediaKeys_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        _settings.MediaKeys = MediaKeysBox.IsChecked == true;
        App.Current.ApplySettings();
    }

    private void AddCustom_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        var command = CustomCommandBox.Text.Trim();
        if (command.Length < 3)
        {
            ShowNote("Команда ISCP состоит минимум из трёх символов, например DIMDIM или SLI05.");
            return;
        }
        var name = CustomNameBox.Text.Trim();
        _settings.CustomCommands.Add(new CustomCommand { Name = name.Length > 0 ? name : command, Command = command });
        CustomNameBox.Clear();
        CustomCommandBox.Clear();
        App.Current.ApplySettings();
        ShowNote(null);

        if (_groups.FirstOrDefault(g => g.Title == HotkeyCatalog.CustomGroup) is { } group) group.IsExpanded = true;
    }

    private void CustomBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddCustom_Click(sender, e);
    }

    private void DeleteCustom_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null || sender is not FrameworkElement { Tag: HotkeyRowView row }) return;
        var custom = _settings.CustomCommands.FirstOrDefault(c => "raw:" + c.Command == row.Command);
        if (custom is null) return;
        // Drop the hotkey with the command, unless another row (a twin entry, a built-in action) runs the same command.
        if (AllRows.Count(r => r.Command == row.Command) == 1) _settings.SetHotkey(row.Command, "");
        _settings.CustomCommands.Remove(custom);
        App.Current.ApplySettings();
        RefreshState();
    }
}
