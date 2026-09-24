namespace Consysto.CadPreview.Drawing;

/// <summary>
/// Reads drawings of the old kind that the main reader gives up on.
///
/// A file saved as DXF R10 (1988) still turns up daily in a cutting shop: machine software and old libraries write it,
/// and a customer sends what he has. The library this program reads drawings with parses the shape of such a file —
/// the polylines, the vertices, their count — but hands back every vertex at the origin. The coordinates are gone,
/// so there is nothing to rebuild from and nothing to show.
///
/// This reader goes at the text itself. A DXF is pairs of lines, a numeric code and its value, and the codes for what
/// a drawing is made of have not changed since: 10 and 20 are a point, 40 a radius, 42 the curvature between two
/// vertices. Only geometry is read; nothing in the file is executed.
///
/// It is used solely as a last resort — when the main reader came back with nothing to draw — so a file it handles
/// well is never taken away from it.
/// </summary>
internal static class LegacyDxfReader
{
	/// <summary>Enough for a drawing; a file with more is not a drawing but a dump, and reading it would hang the pane.</summary>
	private const int MaximumEntities = 200_000;

	private const int FullCircleSegments = 72;

	/// <summary>Set on a POLYLINE that is a mesh or a surface rather than a line: its vertices are not a path.</summary>
	private const int PolygonMeshFlags = 16 | 64;

	public static bool TryRead(string path, Drawing2D drawing)
	{
		try
		{
			using var reader = new StreamReader(path);

			// A binary DXF is a different animal and says so on its first line
			var probe = reader.ReadLine();
			if (probe is null || probe.StartsWith("AutoCAD Binary", StringComparison.Ordinal))
				return false;

			reader.BaseStream.Position = 0;
			reader.DiscardBufferedData();

			var found = new Reading(drawing).Run(reader);
			if (found > 0)
				drawing.Notes.Add($"Старый DXF прочитан запасным разбором: {found} объектов");

			return found > 0;
		}
		catch (Exception)
		{
			return false;
		}
	}

	private sealed class Reading(Drawing2D drawing)
	{
		private readonly Dictionary<int, List<string>> fields = [];

		/// <summary>Vertices of the POLYLINE being read; null when we are not inside one.</summary>
		private List<(double X, double Y, double Bulge)>? path;
		private bool pathClosed;
		private string pathLayer = "0";

		private string? entity;
		private int drawn;

		public int Run(TextReader reader)
		{
			var inEntities = false;

			while (Next(reader) is (int code, string value))
			{
				if (code != 0)
				{
					if (inEntities)
						Collect(code, value);
					continue;
				}

				// A zero code opens the next entity, which means the previous one is complete
				if (inEntities)
					Finish();

				switch (value)
				{
					case "SECTION":
						entity = "SECTION";
						break;

					case "ENDSEC":
						if (inEntities)
							return drawn;
						break;

					default:
						entity = value;
						break;
				}

				if (entity == "SECTION")
				{
					// The name of the section follows in code 2
					if (Next(reader) is (2, string name))
					{
						inEntities = name == "ENTITIES";
						entity = null;
					}
				}

				fields.Clear();

				if (drawn > MaximumEntities)
					return drawn;
			}

			if (inEntities)
				Finish();

			return drawn;
		}

		private static (int Code, string Value)? Next(TextReader reader)
		{
			var code = reader.ReadLine();
			var value = code is null ? null : reader.ReadLine();
			if (code is null || value is null)
				return null;

			return int.TryParse(code.Trim(), out var number) ? (number, value.Trim()) : (-1, string.Empty);
		}

		private void Collect(int code, string value)
		{
			if (!fields.TryGetValue(code, out var values))
				fields[code] = values = [];

			if (values.Count < 100_000)
				values.Add(value);
		}

		private void Finish()
		{
			switch (entity)
			{
				case "LINE":
					Add([Point(10, 20), Point(11, 21)], false);
					break;

				case "CIRCLE":
					AddArc(Point(10, 20), Number(40), 0, 2 * Math.PI, true);
					break;

				case "ARC":
					var start = Number(50) * Math.PI / 180;
					var end = Number(51) * Math.PI / 180;
					var sweep = end - start;
					if (sweep <= 0)
						sweep += 2 * Math.PI;
					AddArc(Point(10, 20), Number(40), start, sweep, false);
					break;

				case "LWPOLYLINE":
					AddPath(Vertices(), (Flags() & 1) != 0);
					break;

				case "POLYLINE":
					// Vertices arrive as separate entities until SEQEND
					path = (Flags() & PolygonMeshFlags) != 0 ? null : [];
					pathClosed = (Flags() & 1) != 0;
					pathLayer = Layer();
					break;

				case "VERTEX":
					path?.Add((Number(10), Number(20), Number(42)));
					break;

				case "SEQEND":
					if (path is { Count: > 1 })
						AddPath(path, pathClosed, pathLayer);
					path = null;
					break;

				case "SOLID":
				case "TRACE":
					// The corners of a SOLID are given in a Z order, so the third and fourth are swapped to walk its edge
					Add([Point(10, 20), Point(11, 21), Point(13, 23), Point(12, 22)], true);
					break;

				case "TEXT":
					var text = Text(1);
					if (!string.IsNullOrEmpty(text))
					{
						drawing.Layers.Add(Layer());
						drawing.Primitives.Add(new TextPrimitive(
							Rgb.Black, Layer(), Point(10, 20), Number(40), Number(50) * Math.PI / 180, text));
						drawn++;
					}
					break;
			}
		}

		private void AddPath(List<(double X, double Y, double Bulge)> vertices, bool closed, string? layer = null)
		{
			if (vertices.Count > 1)
				Add(EntityFlattener.BulgePoints(vertices, closed), closed, layer);
		}

		private void AddArc(Point2 center, double radius, double from, double sweep, bool closed)
		{
			if (radius <= 0 || !double.IsFinite(radius))
				return;

			var segments = Math.Max(8, (int)Math.Ceiling(FullCircleSegments * Math.Abs(sweep) / (2 * Math.PI)));
			var points = new Point2[segments + 1];
			for (var i = 0; i <= segments; i++)
			{
				var angle = from + sweep * i / segments;
				points[i] = new Point2(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));
			}

			Add(points, closed);
		}

		private void Add(Point2[] points, bool closed, string? layer = null)
		{
			if (points.Length < 2 || points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y)))
				return;

			var name = layer ?? Layer();
			drawing.Layers.Add(name);
			drawing.Primitives.Add(new PolylinePrimitive(Rgb.Black, name, points, closed));
			drawn++;
		}

		/// <summary>A point built from its two codes; a missing coordinate reads as zero, as it does in the format.</summary>
		private Point2 Point(int x, int y) => new(Number(x), Number(y));

		private double Number(int code)
			=> fields.TryGetValue(code, out var values) && values.Count > 0
				&& double.TryParse(values[0], System.Globalization.NumberStyles.Float,
					System.Globalization.CultureInfo.InvariantCulture, out var value)
				? value
				: 0;

		private int Flags() => (int)Number(70);

		private string Layer()
			=> fields.TryGetValue(8, out var values) && values.Count > 0 && values[0].Length > 0 ? values[0] : "0";

		private string Text(int code)
			=> fields.TryGetValue(code, out var values) && values.Count > 0 ? values[0] : string.Empty;

		/// <summary>The vertices of an LWPOLYLINE, whose coordinates repeat under the same codes.</summary>
		private List<(double X, double Y, double Bulge)> Vertices()
		{
			var result = new List<(double X, double Y, double Bulge)>();
			if (!fields.TryGetValue(10, out var xs) || !fields.TryGetValue(20, out var ys))
				return result;

			// A bulge is written only for the vertices that have one, so the values line up with the points
			// only when every vertex carries one. Otherwise they are left out: a straight line is a smaller
			// error than an arc bent at the wrong vertex.
			fields.TryGetValue(42, out var bulges);
			var aligned = bulges is not null && bulges.Count == xs.Count;

			for (var i = 0; i < Math.Min(xs.Count, ys.Count); i++)
				result.Add((Parse(xs[i]), Parse(ys[i]), aligned ? Parse(bulges![i]) : 0));

			return result;

			static double Parse(string value)
				=> double.TryParse(value, System.Globalization.NumberStyles.Float,
					System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : 0;
		}
	}
}
