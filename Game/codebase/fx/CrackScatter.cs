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

namespace Vantix.Fx;

/// <summary>Scatters many small procedural CrackDecals over a floor area. Baking is spread across frames (a few per
/// frame) so the editor stays responsive and reports progress instead of freezing. Deterministic from ScatterSeed.
/// Scatter re-rolls the seed; Rebuild keeps it. Place over the floor, set AreaSize to cover it.</summary>
[Tool]
[GlobalClass]
public partial class CrackScatter : Node3D
{
	[Export(PropertyHint.Range, "1,300,1")] public int Count { get; set; } = 40;
	[Export] public Vector2 AreaSize { get; set; } = new Vector2(60.0f, 60.0f);
	[Export(PropertyHint.Range, "0.3,8.0,0.1")] public float SizeMin { get; set; } = 1.0f;
	[Export(PropertyHint.Range, "0.3,8.0,0.1")] public float SizeMax { get; set; } = 3.0f;
	[Export(PropertyHint.Range, "0.2,4.0,0.1")] public float ProjectDepth { get; set; } = 1.0f;
	[Export(PropertyHint.Range, "64,512,64")] public int DecalTextureSize { get; set; } = 128;
	[Export] public float YOffset { get; set; } = 0.5f;
	[Export(PropertyHint.Range, "1,16,1")] public int BakesPerFrame { get; set; } = 2;
	[Export] public int ScatterSeed { get; set; } = 1;
	[Export] public string Status { get; private set; } = "idle";
	[Export] public bool Scatter { get => false; set { if (value) { ScatterSeed = (int)(GD.Randi() % 1000000u) + 1; Begin(); } } }
	[Export] public bool Rebuild { get => false; set { if (value) Begin(); } }

	private RandomNumberGenerator _rng;
	private int _pending;
	private int _done;

	public override void _Ready()
	{
		SetProcess(false);
		if (GetChildCount() == 0)
			Begin();
	}

	public override void _Process(double delta)
	{
		if (_pending <= 0)
		{
			SetProcess(false);
			return;
		}
		int batch = Mathf.Min(BakesPerFrame, _pending);
		for (int b = 0; b < batch; b++)
		{
			SpawnOne();
			_done++;
			_pending--;
		}
		Status = _pending > 0 ? $"baking {_done}/{Count}" : $"done ({Count})";
		if (_pending <= 0)
		{
			SetProcess(false);
			GD.Print($"[CrackScatter] {Status}");
		}
	}

	private void Clear()
	{
		foreach (Node child in GetChildren())
		{
			RemoveChild(child);
			child.Free();
		}
	}

	private void Begin()
	{
		Clear();
		_rng = new RandomNumberGenerator { Seed = (ulong)ScatterSeed };
		_done = 0;
		_pending = Count;
		Status = $"baking 0/{Count}";
		SetProcess(true);
	}

	private void SpawnOne()
	{
		float hx = AreaSize.X * 0.5f;
		float hz = AreaSize.Y * 0.5f;
		var crack = new CrackDecal
		{
			Name = $"Crack{_done}",
			TextureSize = DecalTextureSize,
			Seed = _rng.RandiRange(1, 1000000),
			Position = new Vector3(_rng.RandfRange(-hx, hx), YOffset, _rng.RandfRange(-hz, hz)),
			RotationDegrees = new Vector3(0.0f, _rng.RandfRange(0.0f, 360.0f), 0.0f),
		};
		float s = _rng.RandfRange(SizeMin, SizeMax);
		crack.Size = new Vector3(s, ProjectDepth, s);
		AddChild(crack);
		if (Engine.IsEditorHint())
		{
			Node root = GetTree()?.EditedSceneRoot;
			if (root != null)
				crack.Owner = root;
		}
	}
}
