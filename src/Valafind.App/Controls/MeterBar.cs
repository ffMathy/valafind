using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Valafind.App.Controls;

/// <summary>A thin rounded progress / level bar (score bars, phase progress, CPU and disk meters).</summary>
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

    static MeterBar()
    {
        AffectsRender<MeterBar>(ValueProperty, FillProperty, TrackProperty, RadiusProperty);
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
        if (v <= 0 || Fill is not { } fill)
            return;
        var fw = w * v;
        // Clip to the rounded track so short fills keep the track's rounded start.
        using (context.PushGeometryClip(new RectangleGeometry(new Rect(0, 0, w, h), r, r)))
            context.DrawRectangle(fill, null, new Rect(0, 0, fw, h), Math.Min(r, fw / 2), r);
    }
}
