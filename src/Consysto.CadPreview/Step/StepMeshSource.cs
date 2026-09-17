using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Consysto.CadPreview.Mesh;

namespace Consysto.CadPreview.Step;

/// <summary>
/// STEP and IGES through OpenCascade. OCCT runs in a helper process (Consysto.StepMesher.exe, native\StepMesher):
/// a crash or a runaway file inside it cannot take the host down, and the LGPL library stays a separate set of DLLs.
/// Meshes are cached on disk because tessellating an assembly takes seconds.
/// </summary>
public static class StepMeshSource
{
    private const string FormatVersion = "csmesh1";
    private const int HeaderBytes = 16;
    private const int TriangleBytes = 36;

    private static readonly string[] SupportedExtensions = [".step", ".stp", ".iges", ".igs"];

    // One mesher run per file at a time: the pane and the thumbnail often ask for the same file together.
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);

    // Each run already meshes on every core; a folder full of STEP files must not start a dozen of them at once.
    private static readonly SemaphoreSlim MesherSlots = new(2);

    private static string? probedMesherPath;
    private static bool mesherExists;

    /// <summary>The helper together with the OCCT DLLs; by default found where Consysto.CadPreview.WinUI ships it.</summary>
    public static string MesherPath { get; set; } = FindDefaultMesher();

    public static string CacheDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "Consysto.CadPreview", "meshes");

    public static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>False when the helper is not deployed, so the host falls back to whatever else can show the file.</summary>
    public static bool IsSupported(string? extension)
        => extension is not null && SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) && MesherExists();

    public static Mesh3D Load(string path, CancellationToken cancellationToken = default)
    {
        var file = new FileInfo(path);
        Directory.CreateDirectory(CacheDirectory);
        string cachePath = Path.Combine(CacheDirectory, CacheKey(file) + ".csmesh");

        lock (Gates.GetOrAdd(cachePath, _ => new object()))
        {
            if (!File.Exists(cachePath))
            {
                MesherSlots.Wait(cancellationToken);
                try
                {
                    RunMesher(file.FullName, cachePath, cancellationToken);
                }
                finally
                {
                    MesherSlots.Release();
                }
            }

            try
            {
                return ReadMesh(cachePath);
            }
            catch (InvalidDataException)
            {
                // A damaged cache entry must not stick: the next attempt meshes the file again.
                File.Delete(cachePath);
                throw;
            }
        }
    }

    // Windows App SDK puts a library's content under a folder named after the library, both in the build output and in
    // the MSIX package: Files gets "Consysto.CadPreview.WinUI\occt". Unpackaged hosts also get a copy at the root.
    private static string FindDefaultMesher()
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "occt", "Consysto.StepMesher.exe"),
            Path.Combine(AppContext.BaseDirectory, "Consysto.CadPreview.WinUI", "occt", "Consysto.StepMesher.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private static bool MesherExists()
    {
        string path = MesherPath;
        if (!string.Equals(path, probedMesherPath, StringComparison.Ordinal))
        {
            mesherExists = File.Exists(path);
            probedMesherPath = path;
        }

        return mesherExists;
    }

    private static void RunMesher(string input, string output, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(MesherPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(MesherPath)!,
        };
        start.ArgumentList.Add(input);
        start.ArgumentList.Add(output);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("The STEP mesher did not start.");
        try
        {
            // Tessellation uses every core; the file manager must stay responsive meanwhile.
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (InvalidOperationException)
        {
            // Already finished.
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);
        try
        {
            process.WaitForExitAsync(deadline.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Exited on its own in the meantime.
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"The STEP mesher did not finish within {Timeout.TotalSeconds:0} s.");
        }

        if (process.ExitCode != 0)
            throw new InvalidDataException($"The STEP mesher failed: {DescribeExitCode(process.ExitCode)}.");
    }

    private static string DescribeExitCode(int code) => code switch
    {
        2 => "wrong arguments",
        3 => "the file could not be read",
        4 => "the file has no geometry",
        5 => "the mesh could not be written",
        6 => $"more than {MeshBuilder.MaximumTriangles} triangles",
        _ => $"exit code 0x{code:X8}",
    };

    private static Mesh3D ReadMesh(string cachePath)
    {
        var bytes = File.ReadAllBytes(cachePath);
        if (bytes.Length < HeaderBytes || !bytes.AsSpan(0, 8).SequenceEqual("CSMESH1\0"u8))
            throw new InvalidDataException("The cached STEP mesh is damaged.");

        uint triangles = BitConverter.ToUInt32(bytes, 8);
        if (triangles > MeshBuilder.MaximumTriangles || HeaderBytes + (long)triangles * TriangleBytes != bytes.Length)
            throw new InvalidDataException("The cached STEP mesh is damaged.");

        // Nine little-endian float32 per triangle map straight onto three Vector3.
        var vertices = new Vector3[triangles * 3];
        MemoryMarshal.Cast<byte, Vector3>(bytes.AsSpan(HeaderBytes)).CopyTo(vertices);
        // Inventor exports STEP in its own frame, Y up.
        return new Mesh3D(vertices, MeshUp.Y);
    }

    private static string CacheKey(FileInfo file)
    {
        var identity = $"{file.FullName.ToUpperInvariant()}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{FormatVersion}";
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(identity)));
    }
}
