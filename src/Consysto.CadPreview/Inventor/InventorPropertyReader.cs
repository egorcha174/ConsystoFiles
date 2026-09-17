namespace Consysto.CadPreview.Inventor;

/// <summary>The iProperties of an Inventor document that people search and sort by, read without Inventor.</summary>
public sealed record InventorProperties
{
    public string? Title { get; init; }
    public string? Subject { get; init; }
    public string? Author { get; init; }
    public string? Comments { get; init; }
    public string? PartNumber { get; init; }
    public string? Description { get; init; }
    public string? Project { get; init; }
    public string? Designer { get; init; }
    public string? Vendor { get; init; }
    public string? Stock { get; init; }
    public string? Material { get; init; }
    public string? SheetMetalRule { get; init; }
    public string? DocumentType { get; init; }
    public string? LastUpdatedWith { get; init; }

    /// <summary>Mass in kilograms, when Inventor has computed it for the saved state.</summary>
    public double? MassKilograms { get; init; }

    /// <summary>User-defined properties in the order Inventor keeps them.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Custom { get; init; } = [];

    public bool IsEmpty
        => PartNumber is null && Description is null && Material is null && Title is null && Author is null && Custom.Count == 0;
}

/// <summary>
/// Maps Inventor's property sets to <see cref="InventorProperties"/>. The property ids are the ones the Inventor API
/// documents for its "Design Tracking Properties" and the standard summary sets.
/// </summary>
public static class InventorPropertyReader
{
    private static readonly Guid SummaryInformation = new("f29f85e0-4ff9-1068-ab91-08002b27b3d9");
    private static readonly Guid InventorSummaryInformation = new("3d38de39-0588-4c14-bb37-18f4d5dd31c7");
    private static readonly Guid DesignTracking = new("32853f0f-3444-11d1-9e93-0060b03c1ca6");
    private static readonly Guid UserDefined = new("9929adb8-6407-413e-b3dc-cb9ad2f564b7");
    private static readonly Guid OfficeUserDefined = new("d5cdd505-2e9c-101b-9397-08002b2cf9ae");

    private const uint MassValidFlags = 62;

    public static bool IsSupported(string? extension)
        => InventorPreviewReader.IsSupported(extension);

    public static InventorProperties Read(string path)
    {
        var sets = PropertySetReader.Read(path);

        var summary = Find(sets, InventorSummaryInformation) ?? Find(sets, SummaryInformation);
        var tracking = Find(sets, DesignTracking);
        var custom = Find(sets, UserDefined) ?? Find(sets, OfficeUserDefined);

        return new InventorProperties
        {
            Title = Text(summary, 2),
            Subject = Text(summary, 3),
            Author = Text(summary, 4),
            Comments = Text(summary, 6),
            PartNumber = Text(tracking, 5),
            Project = Text(tracking, 7),
            Description = Text(tracking, 29),
            Vendor = Text(tracking, 30),
            Designer = Text(tracking, 41),
            Stock = Text(tracking, 55),
            Material = Text(tracking, 20),
            DocumentType = Text(tracking, 32),
            SheetMetalRule = Text(tracking, 66),
            LastUpdatedWith = Text(tracking, 67),
            MassKilograms = Mass(tracking),
            Custom = CustomProperties(custom),
        };
    }

    private static StoredPropertySet? Find(IReadOnlyList<StoredPropertySet> sets, Guid formatId)
        => sets.FirstOrDefault(set => set.FormatId == formatId);

    private static string? Text(StoredPropertySet? set, uint id)
        => set?.Properties.FirstOrDefault(property => property.Id == id)?.Value is { } value
            && Convert.ToString(value, System.Globalization.CultureInfo.CurrentCulture)?.Trim() is { Length: > 0 } text
                ? text
                : null;

    /// <summary>
    /// Inventor stores mass in grams (checked against the Physical tab of a real part); a zero in the validity flags
    /// means it was never computed and the number is stale.
    /// </summary>
    private static double? Mass(StoredPropertySet? set)
    {
        if (set?.Properties.FirstOrDefault(property => property.Id == 58)?.Value is not double mass || mass <= 0)
            return null;
        if (set.Properties.FirstOrDefault(property => property.Id == MassValidFlags)?.Value is int flags && flags == 0)
            return null;
        return mass / 1000d;
    }

    private static List<KeyValuePair<string, string>> CustomProperties(StoredPropertySet? set)
    {
        var properties = new List<KeyValuePair<string, string>>();
        if (set is null)
            return properties;

        foreach (var property in set.Properties)
        {
            // Names starting with '#' are Inventor's own copies of parameters exported as properties; the user sees the plain ones
            if (property.Name is not { Length: > 0 } name || name.StartsWith('#') || name == "Property Set Name")
                continue;
            if (Convert.ToString(property.Value, System.Globalization.CultureInfo.CurrentCulture)?.Trim() is { Length: > 0 } value)
                properties.Add(new(name, value));
        }

        return properties;
    }
}
