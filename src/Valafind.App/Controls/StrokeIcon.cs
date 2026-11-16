using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Valafind.App.Controls;

/// <summary>
/// Draws an outline icon the way an SVG with <c>fill="none" stroke="currentColor"</c> does: the geometry is defined in a
/// square view box (24 units by default) and scaled together with its stroke to the control size.
/// </summary>
public sealed class StrokeIcon : Control
{
    /// <summary>Defines <see cref="Data"/>.</summary>
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<StrokeIcon, Geometry?>(nameof(Data));

    /// <summary>Defines <see cref="Foreground"/> (inherited like text colour).</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<StrokeIcon>();

    /// <summary>Defines <see cref="StrokeThickness"/>.</summary>
    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<StrokeIcon, double>(nameof(StrokeThickness), 1.6);

    /// <summary>Defines <see cref="ViewBoxSize"/>.</summary>
    public static readonly StyledProperty<double> ViewBoxSizeProperty =
        AvaloniaProperty.Register<StrokeIcon, double>(nameof(ViewBoxSize), 24);

    static StrokeIcon()
    {
        AffectsRender<StrokeIcon>(DataProperty, ForegroundProperty, StrokeThicknessProperty, ViewBoxSizeProperty);
    }

    /// <summary>Outline geometry in view-box units.</summary>
    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    /// <summary>Stroke colour.</summary>
    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>Stroke width in view-box units (SVG <c>stroke-width</c>).</summary>
    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    /// <summary>Size of the square view box the geometry is authored in.</summary>
    public double ViewBoxSize
    {
        get => GetValue(ViewBoxSizeProperty);
        set => SetValue(ViewBoxSizeProperty, value);
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize) => new(16, 16);

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        if (Data is not { } data || Foreground is not { } brush || ViewBoxSize <= 0)
            return;
        var side = Math.Min(Bounds.Width, Bounds.Height);
        if (side <= 0)
            return;
        var scale = side / ViewBoxSize;
        var dx = (Bounds.Width - side) / 2;
        var dy = (Bounds.Height - side) / 2;
        var pen = new Pen(brush, StrokeThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(dx, dy)))
            context.DrawGeometry(null, pen, data);
    }
}
