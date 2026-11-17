using Avalonia;
using Avalonia.Controls;

namespace Valafind.App.Views.Common;

/// <summary>Index progress (Phase 1, Phase 2, live state with CPU/disk pressure), shared by all launchers.</summary>
public partial class IndexPanelView : UserControl
{
    /// <summary>Whether the panel shows its own "Index Now / Resume" button while indexing is paused. Views that already
    /// show a paused banner with that button (GNOME) turn it off.</summary>
    public static readonly StyledProperty<bool> ShowResumeButtonProperty =
        AvaloniaProperty.Register<IndexPanelView, bool>(nameof(ShowResumeButton), defaultValue: true);

    /// <summary>Creates the view.</summary>
    public IndexPanelView() => InitializeComponent();

    /// <inheritdoc cref="ShowResumeButtonProperty"/>
    public bool ShowResumeButton
    {
        get => GetValue(ShowResumeButtonProperty);
        set => SetValue(ShowResumeButtonProperty, value);
    }
}
