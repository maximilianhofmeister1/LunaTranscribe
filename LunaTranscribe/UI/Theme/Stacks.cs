using System;
using System.Windows;
using System.Windows.Controls;

namespace LunaTranscribe.UI.Theme;

/// <summary>StackPanel mit festem Abstand zwischen sichtbaren Kindern (SwiftUI: VStack/HStack(spacing:)).</summary>
public class SpacedStack : Panel
{
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(SpacedStack),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(
        nameof(Orientation), typeof(Orientation), typeof(SpacedStack),
        new FrameworkPropertyMetadata(Orientation.Vertical, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }
    public Orientation Orientation { get => (Orientation)GetValue(OrientationProperty); set => SetValue(OrientationProperty, value); }

    protected override Size MeasureOverride(Size available)
    {
        var vertical = Orientation == Orientation.Vertical;
        var childAvailable = vertical ? new Size(available.Width, double.PositiveInfinity) : new Size(double.PositiveInfinity, available.Height);
        double main = 0, cross = 0;
        var count = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            child.Measure(childAvailable);
            var d = child.DesiredSize;
            if (count > 0) main += Spacing;
            main += vertical ? d.Height : d.Width;
            cross = Math.Max(cross, vertical ? d.Width : d.Height);
            count++;
        }
        return vertical ? new Size(cross, main) : new Size(main, cross);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var vertical = Orientation == Orientation.Vertical;
        double pos = 0;
        var first = true;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            if (!first) pos += Spacing;
            first = false;
            var d = child.DesiredSize;
            if (vertical)
            {
                child.Arrange(new Rect(0, pos, final.Width, d.Height));
                pos += d.Height;
            }
            else
            {
                child.Arrange(new Rect(pos, 0, d.Width, final.Height));
                pos += d.Width;
            }
        }
        return final;
    }
}

public sealed class VStack : SpacedStack
{
    public VStack() => Orientation = Orientation.Vertical;
}

public sealed class HStack : SpacedStack
{
    public HStack() => Orientation = Orientation.Horizontal;
}
