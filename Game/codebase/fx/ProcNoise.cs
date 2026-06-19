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

/// <summary>Shared CPU value-noise + fBm used by the procedural decal generators (CrackDecal, PuddleDecal) so they
/// bake matching, deterministic patterns without duplicating the noise.</summary>
internal static class ProcNoise
{
	public static float Frac(float x) => x - Mathf.Floor(x);

	public static float Hash(Vector2 p) =>
		Frac(Mathf.Sin(p.Dot(new Vector2(127.1f, 311.7f))) * 43758.5453f);

	public static float VNoise(Vector2 p)
	{
		Vector2 i = p.Floor();
		Vector2 f = p - i;
		f = f * f * (new Vector2(3.0f, 3.0f) - 2.0f * f);
		float a = Hash(i);
		float b = Hash(i + new Vector2(1.0f, 0.0f));
		float c = Hash(i + new Vector2(0.0f, 1.0f));
		float d = Hash(i + new Vector2(1.0f, 1.0f));
		return Mathf.Lerp(Mathf.Lerp(a, b, f.X), Mathf.Lerp(c, d, f.X), f.Y);
	}

	public static float Fbm(Vector2 p)
	{
		float s = 0.0f;
		float a = 0.5f;
		for (int i = 0; i < 4; i++)
		{
			s += a * VNoise(p);
			p *= 2.0f;
			a *= 0.5f;
		}
		return s;
	}
}
