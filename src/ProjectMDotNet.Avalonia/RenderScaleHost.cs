using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ProjectMDotNet.Avalonia;

public class RenderScaleHost : Decorator
{
    public static readonly StyledProperty<double> RenderScaleProperty =
        AvaloniaProperty.Register<RenderScaleHost, double>(nameof(RenderScale), defaultValue: 1.0,
            coerce: (_, value) => double.IsFinite(value) ? Math.Clamp(value, 0.05, 1.0) : 1.0);

    static RenderScaleHost()
    {
        AffectsMeasure<RenderScaleHost>(RenderScaleProperty);
        AffectsArrange<RenderScaleHost>(RenderScaleProperty);
    }

    /// <inheritdoc cref="RenderScaleProperty"/>
    public double RenderScale
    {
        get => GetValue(RenderScaleProperty);
        set => SetValue(RenderScaleProperty, value);
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        var scale = RenderScale;
        Child?.Measure(new Size(availableSize.Width * scale, availableSize.Height * scale));
        var desired = Child?.DesiredSize ?? default;
        return new Size(desired.Width / scale, desired.Height / scale);
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child is { } child)
        {
            var scale = RenderScale;
            child.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Absolute);
            child.RenderTransform = scale >= 1.0 ? null : new ScaleTransform(1 / scale, 1 / scale);
            child.Arrange(new Rect(0, 0, finalSize.Width * scale, finalSize.Height * scale));
        }

        return finalSize;
    }
}
