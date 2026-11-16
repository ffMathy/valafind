using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Valafind.App.ViewModels;
using Valafind.Core.Abstractions;

namespace Valafind.App.Controls;

/// <summary>
/// File-type icon: an optional rounded tile with an outline glyph. Glyphs come from <c>Styles/Icons.axaml</c>
/// (<c>VfIconKind{Kind}</c>); tile and glyph colours come from the enclosing view's palette
/// (<c>VfKind{Palette}Bg</c> / <c>VfKind{Palette}Fg</c>) so each platform design and theme can colour them.
/// </summary>
public sealed class FileKindIcon : Control
{
    /// <summary>Defines <see cref="Kind"/>.</summary>
    public static readonly StyledProperty<FileKind> KindProperty =
        AvaloniaProperty.Register<FileKindIcon, FileKind>(nameof(Kind));

    /// <summary>Defines <see cref="GlyphSize"/>.</summary>
    public static readonly StyledProperty<double> GlyphSizeProperty =
        AvaloniaProperty.Register<FileKindIcon, double>(nameof(GlyphSize), 18);

    /// <summary>Defines <see cref="StrokeThickness"/>.</summary>
    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<FileKindIcon, double>(nameof(StrokeThickness), 1.6);

    /// <summary>Defines <see cref="CornerRadius"/>.</summary>
    public static readonly StyledProperty<double> CornerRadiusProperty =
        AvaloniaProperty.Register<FileKindIcon, double>(nameof(CornerRadius), 6);

    /// <summary>Defines <see cref="ShowTile"/>.</summary>
    public static readonly StyledProperty<bool> ShowTileProperty =
        AvaloniaProperty.Register<FileKindIcon, bool>(nameof(ShowTile), true);

    /// <summary>Defines <see cref="GlyphOverride"/>.</summary>
    public static readonly StyledProperty<IBrush?> GlyphOverrideProperty =
        AvaloniaProperty.Register<FileKindIcon, IBrush?>(nameof(GlyphOverride));

    private Geometry? _glyph;
    private IBrush? _tile;
    private IBrush? _ink;

    static FileKindIcon()
    {
        AffectsRender<FileKindIcon>(GlyphSizeProperty, StrokeThicknessProperty, CornerRadiusProperty, ShowTileProperty, GlyphOverrideProperty);
        KindProperty.Changed.AddClassHandler<FileKindIcon>((c, _) => c.Resolve());
    }

    /// <summary>File category to draw.</summary>
    public FileKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <summary>Glyph edge length in pixels.</summary>
    public double GlyphSize
    {
        get => GetValue(GlyphSizeProperty);
        set => SetValue(GlyphSizeProperty, value);
    }

    /// <summary>Stroke width in 24-unit view-box units.</summary>
    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    /// <summary>Tile corner radius.</summary>
    public double CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    /// <summary>Whether the coloured tile is drawn (macOS design uses bare glyphs).</summary>
    public bool ShowTile
    {
        get => GetValue(ShowTileProperty);
        set => SetValue(ShowTileProperty, value);
    }

    /// <summary>Forces the glyph colour (e.g. white on a selected row).</summary>
    public IBrush? GlyphOverride
    {
        get => GetValue(GlyphOverrideProperty);
        set => SetValue(GlyphOverrideProperty, value);
    }

    /// <inheritdoc />
    protected override void OnAttachedToLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        ResourcesChanged += OnResourcesChanged;
        ActualThemeVariantChanged += OnThemeChanged;
        Resolve();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        ResourcesChanged -= OnResourcesChanged;
        ActualThemeVariantChanged -= OnThemeChanged;
        base.OnDetachedFromLogicalTree(e);
    }

    private void OnResourcesChanged(object? sender, ResourcesChangedEventArgs e) => Resolve();

    private void OnThemeChanged(object? sender, EventArgs e) => Resolve();

    private void Resolve()
    {
        var palette = FileKindGroups.PaletteKey(Kind);
        _glyph = Find<Geometry>("VfIconKind" + Kind) ?? Find<Geometry>("VfIconKindOther");
        _tile = Find<IBrush>("VfKind" + palette + "Bg");
        _ink = Find<IBrush>("VfKind" + palette + "Fg") ?? Brushes.Gray;
        InvalidateVisual();
    }

    private T? Find<T>(string key) where T : class =>
        this.TryFindResource(key, ActualThemeVariant, out var value) ? value as T : null;

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize) => new(GlyphSize, GlyphSize);

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (ShowTile && _tile is not null)
            context.DrawRectangle(_tile, null, bounds, CornerRadius, CornerRadius);
        if (_glyph is null)
            return;
        var ink = GlyphOverride ?? _ink;
        if (ink is null)
            return;
        var size = Math.Min(GlyphSize, Math.Min(bounds.Width, bounds.Height));
        var scale = size / 24;
        var dx = (bounds.Width - size) / 2;
        var dy = (bounds.Height - size) / 2;
        var pen = new Pen(ink, StrokeThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(dx, dy)))
            context.DrawGeometry(null, pen, _glyph);
    }
}
