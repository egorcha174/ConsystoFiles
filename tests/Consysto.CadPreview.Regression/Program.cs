using System.Numerics;
using Consysto.CadPreview.Mesh;

var mesh = new Mesh3D([new(-1, -1, 0), new(1, -1, 0), new(0, 1, 0)], MeshUp.Z);
var rasterizer = new MeshRasterizer();
int checks = 0;

var normal = rasterizer.RenderFrame(mesh, 320, 240, MeshView.Home, MeshFit.Sphere, 2);
Check(normal.Width == 320 && normal.Height == 240, "ordinary preview keeps its dimensions");
Check(normal.Pixels.Where((_, i) => i % 4 == 3).Any(alpha => alpha != 0), "triangle is visible");
Check(normal.Pixels.Length == normal.Width * normal.Height * 4, "BGRA buffer matches dimensions");

foreach (var (width, height, samples) in new[]
{
	(7680, 4320, 2),
	(int.MaxValue, int.MaxValue, 4),
	(int.MaxValue, 1, 4),
	(1, int.MaxValue, 4),
	(3840, 2160, 1),
	(4096, 4096, 4),
})
{
	var frame = rasterizer.RenderFrame(Mesh3D.Empty, width, height, MeshView.Home, MeshFit.Sphere, samples);
	Check(frame.Width > 0 && frame.Height > 0, "large preview has positive dimensions");
	Check((long)frame.Width * frame.Height * samples * samples <= 4 * 1024 * 1024, "large preview stays within sample budget");
	Check(frame.Pixels.Length == (long)frame.Width * frame.Height * 4, "large preview has a complete BGRA buffer");
}

var resized = rasterizer.RenderFrame(mesh, 80, 60, MeshView.Home, MeshFit.Sphere, 2);
Check(resized.Pixels.Length == 80 * 60 * 4, "small preview releases oversized output buffer");
var minimum = rasterizer.RenderFrame(Mesh3D.Empty, 0, -1, MeshView.Home, MeshFit.Sphere, 0);
Check(minimum.Width == 1 && minimum.Height == 1 && minimum.Pixels.Length == 4, "empty control dimensions are safe");

// Measuring. Front view of a Z-up mesh: the viewer looks along +Y, so the face nearest to it has the smallest Y.
var front = new MeshView(0, 0, 1, 0, 0);
foreach (var centre in new[] { Vector3.Zero, new Vector3(250_000, -180_000, 90_000) })
{
	var box = Box(centre, new Vector3(40, 20, 10));
	var boxMeasure = new MeshMeasure(box);
	var boxView = MeshProjection.Create(box, 400, 300, front, MeshFit.Sphere);

	// The middle of the screen is off every corner and edge, so the pick stays on the face.
	var middle = boxMeasure.Pick(boxView, new Vector2(200, 150), 1);
	Check(middle is { Snap: MeshSnap.Face }, "pick in the middle of a box hits a face");
	Check(MathF.Abs(middle!.Value.Point.Y - (centre.Y - 10)) < 0.01f, "pick hits the face towards the viewer, not the one behind");
	Check(MathF.Abs(middle.Value.Point.X - centre.X) < 0.05f && MathF.Abs(middle.Value.Point.Z - centre.Z) < 0.05f, "picked point lies under the cursor, also far from the origin");
	Check(Vector3.Dot(middle.Value.Normal, -Vector3.UnitY) > 0.99f, "picked normal faces the viewer");
	Check(boxMeasure.Pick(boxView, new Vector2(5, 5), 1) is null, "pick beside the model hits nothing");

	var cornerOnScreen = boxView.ToScreen(centre + new Vector3(-20, -10, 5));
	var corner = boxMeasure.Pick(boxView, cornerOnScreen + new Vector2(2, 2), 8);
	Check(corner is { Snap: MeshSnap.Vertex } && Vector3.Distance(corner.Value.Point, centre + new Vector3(-20, -10, 5)) < 0.01f, "pick near a corner snaps to it");
}

var cube = Box(Vector3.Zero, new Vector3(20, 20, 20));
var cubeMeasure = new MeshMeasure(cube);
var corner3 = MeshProjection.Create(cube, 400, 400, MeshView.Home, MeshFit.Sphere);
var faceTowardViewer = cubeMeasure.Pick(corner3, corner3.ToScreen(new Vector3(0, -10, 0)), 1);
var faceOnTop = cubeMeasure.Pick(corner3, corner3.ToScreen(new Vector3(0, 0, 10)), 1);
Check(faceTowardViewer is { Snap: MeshSnap.Face } && faceOnTop is { Snap: MeshSnap.Face }, "two faces of a cube are picked");
Check(MathF.Abs(cubeMeasure.Angle(faceTowardViewer!.Value, faceOnTop!.Value) - 90) < 0.1f, "faces of a cube meet at 90 degrees");

// A tube: the wall and both rims are circles of the outer radius.
var tube = Cylinder(Vector3.Zero, 12.5f, 30, 48);
var tubeMeasure = new MeshMeasure(tube);
var tubeView = MeshProjection.Create(tube, 400, 400, front, MeshFit.Sphere);
var wall = tubeMeasure.Pick(tubeView, tubeView.ToScreen(new Vector3(0, -12.5f, 0)), 1);
Check(wall is { Snap: MeshSnap.Face }, "the wall of a tube is picked");
var wallCircle = tubeMeasure.FindCircle(wall!.Value);
Check(wallCircle is { } c1 && MathF.Abs(c1.Radius - 12.5f) < 0.05f, "radius is found from the wall");
Check(tubeMeasure.FindHole(wall.Value) is { } h1 && Vector3.Distance(h1.Center with { Z = 0 }, Vector3.Zero) < 0.05f, "a click on the wall finds the axis");

var rimPoint = new Vector3(0, -12.5f, 15);
var rim = tubeMeasure.Pick(tubeView, tubeView.ToScreen(rimPoint) + new Vector2(0, 1), 6);
Check(rim is { Snap: MeshSnap.Edge or MeshSnap.Vertex }, "the rim of a tube is snapped to");
var rimCircle = tubeMeasure.FindHole(rim!.Value);
Check(rimCircle is { } c2 && MathF.Abs(c2.Radius - 12.5f) < 0.05f && MathF.Abs(c2.Center.Z - 15) < 0.01f, "the rim is followed all the way round");

Check(MeshMeasure.CircleThrough(new Vector3(5, 0, 0), new Vector3(0, 5, 0), new Vector3(-5, 0, 0)) is { } through && MathF.Abs(through.Radius - 5) < 1e-4f && through.Center.Length() < 1e-4f, "circle through three points");
Check(MeshMeasure.CircleThrough(Vector3.Zero, Vector3.UnitX, 2 * Vector3.UnitX) is null, "no circle through three points on a line");

var empty = new MeshMeasure(Mesh3D.Empty);
Check(empty.Pick(MeshProjection.Create(Mesh3D.Empty, 100, 100, front, MeshFit.Sphere), new Vector2(50, 50), 8) is null, "nothing is picked on an empty mesh");

// Broken numbers in a mesh file drop their triangle instead of breaking the frame and every measurement.
var stl = Path.Combine(Path.GetTempPath(), $"consysto-regression-{Environment.ProcessId}.stl");
try
{
	File.WriteAllText(stl, """
		solid broken
		facet normal 0 0 1
		outer loop
		vertex 0 0 0
		vertex 10 0 0
		vertex 0 10 0
		endloop
		endfacet
		facet normal 0 0 1
		outer loop
		vertex nan 0 0
		vertex 1 0 0
		vertex 0 1 0
		endloop
		endfacet
		facet normal 0 0 1
		outer loop
		vertex 3e38 0 0
		vertex 1 0 0
		vertex 0 1 0
		endloop
		endfacet
		endsolid broken
		""");
	var read = StlReader.Read(stl);
	Check(read.TriangleCount == 1 && float.IsFinite(read.Radius) && MathF.Abs(read.Radius - MathF.Sqrt(200) / 2) < 1e-3f, "NaN and overflowing corners are dropped from STL");
}
finally
{
	File.Delete(stl);
}

Console.WriteLine($"PASS: {checks} checks");

void Check(bool condition, string description)
{
	if (!condition)
		throw new InvalidOperationException(description);
	checks++;
}

static Mesh3D Box(Vector3 centre, Vector3 size)
{
	var h = size / 2;
	Vector3 P(int x, int y, int z) => centre + new Vector3(x == 0 ? -h.X : h.X, y == 0 ? -h.Y : h.Y, z == 0 ? -h.Z : h.Z);
	var quads = new (Vector3, Vector3, Vector3, Vector3)[]
	{
		(P(0, 0, 0), P(1, 0, 0), P(1, 0, 1), P(0, 0, 1)),
		(P(1, 1, 0), P(0, 1, 0), P(0, 1, 1), P(1, 1, 1)),
		(P(0, 1, 0), P(0, 0, 0), P(0, 0, 1), P(0, 1, 1)),
		(P(1, 0, 0), P(1, 1, 0), P(1, 1, 1), P(1, 0, 1)),
		(P(0, 0, 1), P(1, 0, 1), P(1, 1, 1), P(0, 1, 1)),
		(P(0, 1, 0), P(1, 1, 0), P(1, 0, 0), P(0, 0, 0)),
	};
	var vertices = new List<Vector3>();
	foreach (var (a, b, c, d) in quads)
		vertices.AddRange([a, b, c, a, c, d]);
	return new Mesh3D([.. vertices], MeshUp.Z);
}

// A closed cylinder standing on its axis along Z, its middle at the given point.
static Mesh3D Cylinder(Vector3 centre, float radius, float height, int segments)
{
	var vertices = new List<Vector3>();
	Vector3 Rim(int i, float z) => centre + new Vector3(radius * MathF.Cos(i * 2 * MathF.PI / segments), radius * MathF.Sin(i * 2 * MathF.PI / segments), z);
	float bottom = -height / 2, top = height / 2;
	for (int i = 0; i < segments; i++)
	{
		var (a, b, c, d) = (Rim(i, bottom), Rim(i + 1, bottom), Rim(i + 1, top), Rim(i, top));
		vertices.AddRange([a, b, c, a, c, d]);
		vertices.AddRange([centre + new Vector3(0, 0, top), d, c]);
		vertices.AddRange([centre + new Vector3(0, 0, bottom), b, a]);
	}

	return new Mesh3D([.. vertices], MeshUp.Z);
}
