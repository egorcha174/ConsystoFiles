namespace Consysto.CadPreview.Mesh;

/// <summary>
/// Locks that keep two loads of the same file from building its cache entry at once. A fixed set shared by hash:
/// a lock per file kept in a dictionary would grow with every file the program ever previewed.
/// </summary>
internal sealed class MeshGates
{
	private readonly object[] gates = new object[64];

	public MeshGates()
	{
		for (int i = 0; i < gates.Length; i++)
			gates[i] = new object();
	}

	/// <summary>Two files may share a lock; then one waits for the other, which only costs time.</summary>
	public object For(string cachePath)
		=> gates[(StringComparer.OrdinalIgnoreCase.GetHashCode(cachePath) & int.MaxValue) % gates.Length];
}
