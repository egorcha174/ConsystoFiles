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

Console.WriteLine($"PASS: {checks} checks");

void Check(bool condition, string description)
{
	if (!condition)
		throw new InvalidOperationException(description);
	checks++;
}
