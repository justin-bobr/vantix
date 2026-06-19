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

namespace Vantix.Character;

/// <summary>Per-tick movement input (wishdir, view angles, buttons) for the deterministic movement step.</summary>
public struct MovementInput
{
	/// <summary>Sequence number used by replay and reconciliation.</summary>
	public uint TickIndex;
	public float Dt;

	/// <summary>Local-space input vector (X = strafe right positive, Z = back positive).</summary>
	public Vector3 WishDir;

	/// <summary>Body yaw in radians.</summary>
	public float ViewYaw;

	/// <summary>Head pitch in radians, used for the aim direction.</summary>
	public float ViewPitch;
	public bool SprintHeld;
	public bool ShiftHeld;
	public bool CrouchHeld;

	/// <summary>Press-edge of the crouch key — used to initiate slides.</summary>
	public bool CrouchPressed;

	/// <summary>Right-mouse hold. Blocks sprint and enables the ADS blend.</summary>
	public bool AdsHeld;

	/// <summary>Hold to dampen ADS sway for a few seconds, then a shaky recover phase.</summary>
	public bool BreathHoldHeld;

	/// <summary>Press-edge of the jump key (not held), so bunny-hopping is impossible.</summary>
	public bool JumpPressed;

	/// <summary>Currently selected weapon — required for ADS speed multiplier and ADS blend time.</summary>
	public WeaponStats Weapon;

	/// <summary>Used for gravity and regular jumps. Server-derived physics truth.</summary>
	public bool OnFloor;

	/// <summary>Used for wall jumps. Server-derived physics truth.</summary>
	public bool TouchingWall;

	/// <summary>World-space wall normal. Server-derived physics truth.</summary>
	public Vector3 WallNormal;

	/// <summary>Ceiling too low to stand from a crouch — keeps the player crouched on release. Server-derived
	/// physics truth (upward capsule sweep).</summary>
	public bool HeadroomBlocked;

	/// <summary>Subtick events ordered by TFraction ascending; null/empty for the legacy single-segment path.</summary>
	public SubtickEvent[] Events;

	/// <summary>Held-input bitmask at t=0, for the subtick path's first segment. Ignored on the legacy path.</summary>
	public InputBits InitialBits;

	/// <summary>View yaw at the start of the tick. Ignored on the legacy path.</summary>
	public float InitialViewYaw;

	/// <summary>View pitch at the start of the tick. Ignored on the legacy path.</summary>
	public float InitialViewPitch;

	/// <summary>Body basis derived from ViewYaw, used to transform WishDir into world space.</summary>
	public readonly Basis BodyBasis => Basis.FromEuler(new Vector3(0f, ViewYaw, 0f));

	/// <summary>Unit forward from ViewYaw/ViewPitch, used by server hitscan.</summary>
	public readonly Vector3 AimDirection
	{
		get
		{
			float cp = Mathf.Cos(ViewPitch);
			return new Vector3(-Mathf.Sin(ViewYaw) * cp, Mathf.Sin(ViewPitch), -Mathf.Cos(ViewYaw) * cp);
		}
	}
}
