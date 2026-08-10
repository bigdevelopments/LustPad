using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using LustPad.Core.Audio;

namespace LustPad.Controls;

/// <summary>
/// Amplitude overview with loop-start and crossfade region markers.
/// </summary>
public sealed class WaveformView : Control
{
    public static readonly StyledProperty<WaveformPeaks.PeakColumn[]?> PeaksProperty =
        AvaloniaProperty.Register<WaveformView, WaveformPeaks.PeakColumn[]?>(nameof(Peaks));

    public static readonly StyledProperty<float> LoopStartFractionProperty =
        AvaloniaProperty.Register<WaveformView, float>(nameof(LoopStartFraction), 0.2f);

    public static readonly StyledProperty<float> CrossfadeStartFractionProperty =
        AvaloniaProperty.Register<WaveformView, float>(nameof(CrossfadeStartFraction), 0.9f);

    public static readonly StyledProperty<string> JoinInfoProperty =
        AvaloniaProperty.Register<WaveformView, string>(nameof(JoinInfo), "");

    static WaveformView()
    {
        AffectsRender<WaveformView>(
            PeaksProperty, LoopStartFractionProperty, CrossfadeStartFractionProperty, BoundsProperty);
    }

    public WaveformPeaks.PeakColumn[]? Peaks
    {
        get => GetValue(PeaksProperty);
        set => SetValue(PeaksProperty, value);
    }

    public float LoopStartFraction
    {
        get => GetValue(LoopStartFractionProperty);
        set => SetValue(LoopStartFractionProperty, value);
    }

    public float CrossfadeStartFraction
    {
        get => GetValue(CrossfadeStartFractionProperty);
        set => SetValue(CrossfadeStartFractionProperty, value);
    }

    public string JoinInfo
    {
        get => GetValue(JoinInfoProperty);
        set => SetValue(JoinInfoProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        if (bounds.Width < 2 || bounds.Height < 2)
            return;

        context.FillRectangle(new SolidColorBrush(Color.FromRgb(24, 24, 27)), bounds);

        var peaks = Peaks;
        if (peaks is { Length: > 0 })
        {
            double mid = bounds.Height * 0.5;
            double amp = bounds.Height * 0.45;
            var brush = new SolidColorBrush(Color.FromRgb(167, 139, 250));
            int n = peaks.Length;
            double colW = bounds.Width / n;
            for (int i = 0; i < n; i++)
            {
                float lo = peaks[i].Min;
                float hi = peaks[i].Max;
                double y1 = mid - hi * amp;
                double y2 = mid - lo * amp;
                if (y2 < y1) (y1, y2) = (y2, y1);
                if (y2 - y1 < 1) y2 = y1 + 1;
                context.FillRectangle(brush, new Rect(i * colW, y1, Math.Max(1, colW - 0.5), y2 - y1));
            }
        }

        // Loop region tint
        double ls = Math.Clamp(LoopStartFraction, 0, 1) * bounds.Width;
        double cf = Math.Clamp(CrossfadeStartFraction, 0, 1) * bounds.Width;
        var loopTint = new SolidColorBrush(Color.FromArgb(40, 52, 211, 153));
        context.FillRectangle(loopTint, new Rect(ls, 0, bounds.Width - ls, bounds.Height));

        // Crossfade region tint
        var xfTint = new SolidColorBrush(Color.FromArgb(55, 251, 191, 36));
        context.FillRectangle(xfTint, new Rect(cf, 0, bounds.Width - cf, bounds.Height));

        // Markers
        var loopPen = new Pen(new SolidColorBrush(Color.FromRgb(52, 211, 153)), 1.5);
        var xfPen = new Pen(new SolidColorBrush(Color.FromRgb(251, 191, 36)), 1.5);
        var endPen = new Pen(new SolidColorBrush(Color.FromRgb(248, 113, 113)), 1.5);
        context.DrawLine(loopPen, new Point(ls, 0), new Point(ls, bounds.Height));
        context.DrawLine(xfPen, new Point(cf, 0), new Point(cf, bounds.Height));
        context.DrawLine(endPen, new Point(bounds.Width - 1, 0), new Point(bounds.Width - 1, bounds.Height));
    }
}
