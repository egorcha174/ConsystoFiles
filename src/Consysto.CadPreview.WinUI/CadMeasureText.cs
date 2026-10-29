namespace Consysto.CadPreview.WinUI;

/// <summary>
/// The words the measuring tools of <see cref="CadMeshView"/> and <see cref="CadDrawingView"/> show. The library ships no resources of its own, so the
/// host passes them in its language; the defaults are English. Format strings take numbers already formatted.
/// </summary>
public sealed class CadMeasureText
{
    public string BoundsTool { get; init; } = "Overall dimensions";

    public string DistanceTool { get; init; } = "Distance";

    public string RadiusTool { get; init; } = "Radius and diameter";

    public string AngleTool { get; init; } = "Angle";

    public string Clear { get; init; } = "Clear measurements";

    public string Millimeters { get; init; } = "mm";

    /// <summary>{0}, {1}, {2} sizes along X, Y, Z; {3} unit.</summary>
    public string BoundsFormat { get; init; } = "Overall: {0} × {1} × {2} {3}";

    /// <summary>{0} distance; {1} unit; {2}, {3}, {4} offsets along X, Y, Z.</summary>
    public string DistanceFormat { get; init; } = "Distance: {0} {1} (ΔX {2}, ΔY {3}, ΔZ {4})";

    /// <summary>{0} distance between the axes of two holes; {1} unit; {2} wall between them; {3}, {4}, {5} offsets along X, Y, Z.</summary>
    public string CentresFormat { get; init; } = "Between centres: {0} {1}, between edges: {2} {1} (ΔX {3}, ΔY {4}, ΔZ {5})";

    /// <summary>{0} radius; {1} diameter; {2} unit.</summary>
    public string RadiusFormat { get; init; } = "Radius {0} {2}, diameter {1} {2}";

    /// <summary>{0} angle in degrees.</summary>
    public string AngleFormat { get; init; } = "Angle: {0}°";

    public string DistancePrompt { get; init; } = "Click two points on the model; a hole is taken at its centre, hold Ctrl for the exact point";

    public string RadiusPrompt { get; init; } = "Click a round edge or face, or three points on an arc";

    /// <summary>{0} points given so far, out of three.</summary>
    public string RadiusPointsPrompt { get; init; } = "No circle found here: click points on the arc ({0} of 3)";

    public string AnglePrompt { get; init; } = "Click two faces or edges";

    public string Preparing { get; init; } = "Preparing the model for measuring…";

    public string UnitsAssumed { get; init; } = "The file does not state its units; millimetres are assumed";

    // The 2D drawing view

    /// <summary>Shown after a size in a drawing that states no units.</summary>
    public string DrawingUnits { get; init; } = "units";

    /// <summary>{0}, {1} sizes along X, Y; {2} unit.</summary>
    public string DrawingBoundsFormat { get; init; } = "Overall: {0} × {1} {2}";

    /// <summary>{0} distance; {1} unit; {2}, {3} offsets along X, Y.</summary>
    public string DrawingDistanceFormat { get; init; } = "Distance: {0} {1} (ΔX {2}, ΔY {3})";

    /// <summary>{0} distance between the centres of two holes; {1} unit; {2} wall between them; {3}, {4} offsets along X, Y.</summary>
    public string DrawingCentresFormat { get; init; } = "Between centres: {0} {1}, between edges: {2} {1} (ΔX {3}, ΔY {4})";

    public string DrawingDistancePrompt { get; init; } = "Click two points; a hole is taken at its centre, hold Ctrl for the exact point";

    public string DrawingRadiusPrompt { get; init; } = "Click an arc or a circle";

    public string DrawingNoArc { get; init; } = "This is not an arc or a circle";

    public string DrawingAnglePrompt { get; init; } = "Click two lines";

    public string DrawingUnitsUnknown { get; init; } = "The file does not state its units; sizes are in drawing units";
}
