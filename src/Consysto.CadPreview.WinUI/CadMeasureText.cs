namespace Consysto.CadPreview.WinUI;

/// <summary>
/// The words the measuring tools of <see cref="CadMeshView"/> show. The library ships no resources of its own, so the
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

    /// <summary>{0} radius; {1} diameter; {2} unit.</summary>
    public string RadiusFormat { get; init; } = "Radius {0} {2}, diameter {1} {2}";

    /// <summary>{0} angle in degrees.</summary>
    public string AngleFormat { get; init; } = "Angle: {0}°";

    public string DistancePrompt { get; init; } = "Click two points on the model";

    public string RadiusPrompt { get; init; } = "Click a round edge or face, or three points on an arc";

    /// <summary>{0} points given so far, out of three.</summary>
    public string RadiusPointsPrompt { get; init; } = "No circle found here: click points on the arc ({0} of 3)";

    public string AnglePrompt { get; init; } = "Click two faces or edges";

    public string Preparing { get; init; } = "Preparing the model for measuring…";

    public string UnitsAssumed { get; init; } = "The file does not state its units; millimetres are assumed";
}
