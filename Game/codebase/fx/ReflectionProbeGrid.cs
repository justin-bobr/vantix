/*
 * License: Apache-2.0
 * Copyright 2026 Stefan Kalysta (stefan@redninjas.dev)
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using Godot;
using System.Collections.Generic;

namespace Vantix.Fx;

/// <summary>Procedurally fills the scene with a grid of box-projected ReflectionProbes. It measures the combined
/// world AABB of every MeshInstance3D under BoundsSource (or the scene root), divides it into cells of roughly
/// CellSize, and spawns one probe per cell sized with Overlap so neighbours blend. Probes are generated on load and
/// via the Rebuild toggle; they are not serialized into the scene. Keep the total under the project's reflection
/// atlas count (rendering/reflections/reflection_atlas/reflection_count) — MaxProbes clamps to it.</summary>
[Tool]
[GlobalClass]
public partial class ReflectionProbeGrid : Node3D
{
	[Export] public Node3D BoundsSource { get; set; }
	[Export] public bool AutoCellSize { get; set; } = true;
	[Export(PropertyHint.Range, "1,32,1")] public int TargetProbes { get; set; } = 16;
	[Export(PropertyHint.Range, "2,80,0.5")] public float CellSize { get; set; } = 24f;
	[Export(PropertyHint.Range, "1,8,1")] public int VerticalCells { get; set; } = 1;
	[Export(PropertyHint.Range, "0,1,0.05")] public float Overlap { get; set; } = 0.25f;
	[Export(PropertyHint.Range, "0,10,0.1")] public float Padding { get; set; } = 1f;
	[Export(PropertyHint.Range, "1,32,1")] public int MaxProbes { get; set; } = 32;
	[Export] public bool Interior { get; set; } = true;
	[Export] public bool BoxProjection { get; set; } = true;
	[Export] public ReflectionProbe.UpdateModeEnum UpdateMode { get; set; } = ReflectionProbe.UpdateModeEnum.Once;
	[Export(PropertyHint.Range, "0,4,0.05")] public float Intensity { get; set; } = 1f;
	[Export] public bool Rebuild { get => false; set { if (value) Callable.From(Generate).CallDeferred(); } }

	public override void _Ready()
	{
		if (!HasProbes()) Generate();
	}

	private void Generate()
	{
		ClearProbes();
		if (!TryComputeBounds(out Aabb bounds)) return;
		bounds = bounds.Grow(Padding);
		Vector3 size = bounds.Size;
		if (size.X <= 0f || size.Y <= 0f || size.Z <= 0f) return;

		float cellSize = CellSize;
		if (AutoCellSize)
		{
			int target = Mathf.Clamp(TargetProbes, 1, MaxProbes);
			cellSize = Mathf.Sqrt(size.X * size.Z / target);
		}
		if (cellSize <= 0f) return;

		int nx = Mathf.Max(1, Mathf.RoundToInt(size.X / cellSize));
		int ny = Mathf.Max(1, VerticalCells);
		int nz = Mathf.Max(1, Mathf.RoundToInt(size.Z / cellSize));
		while (nx * ny * nz > MaxProbes && (nx > 1 || nz > 1))
		{
			if (nx >= nz && nx > 1) nx--;
			else if (nz > 1) nz--;
			else break;
		}

		Vector3 cell = new(size.X / nx, size.Y / ny, size.Z / nz);
		Vector3 probeSize = cell * (1f + Overlap);
		Node owner = ResolveOwner();

		for (int x = 0; x < nx; x++)
			for (int y = 0; y < ny; y++)
				for (int z = 0; z < nz; z++)
				{
					Vector3 center = bounds.Position + new Vector3(
						(x + 0.5f) * cell.X, (y + 0.5f) * cell.Y, (z + 0.5f) * cell.Z);
					var probe = new ReflectionProbe
					{
						Name = $"Probe_{x}_{y}_{z}",
						Size = probeSize,
						BoxProjection = BoxProjection,
						Interior = Interior,
						UpdateMode = UpdateMode,
						Intensity = Intensity,
					};
					AddChild(probe);
					probe.GlobalTransform = new Transform3D(Basis.Identity, center);
					if (owner != null) probe.Owner = owner;
				}
	}

	private bool HasProbes()
	{
		foreach (Node child in GetChildren())
			if (child is ReflectionProbe) return true;
		return false;
	}

	private Node ResolveOwner()
	{
		if (Owner != null) return Owner;
		return Engine.IsEditorHint() ? GetTree()?.EditedSceneRoot : null;
	}

	private void ClearProbes()
	{
		foreach (Node child in GetChildren())
			if (child is ReflectionProbe probe)
			{
				RemoveChild(probe);
				probe.QueueFree();
			}
	}

	private bool TryComputeBounds(out Aabb result)
	{
		result = default;
		Node root = BoundsSource;
		root ??= Engine.IsEditorHint() ? GetTree()?.EditedSceneRoot : GetTree()?.CurrentScene;
		root ??= Owner;
		root ??= GetParent();
		if (root == null) return false;

		bool has = false;
		var stack = new Stack<Node>();
		stack.Push(root);
		while (stack.Count > 0)
		{
			Node n = stack.Pop();
			if (n != this && n is MeshInstance3D mi && mi.Visible && mi.Mesh != null)
			{
				Aabb world = XformAabb(mi.GlobalTransform, mi.GetAabb());
				result = has ? result.Merge(world) : world;
				has = true;
			}
			foreach (Node c in n.GetChildren()) stack.Push(c);
		}
		return has;
	}

	private static Aabb XformAabb(Transform3D t, Aabb a)
	{
		Aabb r = new(t * a.Position, Vector3.Zero);
		for (int i = 1; i < 8; i++)
		{
			Vector3 corner = a.Position + new Vector3(
				(i & 1) != 0 ? a.Size.X : 0f,
				(i & 2) != 0 ? a.Size.Y : 0f,
				(i & 4) != 0 ? a.Size.Z : 0f);
			r = r.Expand(t * corner);
		}
		return r;
	}
}
