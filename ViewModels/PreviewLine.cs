using LaserConsole.Services;

namespace LaserConsole.ViewModels;

// View-ready snapshot of one fading preview segment — mm coordinates
// relative to field centre, plus the opacity computed for this render tick.
public readonly struct PreviewLine
{
    public double X1Mm { get; init; }
    public double Y1Mm { get; init; }
    public double X2Mm { get; init; }
    public double Y2Mm { get; init; }
    public MoveKind Kind { get; init; }
    public double Opacity { get; init; }
}
