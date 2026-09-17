using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Xml;

namespace Consysto.CadPreview.Mesh;

public sealed record ThreeMfContent(Mesh3D Mesh, byte[]? Thumbnail);

/// <summary>
/// Reads 3MF packages: the root model, components spread over several model parts (production extension, the way
/// Bambu Studio and Orca write them) and the package thumbnail. Sliced ".gcode.3mf" projects often carry no mesh at
/// all, only the slicer's render of the plate; then the thumbnail is everything there is to show.
/// </summary>
public static class ThreeMfReader
{
    private const string CoreNamespace = "http://schemas.microsoft.com/3dmanufacturing/core/2015/02";
    private const string ProductionNamespace = "http://schemas.microsoft.com/3dmanufacturing/production/2015/06";
    private const string RelationshipsNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string ModelRelationship = "http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel";
    private const string ThumbnailRelationship = "http://schemas.openxmlformats.org/package/2006/relationships/metadata/thumbnail";
    private const string DefaultModelPath = "3D/3dmodel.model";
    private const long MaximumThumbnailBytes = 16 * 1024 * 1024;
    private const int MaxDepth = 16;

    public static ThreeMfContent Read(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var (modelPath, thumbnailPath) = ReadRootRelationships(zip);

        var package = new Package(zip);
        var builder = new MeshBuilder();
        foreach (var item in package.GetModel(modelPath).BuildItems)
            package.AddObject(builder, item.Path ?? modelPath, item.ObjectId, item.Transform, depth: 0);

        byte[]? thumbnail = thumbnailPath is null ? null : ReadEntry(zip, thumbnailPath, MaximumThumbnailBytes);
        return new ThreeMfContent(builder.ToMesh(MeshUp.Z), thumbnail);
    }

    private static (string ModelPath, string? ThumbnailPath) ReadRootRelationships(ZipArchive zip)
    {
        string modelPath = DefaultModelPath;
        string? thumbnailPath = null;

        var entry = FindEntry(zip, "_rels/.rels");
        if (entry is null)
            return (modelPath, thumbnailPath);

        using var stream = entry.Open();
        using var xml = XmlReader.Create(stream, XmlSettings);
        while (xml.Read())
        {
            if (xml.NodeType != XmlNodeType.Element || xml.LocalName != "Relationship" || xml.NamespaceURI != RelationshipsNamespace)
                continue;

            var target = xml.GetAttribute("Target");
            if (string.IsNullOrEmpty(target))
                continue;

            switch (xml.GetAttribute("Type"))
            {
                case ModelRelationship:
                    modelPath = NormalizePartName(target);
                    break;
                case ThumbnailRelationship:
                    thumbnailPath = NormalizePartName(target);
                    break;
            }
        }

        return (modelPath, thumbnailPath);
    }

    private static readonly XmlReaderSettings XmlSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreWhitespace = true,
    };

    private static string NormalizePartName(string partName) => partName.TrimStart('/');

    // OPC part names may be stored percent-encoded or with different case than the relationship spells them.
    private static ZipArchiveEntry? FindEntry(ZipArchive zip, string partName)
    {
        partName = NormalizePartName(partName);
        return zip.GetEntry(partName)
            ?? zip.GetEntry(Uri.UnescapeDataString(partName))
            ?? zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, partName, StringComparison.OrdinalIgnoreCase));
    }

    private static byte[]? ReadEntry(ZipArchive zip, string partName, long maximumBytes)
    {
        var entry = FindEntry(zip, partName);
        if (entry is null || entry.Length > maximumBytes)
            return null;

        using var stream = entry.Open();
        var data = new byte[entry.Length];
        stream.ReadExactly(data);
        return data;
    }

    private readonly record struct ObjectReference(int ObjectId, string? Path, Matrix4x4 Transform);

    private sealed class ObjectDefinition
    {
        public List<Vector3> Vertices { get; } = [];

        public List<int> Triangles { get; } = [];

        public List<ObjectReference> Components { get; } = [];
    }

    private sealed class ModelPart
    {
        public Dictionary<int, ObjectDefinition> Objects { get; } = [];

        public List<ObjectReference> BuildItems { get; } = [];
    }

    private sealed class Package(ZipArchive zip)
    {
        private readonly Dictionary<string, ModelPart> models = new(StringComparer.OrdinalIgnoreCase);

        public ModelPart GetModel(string partName)
        {
            partName = NormalizePartName(partName);
            if (models.TryGetValue(partName, out var model))
                return model;

            var entry = FindEntry(zip, partName);
            model = new ModelPart();
            if (entry is not null)
            {
                using var stream = entry.Open();
                Parse(stream, model);
            }

            models[partName] = model;
            return model;
        }

        /// <param name="transform">Maps the object's coordinates to the build plate.</param>
        public void AddObject(MeshBuilder builder, string modelPath, int objectId, Matrix4x4 transform, int depth)
        {
            if (depth > MaxDepth || !GetModel(modelPath).Objects.TryGetValue(objectId, out var definition))
                return;

            var vertices = definition.Vertices;
            var triangles = definition.Triangles;
            for (int i = 0; i + 2 < triangles.Count; i += 3)
            {
                int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
                if ((uint)a >= (uint)vertices.Count || (uint)b >= (uint)vertices.Count || (uint)c >= (uint)vertices.Count)
                    continue;

                builder.AddTriangle(
                    Vector3.Transform(vertices[a], transform),
                    Vector3.Transform(vertices[b], transform),
                    Vector3.Transform(vertices[c], transform));
            }

            // A component without p:path lives in the same model part as the object that references it.
            foreach (var component in definition.Components)
                AddObject(builder, component.Path ?? modelPath, component.ObjectId, component.Transform * transform, depth + 1);
        }

        private static void Parse(Stream stream, ModelPart model)
        {
            using var xml = XmlReader.Create(stream, XmlSettings);
            ObjectDefinition? current = null;

            while (xml.Read())
            {
                if (xml.NodeType != XmlNodeType.Element || xml.NamespaceURI != CoreNamespace)
                    continue;

                switch (xml.LocalName)
                {
                    case "object":
                        current = new ObjectDefinition();
                        model.Objects[ParseInt(xml.GetAttribute("id"))] = current;
                        break;
                    case "vertex" when current is not null:
                        current.Vertices.Add(new Vector3(ParseFloat(xml.GetAttribute("x")), ParseFloat(xml.GetAttribute("y")), ParseFloat(xml.GetAttribute("z"))));
                        break;
                    case "triangle" when current is not null:
                        current.Triangles.Add(ParseInt(xml.GetAttribute("v1")));
                        current.Triangles.Add(ParseInt(xml.GetAttribute("v2")));
                        current.Triangles.Add(ParseInt(xml.GetAttribute("v3")));
                        break;
                    case "component" when current is not null:
                        current.Components.Add(ParseReference(xml));
                        break;
                    case "build":
                        current = null;
                        break;
                    case "item":
                        model.BuildItems.Add(ParseReference(xml));
                        break;
                }
            }
        }

        private static ObjectReference ParseReference(XmlReader xml)
        {
            var path = xml.GetAttribute("path", ProductionNamespace);
            return new ObjectReference(
                ParseInt(xml.GetAttribute("objectid")),
                string.IsNullOrEmpty(path) ? null : NormalizePartName(path),
                ParseTransform(xml.GetAttribute("transform")));
        }

        // "m00 m01 m02 m10 m11 m12 m20 m21 m22 m30 m31 m32": row vectors with the translation last,
        // the same convention as System.Numerics, so Vector3.Transform applies it as is.
        private static Matrix4x4 ParseTransform(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return Matrix4x4.Identity;

            var parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 12)
                return Matrix4x4.Identity;

            var m = new float[12];
            for (int i = 0; i < 12; i++)
                m[i] = ParseFloat(parts[i]);

            return new Matrix4x4(
                m[0], m[1], m[2], 0,
                m[3], m[4], m[5], 0,
                m[6], m[7], m[8], 0,
                m[9], m[10], m[11], 1);
        }

        // A vertex that fails to parse stays in the list as NaN so later indices keep pointing at the right vertices;
        // MeshBuilder drops the triangles that touch it.
        private static float ParseFloat(string? value)
            => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float result) ? result : float.NaN;

        private static int ParseInt(string? value)
            => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) ? result : -1;
    }
}
