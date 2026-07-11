using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using LaserConsole.Services;
using LaserConsole.ViewModels;

namespace LaserConsole.Views;

// Custom-drawn (not shape-per-item) so a fast-refreshing LightBurn preview
// doesn't pay Avalonia control/container overhead per line — this can redraw
// dozens of fading segments at 30fps cheaply via DrawingContext directly.
public sealed class MarkPreviewControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<PreviewLine>?> LinesProperty =
        AvaloniaProperty.Register<MarkPreviewControl, IReadOnlyList<PreviewLine>?>(nameof(Lines));

    public static readonly StyledProperty<double> FieldWidthMmProperty =
        AvaloniaProperty.Register<MarkPreviewControl, double>(nameof(FieldWidthMm), 100.0);

    public static readonly StyledProperty<double> FieldHeightMmProperty =
        AvaloniaProperty.Register<MarkPreviewControl, double>(nameof(FieldHeightMm), 100.0);

    static MarkPreviewControl()
    {
        AffectsRender<MarkPreviewControl>(LinesProperty, FieldWidthMmProperty, FieldHeightMmProperty);
    }

    public IReadOnlyList<PreviewLine>? Lines
    {
        get => GetValue(LinesProperty);
        set => SetValue(LinesProperty, value);
    }

    public double FieldWidthMm
    {
        get => GetValue(FieldWidthMmProperty);
        set => SetValue(FieldWidthMmProperty, value);
    }

    public double FieldHeightMm
    {
        get => GetValue(FieldHeightMmProperty);
        set => SetValue(FieldHeightMmProperty, value);
    }

    static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.Parse("#11111B"));
    static readonly IPen   GridPen   = new Pen(new SolidColorBrush(Color.Parse("#232338")), 1);
    static readonly IPen   AxisPen   = new Pen(new SolidColorBrush(Color.Parse("#3A3A55")), 1);

    // Pens are cached (not `new Pen(...)` per segment per frame) — with a
    // complex shape redrawing hundreds of segments at 30fps, allocating a
    // fresh Pen for every line every frame was real GC pressure and part of
    // why rendering fell behind LightBurn on anything more than a square.
    static readonly IPen JumpPen   = new Pen(new SolidColorBrush(Color.Parse("#45B3D4")), 1);   // beam off
    static readonly IPen RedDotPen = new Pen(new SolidColorBrush(Color.Parse("#FAB387")), 1.5); // pilot/guide beam (inferred)
    static readonly IPen MarkPen   = new Pen(new SolidColorBrush(Color.Parse("#F38BA8")), 2);   // laser firing

    public override void Render(DrawingContext context)
    {
        double w = Bounds.Width;
        double h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        context.FillRectangle(BackgroundBrush, new Rect(0, 0, w, h));

        double fw = FieldWidthMm  > 0 ? FieldWidthMm  : 100;
        double fh = FieldHeightMm > 0 ? FieldHeightMm : 100;

        // 1cm grid.
        for (double x = 0; x <= fw + 0.001; x += 10)
        {
            double px = x / fw * w;
            context.DrawLine(GridPen, new Point(px, 0), new Point(px, h));
        }
        for (double y = 0; y <= fh + 0.001; y += 10)
        {
            double py = y / fh * h;
            context.DrawLine(GridPen, new Point(0, py), new Point(w, py));
        }

        // Centre crosshair (field origin).
        context.DrawLine(AxisPen, new Point(w / 2, 0), new Point(w / 2, h));
        context.DrawLine(AxisPen, new Point(0, h / 2), new Point(w, h / 2));

        var lines = Lines;
        if (lines is null) return;

        foreach (var seg in lines)
        {
            var p1 = ToCanvas(seg.X1Mm, seg.Y1Mm, fw, fh, w, h);
            var p2 = ToCanvas(seg.X2Mm, seg.Y2Mm, fw, fh, w, h);
            var pen = seg.Kind switch
            {
                MoveKind.Marking => MarkPen,
                MoveKind.RedDot  => RedDotPen,
                _                => JumpPen,
            };

            using (context.PushOpacity(seg.Opacity))
                context.DrawLine(pen, p1, p2);
        }
    }

    static Point ToCanvas(double xMm, double yMm, double fw, double fh, double w, double h)
    {
        double px = (xMm + fw / 2) / fw * w;
        double py = h - (yMm + fh / 2) / fh * h;   // flip Y: mm "up" = screen "up"
        return new Point(px, py);
    }
}
