using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Consysto.CadPreview.Mesh;

namespace Consysto.CadPreview.SolidWorks;

/// <summary>
/// Real geometry of a SolidWorks part, so it can be turned in the hand rather than looked at as a picture.
///
/// The reading is done by cadmpeg (Apache-2.0) in a helper process: a file that sends it into a corner cannot take the
/// program down with it. What comes back is the JSON of that tool, out of which only the triangles are taken. The result
/// is kept, because a large part takes seconds and both the pane and the thumbnail ask for the same file at once.
/// </summary>
public static class SolidWorksMeshSource
{
	private const string FormatVersion = "swmesh1";
	private const string HelperName = "cadmpeg.exe";

	/// <summary>Only parts. Assemblies and drawings are not decoded by this tool, and their saved picture is shown instead.</summary>
	private static readonly string[] SupportedExtensions = [".sldprt"];

	private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);

	// The helper is heavy; a folder full of parts must not start a dozen of them at once
	private static readonly SemaphoreSlim Slots = new(2);

	private static string? probedPath;
	private static bool helperExists;

	public static string HelperPath { get; set; } = FindDefaultHelper();

	public static string CacheDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "Consysto.CadPreview", "solidworks");

	public static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);

	/// <summary>A part whose geometry this build can read; false when the helper is not deployed.</summary>
	public static bool IsSupported(string? extension)
		=> extension is not null && SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) && HelperExists();

	public static Mesh3D Load(string path, CancellationToken cancellationToken = default)
	{
		try
		{
			var file = new FileInfo(path);
			Directory.CreateDirectory(CacheDirectory);
			var cachePath = Path.Combine(CacheDirectory, CacheKey(file) + ".swmesh");

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
					var mesh = RunHelper(file.FullName, cancellationToken);
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
			// A part this build cannot decode is not a failure of the program: the saved picture is shown instead
			return Mesh3D.Empty;
		}
	}

	private static Mesh3D RunHelper(string input, CancellationToken cancellationToken)
	{
		var json = Path.Combine(CacheDirectory, Path.GetRandomFileName() + ".json");
		try
		{
			var start = new ProcessStartInfo(HelperPath)
			{
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
			};
			start.ArgumentList.Add("dump");
			start.ArgumentList.Add(input);
			start.ArgumentList.Add("-o");
			start.ArgumentList.Add(json);

			using var process = Process.Start(start) ?? throw new InvalidOperationException("cadmpeg did not start");
			using var stopping = cancellationToken.Register(() => KillQuietly(process));

			if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
			{
				KillQuietly(process);
				return Mesh3D.Empty;
			}

			cancellationToken.ThrowIfCancellationRequested();

			return process.ExitCode == 0 && File.Exists(json) ? ReadTriangles(json) : Mesh3D.Empty;
		}
		finally
		{
			// The tool writes a companion file of its own next to the output
			foreach (var leftover in new[] { json, Path.ChangeExtension(json, ".fidelity.json") })
				TryDelete(leftover);
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

	private static void TryDelete(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch (Exception)
		{
		}
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

		// SolidWorks parts stand on the XY plane, as 3MF build plates do
		return new Mesh3D([.. vertices], MeshUp.Z);
	}

	private static Vector3 ReadPoint(JsonElement point)
		=> new(
			(float)point.GetProperty("x").GetDouble(),
			(float)point.GetProperty("y").GetDouble(),
			(float)point.GetProperty("z").GetDouble());

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
