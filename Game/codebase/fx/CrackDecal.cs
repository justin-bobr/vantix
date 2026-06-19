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
using System.Threading.Tasks;
using Godot;

namespace Vantix.Fx;

/// <summary>A Decal that bakes one procedural concrete crack to place by hand. A directed jagged fracture (plus a few
/// thinner offshoots) whose width and depth vary along its length via noise — wider here, hairline there, deep in
/// places, shallow in others — with irregular spall chunks chipped along the edges. Outputs albedo+alpha and a groove
/// normal for real GBuffer depth. Randomize for a new shape, Regenerate to re-bake the current Seed.</summary>
[Tool]
[GlobalClass]
public partial class CrackDecal : Decal
{
	[Export] public int Seed { get; set; } = 1;
	[Export(PropertyHint.Range, "128,1024,64")] public int TextureSize { get; set; } = 512;
	[Export] public Color CrackColor { get; set; } = new Color(0.06f, 0.05f, 0.045f);
	[Export] public Color SpallColor { get; set; } = new Color(0.52f, 0.47f, 0.4f);
	[Export(PropertyHint.Range, "0.003,0.04,0.001")] public float CrackWidth { get; set; } = 0.008f;
	[Export(PropertyHint.Range, "0.0,0.02,0.001")] public float EdgeRoughness { get; set; } = 0.004f;
	[Export(PropertyHint.Range, "8.0,60.0,1.0")] public float EdgeFrequency { get; set; } = 30.0f;
	[Export(PropertyHint.Range, "3,24,1")] public int Segments { get; set; } = 14;
	[Export(PropertyHint.Range, "0.0,1.4,0.05")] public float Jaggedness { get; set; } = 0.8f;
	[Export(PropertyHint.Range, "0.0,1.0,0.05")] public float BranchAmount { get; set; } = 0.3f;
	[Export(PropertyHint.Range, "0,3,1")] public int BranchLevels { get; set; } = 1;
	[Export(PropertyHint.Range, "0.0,1.0,0.05")] public float WidthVariation { get; set; } = 0.6f;
	[Export(PropertyHint.Range, "0.0,1.0,0.05")] public float DepthVariation { get; set; } = 0.75f;
	[Export(PropertyHint.Range, "1.0,8.0,0.5")] public float VariationScale { get; set; } = 3.0f;
	[Export(PropertyHint.Range, "0.0,0.2,0.005")] public float SpallWidth { get; set; } = 0.025f;
	[Export(PropertyHint.Range, "0.0,0.15,0.005")] public float SpallEdgeNoise { get; set; } = 0.02f;
	[Export(PropertyHint.Range, "2.0,24.0,0.5")] public float SpallFrequency { get; set; } = 9.0f;
	[Export(PropertyHint.Range, "0.0,1.0,0.05")] public float SpallStrength { get; set; } = 0.5f;
	[Export(PropertyHint.Range, "0.0,1.0,0.05")] public float Erosion { get; set; } = 0.5f;
	[Export(PropertyHint.Range, "4.0,40.0,1.0")] public float GritFrequency { get; set; } = 18.0f;
	[Export(PropertyHint.Range, "0.0,8.0,0.1")] public float NormalStrength { get; set; } = 2.0f;
	[Export] public bool InvertNormal { get; set; } = true;
	[Export] public bool Randomize { get => false; set { if (value) { Seed = (int)(GD.Randi() % 1000000u) + 1; Generate(); } } }
	[Export] public bool Regenerate { get => false; set { if (value) Generate(); } }

	private struct Seg
	{
		public Vector2 A;
		public Vector2 B;
		public float WA;
		public float WB;
		public float DA;
		public float DB;
	}

	private readonly List<Seg> _segs = [];
	private Vector2 _seedVec;

	public override void _Ready()
	{
		if (TextureAlbedo == null)
			Generate();
	}

	private float WidthAt(float s, float off)
	{
		var nz = ProcNoise.Fbm(new Vector2(s * VariationScale + off, _seedVec.X));
		var tip = Mathf.SmoothStep(0.0f, 0.12f, s) * (1.0f - Mathf.SmoothStep(0.85f, 1.0f, s));
		return CrackWidth * Mathf.Lerp(1.0f, 0.3f + 3.0f * nz, WidthVariation) * tip;
	}

	private float DepthAt(float s, float off)
	{
		var nz = ProcNoise.Fbm(new Vector2(s * VariationScale + off, _seedVec.Y + 5.0f));
		return Mathf.Clamp(Mathf.Lerp(1.0f, 0.2f + 1.1f * nz, DepthVariation), 0.0f, 1.0f);
	}

	private void BuildBranch(RandomNumberGenerator rng, Vector2 start, float angle, int segs, float stepLen, float jitter, float width, int level, float off)
	{
		var cur = start;
		var ang = angle;
		for (int i = 0; i < segs; i++)
		{
			ang += rng.RandfRange(-jitter, jitter) * 0.35f;
			if (rng.Randf() < 0.3f)
				ang += rng.RandfRange(-jitter, jitter);
			var nxt = cur + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * stepLen;
			var s0 = (float)i / segs;
			var s1 = (float)(i + 1) / segs;
			var scale = width / CrackWidth;
			_segs.Add(new Seg
			{
				A = cur,
				B = nxt,
				WA = WidthAt(s0, off) * scale,
				WB = WidthAt(s1, off) * scale,
				DA = DepthAt(s0, off),
				DB = DepthAt(s1, off),
			});
			if (level > 0 && i > 0 && i < segs - 1 && rng.Randf() < BranchAmount)
			{
				var ba = ang + rng.RandfRange(0.5f, 1.2f) * (rng.Randf() > 0.5f ? 1.0f : -1.0f);
				BuildBranch(rng, nxt, ba, Mathf.Max(2, segs / 2), stepLen * 0.8f, jitter * 1.2f, width * 0.55f, level - 1, off + 13.3f);
			}
			cur = nxt;
		}
	}

	private float CrackDist(Vector2 p, out float width, out float depth)
	{
		var best = 1e9f;
		width = CrackWidth;
		depth = 1.0f;
		foreach (var s in _segs)
		{
			var ab = s.B - s.A;
			var t = Mathf.Clamp((p - s.A).Dot(ab) / Mathf.Max(ab.LengthSquared(), 1e-6f), 0.0f, 1.0f);
			var dist = p.DistanceTo(s.A + ab * t);
			if (dist < best)
			{
				best = dist;
				width = Mathf.Lerp(s.WA, s.WB, t);
				depth = Mathf.Lerp(s.DA, s.DB, t);
			}
		}
		return best;
	}

	private float Coverage(Vector2 uv, out float alpha, out float dark)
	{
		var d = CrackDist(uv, out var w, out var depth);
		w = Mathf.Max(w, 0.0015f);
		d += (ProcNoise.VNoise(uv * EdgeFrequency + _seedVec + new Vector2(3.0f, 8.0f)) - 0.5f) * EdgeRoughness;
		var chunk = ProcNoise.VNoise(uv * (SpallFrequency * 0.5f) + _seedVec + new Vector2(19.0f, 7.0f));
		var fray = (ProcNoise.VNoise(uv * SpallFrequency + _seedVec) - 0.5f) * SpallEdgeNoise;
		var open = 1.0f - Mathf.SmoothStep(w * 0.5f, w, d);
		var lipW = SpallWidth * (0.5f + chunk);
		var lip = (1.0f - Mathf.SmoothStep(w, w + lipW, d - fray)) * (1.0f - open) * SpallStrength;
		var scour = 1.0f - Erosion * ProcNoise.VNoise(uv * GritFrequency + _seedVec + new Vector2(31.0f, 13.0f));
		var str = depth * scour;
		dark = open * str;
		alpha = Mathf.Max(open * Mathf.Lerp(0.45f, 1.0f, depth), lip);
		return open * str + lip * 0.25f;
	}

	private void Generate()
	{
		var n = TextureSize;
		var rng = new RandomNumberGenerator { Seed = (ulong)Seed };
		_seedVec = new Vector2(rng.RandfRange(0.0f, 50.0f), rng.RandfRange(0.0f, 50.0f));
		_segs.Clear();
		var baseAngle = rng.RandfRange(0.0f, Mathf.Tau);
		var stepLen = 0.8f / Segments;
		var mid = new Vector2(0.5f, 0.5f);
		var dir = new Vector2(Mathf.Cos(baseAngle), Mathf.Sin(baseAngle));
		BuildBranch(rng, mid - dir * stepLen * Segments * 0.5f, baseAngle, Segments, stepLen, Jaggedness, CrackWidth, BranchLevels, 0.0f);

		var alb = new byte[n * n * 4];
		var nrm = new byte[n * n * 3];
		var hmap = new float[n * n];
		var ns = InvertNormal ? -NormalStrength : NormalStrength;

		Parallel.For(0, n, y =>
		{
			for (int x = 0; x < n; x++)
			{
				var uv = new Vector2((float)x / n, (float)y / n);
				var h = Coverage(uv, out var alpha, out var dark);
				var col = SpallColor.Lerp(CrackColor, dark);
				var p = y * n + x;
				var ai = p * 4;
				alb[ai] = (byte)(col.R * 255.0f);
				alb[ai + 1] = (byte)(col.G * 255.0f);
				alb[ai + 2] = (byte)(col.B * 255.0f);
				alb[ai + 3] = (byte)(Mathf.Clamp(alpha, 0.0f, 1.0f) * 255.0f);
				hmap[p] = h;
			}
		});

		Parallel.For(0, n, y =>
		{
			for (int x = 0; x < n; x++)
			{
				var p = y * n + x;
				var h = hmap[p];
				var hx = hmap[y * n + Mathf.Min(x + 1, n - 1)];
				var hy = hmap[Mathf.Min(y + 1, n - 1) * n + x];
				var nv = new Vector3((h - hx) * ns, (h - hy) * ns, 1.0f).Normalized();
				var ni = p * 3;
				nrm[ni] = (byte)((nv.X * 0.5f + 0.5f) * 255.0f);
				nrm[ni + 1] = (byte)((nv.Y * 0.5f + 0.5f) * 255.0f);
				nrm[ni + 2] = (byte)((nv.Z * 0.5f + 0.5f) * 255.0f);
			}
		});

		TextureAlbedo = ImageTexture.CreateFromImage(Image.CreateFromData(n, n, false, Image.Format.Rgba8, alb));
		TextureNormal = ImageTexture.CreateFromImage(Image.CreateFromData(n, n, false, Image.Format.Rgb8, nrm));
		TextureOrm = null;
	}
}
