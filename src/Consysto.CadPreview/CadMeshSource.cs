using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Consysto.CadPreview.Mesh;
using Consysto.CadPreview.Step;

namespace Consysto.CadPreview;

/// <summary>
/// Geometry of documents made by CAD systems we do not read ourselves, so a part can be turned in the hand rather than
/// looked at as a picture.
///
/// The reading is done by cadmpeg (Apache-2.0) in a helper process: a file that sends it into a corner cannot take the
/// program down with it. Two ways out of that helper are tried, because different documents keep their shape differently:
///
/// 1. The mesh the document already carries — SolidWorks saves one, and so does a Rhino document made of meshes.
/// 2. The exact surfaces, converted to STEP and then divided into triangles by the OpenCascade engine, which is how a
///    Rhino or FreeCAD document built from surfaces gives up its shape.
///
/// Whichever yields more triangles wins. The result is kept on disk: a large part takes seconds, and the pane, the
/// thumbnail and the gallery ask for the same file at once.
/// </summary>
public static class CadMeshSource
{
	private const string FormatVersion = "cadmesh1";
	private const string HelperName = "cadmpeg.exe";

	/// <summary>
	/// Formats confirmed on real documents: parts from SolidWorks, Rhino, FreeCAD, Siemens NX and CATIA. The helper
	/// claims to read Creo as well, but on genuine NIST parts it gives back a handful of triangles instead of a shape,
	/// and a wrong shape is worse than none — so Creo is left out until it can be seen working.
	///
	/// Note that ".prt" belongs to NX here: Creo keeps the version after its extension, as in "part.prt.3", which is
	/// not this extension at all.
	/// </summary>
	private static readonly string[] SupportedExtensions = [".sldprt", ".3dm", ".fcstd", ".prt", ".catpart"];

	/// <summary>Below this, a document is treated as having no usable mesh of its own and the surfaces are tried instead.</summary>
	private const int MeaningfulTriangles = 8;

	private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);

	// The helper is heavy; a folder full of parts must not start a dozen of them at once
	private static readonly SemaphoreSlim Slots = new(2);

	private static string? probedPath;
	private static bool helperExists;

	public static string HelperPath { get; set; } = FindDefaultHelper();

	public static string CacheDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "Consysto.CadPreview", "cad");

	public static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);

	/// <summary>A document whose geometry this build can read; false when the helper is not deployed.</summary>
	public static bool IsSupported(string? extension)
		=> extension is not null && SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) && HelperExists();

	public static Mesh3D Load(string path, CancellationToken cancellationToken = default)
	{
		try
		{
			var file = new FileInfo(path);
			Directory.CreateDirectory(CacheDirectory);
			var cachePath = Path.Combine(CacheDirectory, CacheKey(file) + ".cadmesh");

			lock (Gates.GetOrAdd(cachePath, _ => new object()))
			{
				if (File.Exists(cachePath))
				{
					try
					{
						return MeshCache.Read(cachePath);
					}
					catch (InvalidDataException)
					{
						File.Delete(cachePath);
					}
				}

				Slots.Wait(cancellationToken);
				try
				{
					var mesh = Decode(file.FullName, cancellationToken);
					MeshCache.Write(cachePath, mesh);
					return mesh;
				}
				finally
				{
					Slots.Release();
				}
			}
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception)
		{
			// A document this build cannot decode is not a failure of the program: the saved picture is shown instead
			return Mesh3D.Empty;
		}
	}

	private static Mesh3D Decode(string input, CancellationToken cancellationToken)
	{
		var carried = FromCarriedMesh(input, cancellationToken);
		if (carried.Vertices.Length / 3 >= MeaningfulTriangles)
			return carried;

		var surfaces = FromSurfaces(input, cancellationToken);

		return surfaces.Vertices.Length >= carried.Vertices.Length ? surfaces : carried;
	}

	/// <summary>The mesh the document carries: nothing is computed, so this is the cheap road and it is tried first.</summary>
	private static Mesh3D FromCarriedMesh(string input, CancellationToken cancellationToken)
	{
		var json = Temporary(".json");
		try
		{
			return Run(["dump", input, "-o", json], cancellationToken) && File.Exists(json)
				? ReadTriangles(json)
				: Mesh3D.Empty;
		}
		finally
		{
			Forget(json);
			Forget(Path.ChangeExtension(json, ".fidelity.json"));
		}
	}

	/// <summary>
	/// The exact surfaces, through STEP into the OpenCascade engine. Errors in the source geometry are allowed through:
	/// a document that a strict exporter would refuse is still worth looking at.
	/// </summary>
	private static Mesh3D FromSurfaces(string input, CancellationToken cancellationToken)
	{
		if (!StepMeshSource.IsSupported(".step"))
			return Mesh3D.Empty;

		var step = Temporary(".step");
		var mesh = Temporary(".csmesh");
		try
		{
			if (!Run(["convert", input, "-o", step, "--allow-errors"], cancellationToken) || !File.Exists(step))
				return Mesh3D.Empty;

			StepMeshSource.RunMesher(step, mesh, cancellationToken);

			return File.Exists(mesh) ? MeshCache.Read(mesh) : Mesh3D.Empty;
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			return Mesh3D.Empty;
		}
		finally
		{
			Forget(step);
			Forget(mesh);
		}
	}

	private static bool Run(string[] arguments, CancellationToken cancellationToken)
	{
		var start = new ProcessStartInfo(HelperPath)
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		foreach (var argument in arguments)
			start.ArgumentList.Add(argument);

		using var process = Process.Start(start) ?? throw new InvalidOperationException("cadmpeg did not start");
		using var stopping = cancellationToken.Register(() => KillQuietly(process));

		// The helper is talkative about every flaw it finds in a document. Its output has to be drained while it runs:
		// a full pipe stops the process dead, and the wait below would then always end in the timeout.
		var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
		var errors = process.StandardError.ReadToEndAsync(cancellationToken);

		if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
		{
			KillQuietly(process);
			return false;
		}

		Task.WaitAll([output, errors], TimeSpan.FromSeconds(5));

		cancellationToken.ThrowIfCancellationRequested();

		return process.ExitCode == 0;
	}

	/// <summary>Takes the triangles out of the JSON of the tool and leaves everything else in it alone.</summary>
	private static Mesh3D ReadTriangles(string jsonPath)
	{
		using var stream = File.OpenRead(jsonPath);
		using var document = JsonDocument.Parse(stream);

		if (!document.RootElement.TryGetProperty("model", out var model)
			|| !model.TryGetProperty("tessellations", out var tessellations)
			|| tessellations.ValueKind != JsonValueKind.Array)
			return Mesh3D.Empty;

		var vertices = new List<Vector3>();
		foreach (var piece in tessellations.EnumerateArray())
		{
			if (!piece.TryGetProperty("vertices", out var points) || !piece.TryGetProperty("triangles", out var faces))
				continue;

			var corners = new List<Vector3>();
			foreach (var point in points.EnumerateArray())
				corners.Add(ReadPoint(point));

			foreach (var face in faces.EnumerateArray())
			{
				if (face.ValueKind != JsonValueKind.Array || face.GetArrayLength() < 3)
					continue;

				foreach (var index in face.EnumerateArray())
				{
					if (!index.TryGetInt32(out var corner) || corner < 0 || corner >= corners.Count)
						return Mesh3D.Empty;

					vertices.Add(corners[corner]);
				}

				if (vertices.Count / 3 > MeshBuilder.MaximumTriangles)
					return Mesh3D.Empty;
			}
		}

		// These documents stand on the XY plane, as 3MF build plates do
		return new Mesh3D([.. vertices], MeshUp.Z);
	}

	private static Vector3 ReadPoint(JsonElement point)
		=> new(
			(float)point.GetProperty("x").GetDouble(),
			(float)point.GetProperty("y").GetDouble(),
			(float)point.GetProperty("z").GetDouble());

	private static string Temporary(string extension)
		=> Path.Combine(CacheDirectory, Path.GetRandomFileName() + extension);

	private static void Forget(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch (Exception)
		{
		}
	}

	private static void KillQuietly(Process process)
	{
		try
		{
			if (!process.HasExited)
				process.Kill(entireProcessTree: true);
		}
		catch (Exception)
		{
		}
	}

	private static bool HelperExists()
	{
		if (probedPath != HelperPath)
		{
			probedPath = HelperPath;
			helperExists = !string.IsNullOrEmpty(HelperPath) && File.Exists(HelperPath);
		}

		return helperExists;
	}

	private static string FindDefaultHelper()
		=> Path.Combine(AppContext.BaseDirectory, "CadHelpers", HelperName);

	private static string CacheKey(FileInfo file)
	{
		var identity = $"{file.FullName.ToUpperInvariant()}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{FormatVersion}";
		return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(identity)));
	}
}
