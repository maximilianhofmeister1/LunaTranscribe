using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using LunaTranscribe.Core;
using LunaTranscribe.UI.MainWindow;
using LunaTranscribe.UI.Theme;

namespace LunaTranscribe.UI.Pages.Settings;

/// <summary>Einstellungen im Hauptfenster: Bereichs-Tabs oben, darunter eine ruhige Seite je Bereich.</summary>
public sealed class SettingsPage : UserControl
{
    private readonly AppState _state;
    private readonly ContentControl _section = new() { Focusable = false };
    private readonly RadioButton[] _tabs;

    public SettingsPage(AppState state)
    {
        _state = state;
        var tabs = new StackPanel { Orientation = Orientation.Horizontal };
        _tabs = new RadioButton[SettingsSectionExtensions.All.Length];
        for (var i = 0; i < _tabs.Length; i++)
        {
            var section = SettingsSectionExtensions.All[i];
            var tab = new RadioButton { Content = section.Title(), GroupName = "SettingsTabs", Tag = section, Margin = new Thickness(i == 0 ? 0 : 6, 0, 0, 0) }
                .Styled("Luna.Tab");
            tab.Checked += (_, _) => _state.Router.SettingsSection = section;
            _tabs[i] = tab;
            tabs.Children.Add(tab);
        }
        var tabBar = new Border { Padding = new Thickness(4), HorizontalAlignment = HorizontalAlignment.Left, Child = tabs };
        tabBar.SetBinding(Border.CornerRadiusProperty, new System.Windows.Data.Binding(nameof(ActualHeight))
            { RelativeSource = System.Windows.Data.RelativeSource.Self, Converter = CapsuleRadiusConverter.Instance });
        tabBar.Brushed(Border.BackgroundProperty, "Luna.SurfaceMuted");

        var body = new VStack { Spacing = 20 };
        body.Children.Add(tabBar);
        body.Children.Add(_section);
        Content = new LunaPage { Title = "Einstellungen", Content = body };

        Loaded += (_, _) => { _state.Router.PropertyChanged += OnRouter; Show(); };
        Unloaded += (_, _) => _state.Router.PropertyChanged -= OnRouter;
    }

    private void OnRouter(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainRouter.SettingsSection)) Show();
    }

    private void Show()
    {
        var current = _state.Router.SettingsSection;
        foreach (var tab in _tabs) tab.IsChecked = (SettingsSection)tab.Tag == current;
        _section.Content = current switch
        {
            SettingsSection.General => new GeneralSettings(_state),
            SettingsSection.Dictation => new DictationSettings(_state),
            SettingsSection.Refine => new RefineSettings(_state),
            SettingsSection.Meetings => new MeetingSettings(_state),
            SettingsSection.Models => new ModelSettings(_state),
            _ => new ComingSoonSettings(_state, current.Title()),
        };
    }
}

/// <summary>Vorläufiger Bereich bis zur Integration.</summary>
public sealed class ComingSoonSettings : SettingsSectionView
{
    private readonly string _title;
    public ComingSoonSettings(AppState state, string title) : base(state) => _title = title;
    protected override void Build(VStack root) => root.Children.Add(Card(_title, IconKind.Sliders, Hint("Dieser Bereich folgt in Kürze.")));
}
