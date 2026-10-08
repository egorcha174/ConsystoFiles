using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Consysto.CadPreview.Mesh;

namespace Consysto.CadPreview.Parasolid;

/// <summary>Triangle-only Parasolid previews from the bundled, bounded helper and its built-in profiles.</summary>
public static class ParasolidMeshSource
{
	private const long MaximumInputBytes = 32 * 1024 * 1024;
	private const int MaximumTriangles = 100_000;
	private const string FormatVersion = "parasolid-kit-0.3.7-preview1";
	private static readonly SemaphoreSlim Slot = new(1);
	private static readonly string[] Extensions = [".x_t", ".x_b", ".xmt_txt", ".xmt_bin"];

	public static string HelperPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "Parasolid", "Consysto.ParasolidMesher.exe");
	public static string CacheDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "Consysto.CadPreview", "parasolid");
	public static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(20);

	// Admission depends on the format, not on installation: a missing helper yields the same unsupported state.
	public static bool IsSupported(string? extension)
		=> extension is not null && Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

	public static Mesh3D Load(string path, CancellationToken cancellation = default)
	{
		Slot.Wait(cancellation);
		string? work = null;
		try
		{
			var file = new FileInfo(path);
			if (!file.Exists || file.Length == 0 || file.Length > MaximumInputBytes || !File.Exists(HelperPath))
				return Mesh3D.Empty;

			Directory.CreateDirectory(CacheDirectory);
			string identity = $"{file.FullName.ToUpperInvariant()}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{FormatVersion}";
			string cache = Path.Combine(CacheDirectory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))) + ".csmesh");
			if (File.Exists(cache))
			{
				try
				{
					var cached = ReadMesh(cache);
					if (!cached.IsEmpty)
						return cached;
				}
				catch (IOException) { }
			}

			work = Path.Combine(CacheDirectory, Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(work);
			string input = Path.Combine(work, "input" + file.Extension.ToLowerInvariant());
			string output = Path.Combine(work, "preview.csmesh");
			// The helper only sees a copy: it cannot change the user's original file.
			using (var source = File.Open(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read))
			using (var copy = File.Create(input))
			{
				if (source.Length > MaximumInputBytes)
					return Mesh3D.Empty;
				source.CopyTo(copy);
			}
			var start = new ProcessStartInfo(HelperPath)
			{
				UseShellExecute = false,
				CreateNoWindow = true,
				WorkingDirectory = Path.GetDirectoryName(HelperPath)!,
			};
			start.ArgumentList.Add(input);
			start.ArgumentList.Add(output);
			using var process = Process.Start(start) ?? throw new InvalidOperationException("Parasolid helper did not start");
			using var stopping = cancellation.Register(() => Stop(process));
			if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
			{
				Stop(process);
				return Mesh3D.Empty;
			}
			cancellation.ThrowIfCancellationRequested();
			if (process.ExitCode != 0 || !File.Exists(output))
				return Mesh3D.Empty;
			var mesh = ReadMesh(output);
			if (!mesh.IsEmpty)
				File.Copy(output, cache, overwrite: true);
			return mesh;
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			return Mesh3D.Empty;
		}
		finally
		{
			if (work is not null)
			{
				try { Directory.Delete(work, recursive: true); }
				catch (IOException) { }
				catch (UnauthorizedAccessException) { }
			}
			Slot.Release();
		}
	}

	private static Mesh3D ReadMesh(string path)
	{
		using var stream = File.OpenRead(path);
		if (stream.Length < 16 || stream.Length > 16 + (long)MaximumTriangles * 36)
			return Mesh3D.Empty;
		using var reader = new BinaryReader(stream);
		if (!reader.ReadBytes(8).AsSpan().SequenceEqual("CSMESH1\0"u8))
			return Mesh3D.Empty;
		uint count = reader.ReadUInt32();
		if (count == 0 || count > MaximumTriangles || stream.Length != 16 + (long)count * 36)
			return Mesh3D.Empty;
		reader.ReadUInt32();
		var vertices = new Vector3[count * 3];
		for (int i = 0; i < vertices.Length; i++)
		{
			var point = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
			if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z))
				return Mesh3D.Empty;
			vertices[i] = point;
		}
		var mesh = new Mesh3D(vertices, MeshUp.Z);
		return float.IsFinite(mesh.Radius) && mesh.Radius > 0 ? mesh : Mesh3D.Empty;
	}

	private static void Stop(Process process)
	{
		try
		{
			if (!process.HasExited)
				process.Kill(entireProcessTree: true);
			process.WaitForExit(1000);
		}
		catch (InvalidOperationException) { }
		catch (System.ComponentModel.Win32Exception) { }
	}
}
