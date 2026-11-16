using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Valafind.App.ViewModels;

namespace Valafind.App.Views;

/// <summary>How the launcher card is composited onto the desktop.</summary>
public enum LauncherSurface
{
    /// <summary>Per-pixel transparency: rounded card with drop shadow floating on the desktop.</summary>
    Floating,
    /// <summary>The OS blurs the whole window (acrylic / mica / vibrancy): edge-to-edge translucent card.</summary>
    Blurred,
    /// <summary>No transparency available: square, fully opaque card.</summary>
    Opaque,
}

/// <summary>
/// Shared behaviour of the platform launcher views: search-box focus, row selection by pointer, keeping the
/// selection scrolled into view and switching surface styles (<c>blurred</c> / <c>opaque</c> classes).
/// Views name their search box <c>SearchBox</c> and their results <see cref="ItemsControl"/> <c>ResultsList</c>.
/// </summary>
public abstract class LauncherViewBase : UserControl
{
    private LauncherViewModel? _vm;

    /// <summary>The bound launcher view model.</summary>
    protected LauncherViewModel? ViewModel => _vm;

    /// <summary>Moves keyboard focus to the search box and selects its text.</summary>
    public void FocusSearchBox()
    {
        if (this.FindControl<TextBox>("SearchBox") is not { } box)
            return;
        box.Focus();
        box.SelectAll();
    }

    /// <summary>True when the search box has a non-empty text selection (Ctrl+C then copies text, not the path).</summary>
    public bool SearchBoxHasSelection =>
        this.FindControl<TextBox>("SearchBox") is { } box && box.SelectionStart != box.SelectionEnd;

    /// <summary>Applies the surface style classes.</summary>
    public void SetSurface(LauncherSurface surface)
    {
        Classes.Set("blurred", surface == LauncherSurface.Blurred);
        Classes.Set("opaque", surface == LauncherSurface.Opaque);
    }

    /// <inheritdoc />
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (this.FindControl<ItemsControl>("ResultsList") is { } list)
        {
            list.AddHandler(PointerPressedEvent, OnRowPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
            list.AddHandler(DoubleTappedEvent, OnRowDoubleTapped, RoutingStrategies.Bubble);
        }
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm = DataContext as LauncherViewModel;
        if (_vm is not null)
            _vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LauncherViewModel.SelectedIndex))
            Dispatcher.UIThread.Post(ScrollSelectionIntoView, DispatcherPriority.Background);
    }

    private void ScrollSelectionIntoView()
    {
        if (_vm is null || _vm.SelectedIndex < 0 || this.FindControl<ItemsControl>("ResultsList") is not { } list)
            return;
        list.ContainerFromIndex(_vm.SelectedIndex)?.BringIntoView();
    }

    private static ResultItemViewModel? ItemFrom(object? source) =>
        (source as Visual)?.GetSelfAndVisualAncestors()
            .OfType<StyledElement>()
            .Select(v => v.DataContext)
            .OfType<ResultItemViewModel>()
            .FirstOrDefault();

    private void OnRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Buttons inside a row handle their own clicks; only plain row presses select.
        if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return;
        if (_vm is not null && ItemFrom(e.Source) is { } item)
            _vm.Select(item);
    }

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return;
        if (_vm is not null && ItemFrom(e.Source) is { } item)
        {
            _vm.Select(item);
            _vm.OpenSelectedCommand.Execute(null);
            e.Handled = true;
        }
    }
}
