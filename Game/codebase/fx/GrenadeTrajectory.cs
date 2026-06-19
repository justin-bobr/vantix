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

/// <summary>
/// Deterministic projectile sim for thrown grenades, shared by SmokeGrenade and the aim guide so the
/// preview matches the real throw. Fixed-step, raycast-only, no RigidBody/randomness: same inputs land
/// identically on every client.
/// </summary>
public static class GrenadeTrajectory
{
	public const float BaseGravity = 17.5f;
	public static float Gravity = BaseGravity;
	public const float Radius = 0.07f;
	public const float Restitution = 0.35f;
	public const float BounceFriction = 0.65f;
	public const float GroundDrag = 7f;
	public const float RestSpeed = 1.0f;
	public const float RestDuration = 0.18f;
	public const float MaxFlyTime = 5f;
	public const uint CollisionMask = 1;
	public const float FixedDt = 1f / 128f;

	/// <summary>One step: gravity, raycast move with bounce, ground drag. Returns grounded state after the step.</summary>
	public static bool Advance(PhysicsDirectSpaceState3D space, PhysicsRayQueryParameters3D query,
		ref Vector3 pos, ref Vector3 vel)
	{
		vel.Y -= Gravity * FixedDt;

		Vector3 move = vel * FixedDt;
		float moveLen = move.Length();
		if (moveLen > 1e-6f)
		{
			query.From = pos;
			query.To = pos + move / moveLen * (moveLen + Radius);
			if (space.IntersectRayInto(query, _sharedResult))
			{
				Vector3 n = _sharedResult.GetNormal();
				pos = _sharedResult.GetPosition() + n * Radius;
				Vector3 vn = n * vel.Dot(n);
				Vector3 vt = vel - vn;
				vel = vt * BounceFriction - vn * Restitution;
			}
			else
			{
				pos += move;
			}
		}

		bool grounded = IsGrounded(space, query, pos, out Vector3 groundNormal);
		if (grounded)
		{
			Vector3 vn = groundNormal * vel.Dot(groundNormal);
			Vector3 vt = (vel - vn) * Mathf.Max(0f, 1f - GroundDrag * FixedDt);
			vel = vt + vn;
		}
		return grounded;
	}

	/// <summary>Down-raycast: true if the grenade is resting on a surface.</summary>
	public static bool IsGrounded(PhysicsDirectSpaceState3D space, PhysicsRayQueryParameters3D query,
		Vector3 pos, out Vector3 normal)
	{
		normal = Vector3.Up;
		query.From = pos;
		query.To = pos + Vector3.Down * (Radius + 0.08f);
		if (!space.IntersectRayInto(query, _sharedResult)) return false;
		normal = _sharedResult.GetNormal();
		return true;
	}

	private static PhysicsRayQueryParameters3D _predictQuery;
	private static readonly Godot.Collections.Array<Rid> _predictExclude = new();
	private static readonly PhysicsRayQueryResult3D _sharedResult = new();

	/// <summary>Simulates origin/vel until rest, filling pathOut and the landing point/normal.</summary>
	public static void Predict(PhysicsDirectSpaceState3D space, Vector3 origin, Vector3 vel,
		Rid ownerExclude, List<Vector3> pathOut, out Vector3 landing, out Vector3 landingNormal)
	{
		pathOut.Clear();
		landingNormal = Vector3.Up;
		if (_predictQuery == null)
		{
			_predictQuery = PhysicsRayQueryParameters3D.Create(Vector3.Zero, Vector3.Right, CollisionMask);
			_predictQuery.Exclude = _predictExclude;
		}
		_predictExclude.Clear();
		_predictExclude.Add(ownerExclude);
		_predictQuery.CollisionMask = CollisionMask;
		var query = _predictQuery;

		Vector3 pos = origin;
		pathOut.Add(pos);
		float rest = 0f;
		int maxSteps = Mathf.CeilToInt(MaxFlyTime / FixedDt);

		for (int step = 0; step < maxSteps; step++)
		{
			bool grounded = Advance(space, query, ref pos, ref vel);
			pathOut.Add(pos);

			if (vel.Length() < RestSpeed && grounded) rest += FixedDt;
			else rest = 0f;
			if (rest >= RestDuration) break;
		}

		landing = pos;
		IsGrounded(space, query, pos, out landingNormal);
	}
}
