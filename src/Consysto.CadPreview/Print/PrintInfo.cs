namespace Consysto.CadPreview.Print;

/// <summary>
/// What a slicer wrote about a print job: how long it takes, how much plastic of which kind, on what printer, and the picture
/// of the plate it drew. Every field is null when the file does not say.
/// </summary>
public sealed class PrintInfo
{
    public TimeSpan? PrintTime { get; init; }

    public double? FilamentGrams { get; init; }

    public double? FilamentMeters { get; init; }

    /// <summary>"PETG", or "PLA, PETG" for a job with several filaments.</summary>
    public string? FilamentType { get; init; }

    public double? LayerHeight { get; init; }

    public double? NozzleDiameter { get; init; }

    public int? Layers { get; init; }

    /// <summary>"Flashforge Adventurer 5M".</summary>
    public string? Printer { get; init; }

    /// <summary>"Flash Studio 1.7.13", "PrusaSlicer 2.8.1", "Cura 5.7.1".</summary>
    public string? Slicer { get; init; }

    /// <summary>The largest picture the slicer stored, as PNG, JPEG or BMP.</summary>
    public byte[]? Thumbnail { get; init; }

    public bool IsEmpty
        => PrintTime is null && FilamentGrams is null && FilamentMeters is null && FilamentType is null && LayerHeight is null
            && NozzleDiameter is null && Layers is null && Printer is null && Slicer is null && Thumbnail is null;
}
