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

using System.Collections.Generic;
using Godot;

namespace Vantix.Levels;

/// <summary>Per-map registry on the map root. Holds NodePath arrays to the map's markers — Spawns, Zones,
/// BombSpots, preview cameras — and resolves them into typed lists once on enter-tree. Wired in the
/// inspector or baked via CollectChildren; no runtime group scans. Accessed globally via World.Level.</summary>
[Tool, GlobalClass]
public partial class Level : Node3D
{
	/// <summary>Paths (relative to this node) to every Spawn marker.</summary>
	[Export]
	public NodePath[] SpawnPaths { get; set; } = System.Array.Empty<NodePath>();

	/// <summary>Paths to every Zone region (HUD area names + bot nav targets).</summary>
	[Export]
	public NodePath[] ZonePaths { get; set; } = System.Array.Empty<NodePath>();

	/// <summary>Paths to every BombSpot (A/B/C plant regions).</summary>
	[Export]
	public NodePath[] BombSpotPaths { get; set; } = System.Array.Empty<NodePath>();

	/// <summary>Paths to the preview cameras the team-select screen cycles.</summary>
	[Export]
	public NodePath[] PreviewCamPaths { get; set; } = System.Array.Empty<NodePath>();

	/// <summary>The floor's wet ShaderMaterial. Footsteps (nodes in the puddle_feet group) are fed into its
	/// impacts so the puddles ripple under every player. Leave null on maps without a wet floor — the feed
	/// is then skipped.</summary>
	[Export]
	public ShaderMaterial WetFloorMaterial { get; set; }

	/// <summary>Inspector "button": tick to (re)populate the four path arrays from descendants by type.
	/// Reads back false so it acts as a one-shot, not a stored flag.</summary>
	[Export]
	public bool CollectChildren
	{
		get => false;
		set
		{
			if (value && Engine.IsEditorHint())
				BakePathsFromDescendants();
		}
	}

	private readonly List<Spawn> _spawns = new();
	private readonly List<Zone> _zones = new();
	private readonly List<BombSpot> _bombSpots = new();
	private readonly List<Camera3D> _previewCams = new();

	// Zones + Spawns combined so ZoneAt() also resolves a spawn-area to its name.
	private readonly List<Zone> _zoneLookup = new();

	private static readonly StringName PuddleImpactsParam = "impacts";
	private static readonly StringName PuddleFeetGroup = "puddle_feet";
	private const int PuddleSlots = 16;
	private const float PuddleMinStepDistance = 0.25f;
	private const float PuddleMinInterval = 0.05f;
	private readonly Vector4[] _puddleImpacts = new Vector4[PuddleSlots];
	private readonly Dictionary<ulong, Vector3> _puddleLastPos = new();
	private int _puddleNext;
	private double _puddleAccum;

	/// <summary>True once the path arrays have been turned into live node lists.</summary>
	public bool Resolved { get; private set; }

	public IReadOnlyList<Zone> Zones => _zones;
	public IReadOnlyList<BombSpot> BombSpots => _bombSpots;
	public IReadOnlyList<Camera3D> PreviewCams => _previewCams;

	public override void _Ready()
	{
		if (Engine.IsEditorHint())
			return;
		EnsureResolved();
	}

	/// <summary>Feeds each tracked foot's ground position into the wet-floor shader as timestamped impacts so the
	/// puddles ripple under every step — throttled by interval and min step distance. Skipped in-editor and when
	/// the map has no wet floor.</summary>
	public override void _Process(double delta)
	{
		if (Engine.IsEditorHint() || WetFloorMaterial == null)
			return;
		_puddleAccum += delta;
		if (_puddleAccum < PuddleMinInterval)
			return;
		_puddleAccum = 0;
		float now = Time.GetTicksMsec() / 1000.0f;
		bool changed = false;
		foreach (var node in GetTree().GetNodesInGroup(PuddleFeetGroup))
		{
			if (node is not Node3D foot)
				continue;
			ulong id = foot.GetInstanceId();
			Vector3 pos = foot.GlobalPosition;
			if (_puddleLastPos.TryGetValue(id, out var last)
				&& pos.DistanceSquaredTo(last) < PuddleMinStepDistance * PuddleMinStepDistance)
				continue;
			_puddleLastPos[id] = pos;
			_puddleImpacts[_puddleNext] = new Vector4(pos.X, pos.Z, 0.0f, now);
			_puddleNext = (_puddleNext + 1) % PuddleSlots;
			changed = true;
		}
		if (changed)
			WetFloorMaterial.SetShaderParameter(PuddleImpactsParam, _puddleImpacts);
	}

	/// <summary>Resolves the exported path arrays into typed node lists. Idempotent. Called from _Ready
	/// and lazily from World.Level for consumers querying before _Ready.</summary>
	public void EnsureResolved()
	{
		if (Resolved)
			return;
		ResolveList(SpawnPaths, _spawns);
		ResolveList(ZonePaths, _zones);
		ResolveList(BombSpotPaths, _bombSpots);
		ResolveList(PreviewCamPaths, _previewCams);

		_zoneLookup.Clear();
		_zoneLookup.AddRange(_zones);
		_zoneLookup.AddRange(_spawns);

		Resolved = true;
		Dbg.Print(
			$"[Level] {Name}: {_spawns.Count} Spawn(s), {_zones.Count} Zone(s), "
				+ $"{_bombSpots.Count} BombSpot(s), {_previewCams.Count} PreviewCam(s)"
		);
	}

	private void ResolveList<T>(NodePath[] paths, List<T> dst)
		where T : Node
	{
		dst.Clear();
		if (paths == null)
			return;
		foreach (var p in paths)
		{
			if (p == null || p.IsEmpty)
				continue;
			var n = GetNodeOrNull<T>(p);
			if (n != null)
				dst.Add(n);
			else
				GD.PushWarning($"[Level] {Name}: path \"{p}\" did not resolve to a {typeof(T).Name}");
		}
	}

	/// <summary>Returns the smallest-volume Zone/Spawn area containing the world position (innermost nested
	/// zone wins), or null when outside every region.</summary>
	public Zone ZoneAt(Vector3 worldPos)
	{
		Zone best = null;
		float bestVol = float.MaxValue;
		foreach (var z in _zoneLookup)
		{
			if (!GodotObject.IsInstanceValid(z))
				continue;
			Vector3 local = z.GlobalTransform.AffineInverse() * worldPos;
			Vector3 half = z.Size * 0.5f;
			if (Mathf.Abs(local.X) > half.X || Mathf.Abs(local.Y) > half.Y || Mathf.Abs(local.Z) > half.Z)
				continue;
			float vol = z.Size.X * z.Size.Y * z.Size.Z;
			if (vol < bestVol)
			{
				best = z;
				bestVol = vol;
			}
		}
		return best;
	}

	/// <summary>Returns the first BombSpot with the matching slot, or null if the map has none.</summary>
	public BombSpot BombSpotForSlot(BombSpot.BombSlot slot)
	{
		foreach (var bs in _bombSpots)
			if (bs.Slot == slot)
				return bs;
		return null;
	}

	/// <summary>Lazy enumeration of every Spawn with the matching kind.</summary>
	public IEnumerable<Spawn> SpawnsForKind(Spawn.SpawnKind kind)
	{
		foreach (var sp in _spawns)
			if (sp.Kind == kind)
				yield return sp;
	}

	/// <summary>Editor-only: walks descendants and rewrites the four path arrays by type. BombSpot/Spawn
	/// extend Zone, so they're tested first.</summary>
	private void BakePathsFromDescendants()
	{
		var spawns = new List<NodePath>();
		var zones = new List<NodePath>();
		var spots = new List<NodePath>();
		var cams = new List<NodePath>();
		CollectRecursive(this, spawns, zones, spots, cams);
		SpawnPaths = spawns.ToArray();
		ZonePaths = zones.ToArray();
		BombSpotPaths = spots.ToArray();
		PreviewCamPaths = cams.ToArray();
		NotifyPropertyListChanged();
		GD.Print(
			$"[Level] Baked: {spawns.Count} spawn, {zones.Count} zone, {spots.Count} spot, {cams.Count} cam path(s)"
		);
	}

	private void CollectRecursive(
		Node node,
		List<NodePath> spawns,
		List<NodePath> zones,
		List<NodePath> spots,
		List<NodePath> cams
	)
	{
		foreach (var child in node.GetChildren())
		{
			if (child is BombSpot)
				spots.Add(GetPathTo(child));
			else if (child is Spawn)
				spawns.Add(GetPathTo(child));
			else if (child is Zone)
				zones.Add(GetPathTo(child));
			else if (child is Camera3D)
				cams.Add(GetPathTo(child));
			CollectRecursive(child, spawns, zones, spots, cams);
		}
	}
}
