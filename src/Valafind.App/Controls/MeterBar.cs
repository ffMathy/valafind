using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Valafind.App.Controls;

/// <summary>A thin rounded progress / level bar (score bars, phase progress, CPU and disk meters), with an optional
/// target marker: a short tick across the bar at <see cref="Marker"/> (the level indexing steers towards).</summary>
public sealed class MeterBar : Control
{
    /// <summary>Defines <see cref="Value"/>.</summary>
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<MeterBar, double>(nameof(Value));

    /// <summary>Defines <see cref="Fill"/>.</summary>
    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<MeterBar, IBrush?>(nameof(Fill), Brushes.DodgerBlue);

    /// <summary>Defines <see cref="Track"/>.</summary>
    public static readonly StyledProperty<IBrush?> TrackProperty =
        AvaloniaProperty.Register<MeterBar, IBrush?>(nameof(Track));

    /// <summary>Defines <see cref="Radius"/>; negative means "half the height".</summary>
    public static readonly StyledProperty<double> RadiusProperty =
        AvaloniaProperty.Register<MeterBar, double>(nameof(Radius), -1);

    /// <summary>Defines <see cref="Marker"/>.</summary>
    public static readonly StyledProperty<double> MarkerProperty =
        AvaloniaProperty.Register<MeterBar, double>(nameof(Marker), double.NaN);

    /// <summary>Defines <see cref="MarkerBrush"/>.</summary>
    public static readonly StyledProperty<IBrush?> MarkerBrushProperty =
        AvaloniaProperty.Register<MeterBar, IBrush?>(nameof(MarkerBrush));

    /// <summary>How far the marker reaches above and below the bar, in pixels.</summary>
    internal const double MarkerOverhang = 3;

    /// <summary>Width of the marker, in pixels.</summary>
    internal const double MarkerWidth = 2;

    static MeterBar()
    {
        AffectsRender<MeterBar>(ValueProperty, FillProperty, TrackProperty, RadiusProperty, MarkerProperty, MarkerBrushProperty);
    }

    /// <summary>Position of the target marker, 0..1; NaN (the default) hides it.</summary>
    public double Marker
    {
        get => GetValue(MarkerProperty);
        set => SetValue(MarkerProperty, value);
    }

    /// <summary>Brush of the target marker (nothing is drawn without one).</summary>
    public IBrush? MarkerBrush
    {
        get => GetValue(MarkerBrushProperty);
        set => SetValue(MarkerBrushProperty, value);
    }

    /// <summary>Filled share, 0..1 (clamped).</summary>
    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>Brush of the filled part.</summary>
    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <summary>Brush of the track.</summary>
    public IBrush? Track
    {
        get => GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    /// <summary>Corner radius; negative = height / 2.</summary>
    public double Radius
    {
        get => GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize) => new(0, 4);

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0)
            return;
        var r = Radius < 0 ? h / 2 : Radius;
        if (Track is { } track)
            context.DrawRectangle(track, null, new Rect(0, 0, w, h), r, r);
        var v = double.IsNaN(Value) ? 0 : Math.Clamp(Value, 0, 1);
        if (v > 0 && Fill is { } fill)
        {
            var fw = w * v;
            // Clip to the rounded track so short fills keep the track's rounded start.
            using (context.PushGeometryClip(new RectangleGeometry(new Rect(0, 0, w, h), r, r)))
                context.DrawRectangle(fill, null, new Rect(0, 0, fw, h), Math.Min(r, fw / 2), r);
        }
        if (MarkerRect(w, h, Marker) is { } marker && MarkerBrush is { } markerBrush)
            context.DrawRectangle(markerBrush, null, marker, MarkerWidth / 2, MarkerWidth / 2);
    }

    /// <summary>Where the marker is drawn for a bar of <paramref name="width"/> × <paramref name="height"/>: a tick
    /// that sticks out above and below the bar (drawn outside the bounds, which controls do not clip), kept inside the
    /// bar horizontally. Null when <paramref name="marker"/> is NaN or out of range.</summary>
    internal static Rect? MarkerRect(double width, double height, double marker)
    {
        if (double.IsNaN(marker) || marker < 0 || marker > 1 || width <= MarkerWidth)
            return null;
        var x = Math.Clamp(width * marker - MarkerWidth / 2, 0, width - MarkerWidth);
        return new Rect(x, -MarkerOverhang, MarkerWidth, height + 2 * MarkerOverhang);
    }
}
