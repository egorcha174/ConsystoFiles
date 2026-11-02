using System.Numerics;

namespace Consysto.CadPreview.Mesh;

/// <summary>
/// Boxes around groups of triangles, nested, so a pick tests the few triangles near the line of sight instead of all
/// of them. Built once per mesh, split at the middle of the longest side of each box.
/// </summary>
internal sealed class TriangleTree
{
	private const int LeafTriangles = 8;

	private readonly Vector3[] vertices;
	private readonly int[] order;
	private readonly List<Node> nodes;

	public TriangleTree(Vector3[] vertices, Vector3[] normals)
	{
		this.vertices = vertices;

		// Triangles without an area cannot be hit; they are left out of the tree altogether.
		var usable = new List<int>(normals.Length);
		for (int t = 0; t < normals.Length; t++)
		{
			if (normals[t] != Vector3.Zero)
				usable.Add(t);
		}

		order = [.. usable];
		var centres = new Vector3[normals.Length];
		foreach (int t in order)
			centres[t] = (vertices[t * 3] + vertices[t * 3 + 1] + vertices[t * 3 + 2]) / 3;

		nodes = new List<Node>(Math.Max(1, order.Length / LeafTriangles * 2));
		if (order.Length == 0)
			return;

		nodes.Add(default);
		var pending = new Stack<(int Node, int Start, int Count)>();
		pending.Push((0, 0, order.Length));
		while (pending.Count > 0)
		{
			var (node, start, count) = pending.Pop();
			var (min, max) = Bounds(start, count);
			if (count <= LeafTriangles)
			{
				nodes[node] = new Node(min, max, start, count, -1);
				continue;
			}

			int split = Split(centres, start, count);
			int left = nodes.Count;
			nodes.Add(default);
			int right = nodes.Count;
			nodes.Add(default);
			nodes[node] = new Node(min, max, start, 0, left);
			pending.Push((left, start, split - start));
			pending.Push((right, split, start + count - split));
		}
	}

	/// <summary>The nearest triangle the ray crosses at or beyond its origin, with the distance along it.</summary>
	public int Intersect(Vector3 origin, Vector3 direction, out float nearest)
	{
		nearest = float.PositiveInfinity;
		if (nodes.Count == 0)
			return -1;

		var inverse = new Vector3(1f / direction.X, 1f / direction.Y, 1f / direction.Z);
		int hit = -1;
		var pending = new Stack<int>();
		pending.Push(0);
		while (pending.Count > 0)
		{
			var node = nodes[pending.Pop()];
			if (!Crosses(node, origin, inverse, nearest))
				continue;

			if (node.First < 0)
			{
				for (int i = node.Start; i < node.Start + node.Count; i++)
				{
					int t = order[i];
					if (MeshMeasure.Intersect(origin, direction, vertices[t * 3], vertices[t * 3 + 1], vertices[t * 3 + 2], out float distance)
						&& distance >= 0 && distance < nearest)
					{
						nearest = distance;
						hit = t;
					}
				}

				continue;
			}

			pending.Push(node.First + 1);
			pending.Push(node.First);
		}

		return hit;
	}

	// Slab test. A direction parallel to an axis gives infinite inverses; a box face exactly on the origin then gives
	// NaN, and such a box is searched rather than skipped.
	private static bool Crosses(Node node, Vector3 origin, Vector3 inverse, float limit)
	{
		var near = (node.Min - origin) * inverse;
		var far = (node.Max - origin) * inverse;
		var entry = Vector3.Min(near, far);
		var exit = Vector3.Max(near, far);
		float enter = MathF.Max(MathF.Max(entry.X, entry.Y), MathF.Max(entry.Z, 0));
		float leave = MathF.Min(MathF.Min(exit.X, exit.Y), MathF.Min(exit.Z, limit));
		return float.IsNaN(enter) || float.IsNaN(leave) || enter <= leave;
	}

	private (Vector3 Min, Vector3 Max) Bounds(int start, int count)
	{
		var min = new Vector3(float.PositiveInfinity);
		var max = new Vector3(float.NegativeInfinity);
		for (int i = start; i < start + count; i++)
		{
			int t = order[i];
			for (int k = 0; k < 3; k++)
			{
				min = Vector3.Min(min, vertices[t * 3 + k]);
				max = Vector3.Max(max, vertices[t * 3 + k]);
			}
		}

		return (min, max);
	}

	// Splits at the middle of the centres' spread along its longest side; when every centre falls on one side, in half.
	private int Split(Vector3[] centres, int start, int count)
	{
		var min = new Vector3(float.PositiveInfinity);
		var max = new Vector3(float.NegativeInfinity);
		for (int i = start; i < start + count; i++)
		{
			min = Vector3.Min(min, centres[order[i]]);
			max = Vector3.Max(max, centres[order[i]]);
		}

		var size = max - min;
		int axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;
		float middle = (Component(min, axis) + Component(max, axis)) / 2;

		int low = start, high = start + count - 1;
		while (low <= high)
		{
			if (Component(centres[order[low]], axis) < middle)
			{
				low++;
			}
			else
			{
				(order[low], order[high]) = (order[high], order[low]);
				high--;
			}
		}

		return low == start || low == start + count ? start + count / 2 : low;
	}

	private static float Component(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

	/// <param name="First">The first of two children, which sit side by side; -1 for a leaf.</param>
	private readonly record struct Node(Vector3 Min, Vector3 Max, int Start, int Count, int First);
}
