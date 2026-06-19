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

/// <summary>
/// Deterministic smoke grenade (GrenadeTrajectory); spawns a SmokeVoxelField on impact.
/// Owner runs physics and broadcasts ProjectileState/Despawn; puppet lerps from snapshots and deploys on Despawn.
/// </summary>
public partial class SmokeGrenade : Node3D
{
	private Vector3 _vel;
	private Rid _ownerExclude;
	private float _flyTimer;
	private float _restTimer;
	private PhysicsRayQueryParameters3D _query;

	private MeshInstance3D _body;
	private bool _deployed;
	private SmokeVoxelField _field;

	public byte OwnerNetId;
	public uint ProjectileId;
	public bool IsPuppet;

	private const int StateBroadcastEveryNthTick = 4;
	private int _stateBroadcastCounter;

	private Vector3 _puppetTargetPos;
	private Vector3 _puppetTargetVel;
	private const float PuppetLerpRate = 16f;

	/// <summary>Creates a grenade. Replication optional via ownerNetId/projectileId; puppet mode follows owner ProjectileState.</summary>
	public static SmokeGrenade Spawn(Node parent, Vector3 origin, Vector3 velocity, Rid ownerExclude,
		byte ownerNetId = 0, uint projectileId = 0, bool isPuppet = false)
	{
		var g = new SmokeGrenade
		{
			_vel = velocity,
			_ownerExclude = ownerExclude,
			OwnerNetId = ownerNetId,
			ProjectileId = projectileId,
			IsPuppet = isPuppet,
			_puppetTargetPos = origin,
			_puppetTargetVel = velocity,
		};
		parent.AddChild(g);
		g.GlobalPosition = origin;
		return g;
	}

	public override void _Ready()
	{
		_query = new PhysicsRayQueryParameters3D
		{
			CollisionMask = GrenadeTrajectory.CollisionMask,
			Exclude = new Godot.Collections.Array<Rid> { _ownerExclude },
		};

		const float canHeight = 0.22f;
		_body = new MeshInstance3D
		{
			Mesh = new CylinderMesh { Height = canHeight, TopRadius = 0.055f, BottomRadius = 0.06f },
			Position = new Vector3(0f, canHeight * 0.5f - GrenadeTrajectory.Radius, 0f),
		};
		_body.MaterialOverride = new StandardMaterial3D
		{
			AlbedoColor = new Color(0.16f, 0.20f, 0.14f),
			Metallic = 0.3f,
			Roughness = 0.55f,
		};
		AddChild(_body);

		if (OwnerNetId != 0)
		{
			var client = NetMain.Instance?.Client;
			if (IsPuppet) client?.RegisterPuppetProjectile(OwnerNetId, ProjectileId, this);
			else client?.RegisterOwnedProjectile(ProjectileId, this);
		}
	}

	public override void _PhysicsProcess(double delta)
	{
		using var _prof = MiniProfiler.SampleClient("SmokeGrenade._PhysicsProcess");
		if (_deployed)
		{
			if (!IsInstanceValid(_field)) QueueFree();
			return;
		}

		if (IsPuppet)
		{
			float distSq = (_puppetTargetPos - GlobalPosition).LengthSquared();
			if (distSq > 4f)
				GlobalPosition = _puppetTargetPos;
			else
			{
				float t = 1f - Mathf.Exp(-PuppetLerpRate * (float)delta);
				GlobalPosition = GlobalPosition.Lerp(_puppetTargetPos, t);
			}
			_vel = _puppetTargetVel;
			return;
		}

		StepProjectile();

		if (OwnerNetId != 0)
		{
			_stateBroadcastCounter++;
			if (_stateBroadcastCounter >= StateBroadcastEveryNthTick)
			{
				_stateBroadcastCounter = 0;
				NetMain.Instance?.Client?.SendProjectileState(ProjectileId, GlobalPosition, _vel);
			}
		}
	}

	private void StepProjectile()
	{
		_flyTimer += GrenadeTrajectory.FixedDt;

		Vector3 pos = GlobalPosition, vel = _vel;
		bool grounded = GrenadeTrajectory.Advance(GetWorld3D().DirectSpaceState, _query, ref pos, ref vel);
		GlobalPosition = pos;
		_vel = vel;

		if (_vel.Length() < GrenadeTrajectory.RestSpeed && grounded) _restTimer += GrenadeTrajectory.FixedDt;
		else _restTimer = 0f;

		if (_restTimer >= GrenadeTrajectory.RestDuration || _flyTimer >= GrenadeTrajectory.MaxFlyTime)
			Deploy();
	}

	/// <summary>NetClient hook for incoming ProjectileState — sets the puppet lerp target.</summary>
	public void ApplyRemoteState(Vector3 pos, Vector3 vel)
	{
		if (!IsPuppet) return;
		_puppetTargetPos = pos;
		_puppetTargetVel = vel;
	}

	/// <summary>NetClient hook for incoming ProjectileDespawn — snaps the puppet to finalPos and spawns the smoke. No-op if already deployed.</summary>
	public void ApplyRemoteDespawn(Vector3 finalPos)
	{
		if (_deployed) return;
		GlobalPosition = finalPos;
		Deploy();
	}

	private void Deploy()
	{
		Dbg.Print($"[grenade] deployed @ ({GlobalPosition.X:F1},{GlobalPosition.Y:F1},{GlobalPosition.Z:F1}) | fly={_flyTimer:F2}s puppet={IsPuppet}");
		_field = SmokeVoxelField.Spawn(GetParent(), GlobalPosition);
		_deployed = true;

		if (OwnerNetId != 0)
		{
			var client = NetMain.Instance?.Client;
			if (!IsPuppet)
			{
				client?.SendProjectileDespawn(ProjectileId, GlobalPosition);
				client?.UnregisterOwnedProjectile(ProjectileId);
			}
			else
			{
				client?.UnregisterPuppetProjectile(OwnerNetId, ProjectileId);
			}
		}
	}
}
