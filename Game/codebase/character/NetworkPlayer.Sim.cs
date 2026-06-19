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

/// <summary>Shared player sim: movement, hitscan, mantle, crouch, footsteps, grenades, plus puppet/server
/// visual hooks. LocalPlayer derives from this and adds the local-only camera, mouse-look, aim guide.</summary>
public partial class NetworkPlayer : CharacterBody3D
{
	private bool _isFrozen;
	private uint _savedCollisionLayer;
	private uint _savedCollisionMask;

	private bool _isDead;
	private uint _savedCollisionLayerDead;
	private uint _savedCollisionMaskDead;

	private const float MantleMinHeight = 1.0f;
	private const float MantleMaxHeight = 1.75f;
	private const float MantleReach = 0.7f;
	private const float MantleDuration = 0.35f;

	private readonly FootstepController _footstepLogic = new();
	private static readonly StringName _defaultMaterial = "default";

	private readonly GrenadeController _grenade = new();
	private static readonly float[] _mantleForwardOffsets = { 0.08f, 0.18f, 0.35f };

	private bool _wasOnFloor;
	private Vector3 _mantleStart;
	private Vector3 _mantleTarget;
	private float _mantleTimer;
	private ulong _lastStepupBlockedLogMs;
	private ulong _lastStepupSuccessLogMs;
	private Vector3 _stepupLastBlockedPos = new(float.MinValue, 0, 0);
	private uint _stepupLastBlockedTick;
	private const uint StepupBlockedCooldownTicks = 8;

	private double _reconcileWindowStartSec;

	private float _serverSmoothedBodyYaw;
	private bool _serverBodyYawInitialized;
	private const float ServerBodyYawMaxTwistRad = 1.5708f;
	private const float ServerBodyYawRateMoving = 12f;
	private const float ServerBodyYawRateStanding = 6f;

	private static readonly StringName _pTpsAimAdd = "parameters/AimAdd/add_amount";

	protected float _fixedDt;
	protected CapsuleShape3D _capsule;
	protected Vector3 _headBasePos;

	protected HitboxRig _hitboxRig;
	protected virtual bool NeedsHitboxRig => true;
	protected int _activeSlot;

	protected Vector3 _pendingThrowOrigin;
	protected Vector3 _pendingThrowVel;
	protected bool _pendingThrowValid;

	protected float _preMoveVelocityY;
	protected Vector3 _prevPhysicsPos;
	protected Vector3 _currentPhysicsPos;
	protected bool _isMantling;
	protected uint _mantleReconcileBlockUntilTick;
	protected MovementInput _lastMovementInput;

	protected Vector3 _correctionPending;
	protected Vector3 _visualErrorOffset;
	protected float _activeBleedRate = 6.5f;

	protected ulong _tickStartUsec;
	protected ulong _lastFirePressUsec;

	protected readonly System.Collections.Generic.List<BufferedSubtickEvent> _subtickBuffer = new(Packets.MaxSubtickEventsWire);
	protected InputBits _liveBits;
	protected InputBits _intervalStartBits;
	protected float _intervalStartViewYaw;
	protected float _intervalStartViewPitch;
	protected ulong _prevTickStartUsec;

	protected PhysicsRayQueryParameters3D _rayQuery;
	protected readonly PhysicsRayQueryResult3D _rayResult = new();
	protected Godot.Collections.Array<Rid> _selfExclude;
	protected bool _waitingForFadeOut;
	protected int _ticksSinceSpawn;
	protected const int SpawnSettleTicks = 30;

	protected bool _isReplaying;

	protected int _reconcileCountWindow;
	public PlayerAudio Audio { get; private set; }

	public const int TickRate = 128;
	public uint CurrentTick { get; protected set; }

	[Export(PropertyHint.Range, "0.5,2.5,0.05")]
	public float StandHeight = 1.8f;
	[Export(PropertyHint.Range, "0.5,2.0,0.05")]
	public float CrouchHeight = 1.2f;
	[Export(PropertyHint.Range, "0.1,1.0,0.01")]
	public float CapsuleRadius = 0.4f;
	[Export(PropertyHint.Range, "0.0,1.0,0.05")]
	public float FloorSnapDist = 0.6f;
	[Export(PropertyHint.Range, "0,90,1")]
	public float FloorMaxAngleDeg = 50f;
	[Export(PropertyHint.Range, "0.0,1.0,0.01")]
	public float StepMaxHeight = 0.45f;

	[Export(PropertyHint.Range, "1.0,2.5,0.05")]
	public float StandEyeHeight = 1.7f;
	[Export(PropertyHint.Range, "0.5,2.0,0.05")]
	public float CrouchEyeHeight = 1.1f;

	[Export]
	public CollisionShape3D BodyCollision;
	public byte NetId;

	[Export]
	public Skeleton3D TpsSkeleton;

	/// <summary>Bone-pose history for lag-comp. ServerAgent only.</summary>
	public BonePoseRewindBuffer BoneHistory;

	public MovementController Movement { get; } = new();

	/// <summary>When set, movement reads from this instead of the live input singleton.
	/// ServerPlayer fills it per tick; ServerBotPlayer sets it once at spawn.</summary>
	public InputPacket? NetInputSource;

	/// <summary>Frozen (reconnect pool): _PhysicsProcess early-returns and the pose stays.
	/// Collision is zeroed so live players don't get stuck on the ghost body.</summary>
	public bool IsFrozen
	{
		get => _isFrozen;
		set
		{
			if (_isFrozen == value) return;
			_isFrozen = value;
			if (value)
			{
				_savedCollisionLayer = CollisionLayer;
				_savedCollisionMask = CollisionMask;
				CollisionLayer = 0u;
				CollisionMask = 0u;
			}
			else
			{
				if (_savedCollisionLayer != 0u) CollisionLayer = _savedCollisionLayer;
				if (_savedCollisionMask != 0u) CollisionMask = _savedCollisionMask;
			}
		}
	}
	/// <summary>Death: no movement, collision, or shooting. Set by NetServer on HP=0, cleared on respawn.
	/// Same collision-zero logic as IsFrozen.</summary>
	public bool IsDead
	{
		get => _isDead;
		set
		{
			if (_isDead == value) return;
			_isDead = value;
			if (value)
			{
				if (_savedCollisionLayerDead == 0u && _savedCollisionMaskDead == 0u)
				{
					_savedCollisionLayerDead = CollisionLayer;
					_savedCollisionMaskDead = CollisionMask;
				}
				CollisionLayer = 0u;
				CollisionMask = 0u;
				Velocity = Vector3.Zero;
			}
			else
			{
				if (_savedCollisionLayerDead != 0u) CollisionLayer = _savedCollisionLayerDead;
				if (_savedCollisionMaskDead != 0u) CollisionMask = _savedCollisionMaskDead;
				_savedCollisionLayerDead = 0u;
				_savedCollisionMaskDead = 0u;
			}
		}
	}
	/// <summary>Tick of the last consumed input. Sent back as ackedTick for reconciliation.</summary>
	public uint LastAppliedInputTick;

	[ExportGroup("Firing")]
	[Export]
	public bool CanFire = true;

	/// <summary>True when the mag is empty and not mid-reload. Drives the bot's ReloadPressed input.</summary>
	public bool NeedsReload => Movement != null && Movement.CurrentMag == 0 && !Movement.IsReloading;
	[Export]
	public float HitscanRange = 200f;
	[Export]
	public uint HitscanMask = 1;

	/// <summary>True when this instance is a puppet visual. Set by the PuppetPlayer wrapper before AddChild
	/// (mutable because the wrapper owns the flag).</summary>
	public bool IsPuppet;
	/// <summary>True when this node broadcasts server-authoritative events (shot, footstep, jump, land, hit).</summary>
	public bool IsServerAuthority => IsServerAgent;

	public bool PuppetIsAirborne;
	public bool PuppetIsSprinting;
	public bool PuppetIsReloading;
	public bool PuppetIsInspecting;
	/// <summary>0 = weapon, 1 = grenade. Written by PuppetPlayer from Snapshot.ActiveSlot; without it the
	/// puppet's UpperBodyMix gate would stay at 0 (puppets don't tick to advance _activeSlot).</summary>
	public byte PuppetActiveSlot;
	/// <summary>Spine twist (view yaw minus body yaw, radians). Applied to the aim bone so the upper body
	/// follows the look direction; the body catches up past 90° delta.</summary>
	public float PuppetSpineTwist;

	/// <summary>TpsAimModifier under the skeleton; drives spine twist/pitch for the TPS aim pose (server +
	/// remote). Auto-created in _Ready when absent.</summary>
	public TpsAimModifier AimModifier { get; private set; }

	/// <summary>Netcode reads the footstep phase for the snapshot.</summary>
	public FootstepController FootstepLogic => _footstepLogic;
	/// <summary>Vertical velocity before MoveAndSlide. Used for land-impact scaling.</summary>
	public float PreMoveVelocityY => _preMoveVelocityY;
	/// <summary>Active slot (0 = weapon, 1 = grenade), for the HUD.</summary>
	public int ActiveSlot => _activeSlot;
	/// <summary>Grenade charge 0..1 while throw is held, for the HUD.</summary>
	public float GrenadeCharge => _grenade.Charge;

	/// <summary>Prediction buffer for reconciliation. Filled only for IsLocalPlayer.</summary>
	public readonly PredictionBuffer Prediction = new();
	/// <summary>Authority position for snapshots/reconciliation — real physics state, never the lerped
	/// _Process value (which drifts during the inter-tick window).</summary>
	public Vector3 AuthorityPosition { get => _currentPhysicsPos; set { } }

	private void SetupSim()
	{
		Engine.PhysicsTicksPerSecond = TickRate;
		_fixedDt = 1f / TickRate;

		_selfExclude = new Godot.Collections.Array<Rid> { GetRid() };
		_rayQuery = new PhysicsRayQueryParameters3D { Exclude = _selfExclude };

		SetupCapsule();
		SetupHeadPitch();

		FloorMaxAngle = Mathf.DegToRad(FloorMaxAngleDeg);
		FloorSnapLength = FloorSnapDist;
		FloorBlockOnWall = true;
		FloorStopOnSlope = false;
		Movement.Stamina = ConVars.Sv.MaxStamina;
		Movement.ResetSpawnConsumables();
		WeaponStats spawnWeapon = ConVars.Weapons.AR15;
		Movement.InitializeAmmo(spawnWeapon);
		if (spawnWeapon != null) Movement.FireMode = spawnWeapon.FireMode;
		GrenadeTrajectory.Gravity = GrenadeTrajectory.BaseGravity / Mathf.Max(0.1f, ConVars.Sv.GrenadeRangeScale);
		Audio = new PlayerAudio(
			GetNodeOrNull<FootstepAudio>("FootstepAudio"),
			GetNodeOrNull<WeaponAudio>("WeaponAudio"));
		Audio.Configure(IsLocalPlayer, ConVars.Weapons.AR15);
		WarmUpAudio();
		_wasOnFloor = IsOnFloor();

		HitscanMask = 1u | HitboxRig.Layer;

		if (TpsSkeleton != null && NeedsHitboxRig)
		{
			bool baked = false;
			foreach (Node c in GetChildren())
				if (c is HitboxBaker baker) { baked = baker.Baked; break; }
			_hitboxRig = new HitboxRig { Skeleton = TpsSkeleton, Name = "HitboxRig" };
			AddChild(_hitboxRig);
			_hitboxRig.Build(skipAutoOrient: baked);
		}

		if (TpsSkeleton != null)
		{
			foreach (var child in TpsSkeleton.GetChildren())
			{
				if (child is TpsAimModifier mod)
				{
					AimModifier = mod;
					AimModifier.HeadPitch = HeadPitch;
					AimModifier.AimBoneName = TpsAimBoneName;
					AimModifier.PitchScale = TpsAimPitchScale;
					break;
				}
			}
			if (AimModifier == null && !string.IsNullOrEmpty(TpsAimBoneName))
			{
				int tpsAimBoneIdx = TpsSkeleton.FindBone(TpsAimBoneName);
				if (tpsAimBoneIdx < 0)
					GD.PushWarning($"[NetworkPlayer] TpsAimBoneName='{TpsAimBoneName}' not in skeleton — pitch/twist disabled");
				else
				{
					AimModifier = new TpsAimModifier
					{
						Name = "aim_modifier_auto",
						HeadPitch = HeadPitch,
						AimBoneName = TpsAimBoneName,
						PitchScale = TpsAimPitchScale,
					};
					TpsSkeleton.AddChild(AimModifier);
					Dbg.Print($"[NetworkPlayer] Auto-Setup TpsAimModifier on {TpsAimBoneName} (rig-independent)");
				}
			}
		}

		if (TpsAnimTree != null) TpsAnimTree.Active = true;
		PreWarmAnimationOneShots(TpsAnimTree);
		ApplyViewMode();
		_prevPhysicsPos = GlobalPosition;
		_currentPhysicsPos = GlobalPosition;

		OnSimReady();
	}

	private void PreWarmAnimationOneShots(AnimationTree tree)
	{
		if (tree == null) return;
		var props = tree.GetPropertyList();
		int count = 0;
		foreach (Godot.Collections.Dictionary prop in props)
		{
			if (prop["name"].VariantType != Variant.Type.String && prop["name"].VariantType != Variant.Type.StringName)
				continue;
			string name = prop["name"].AsString();
			if (!name.StartsWith("parameters/") || !name.EndsWith("/request"))
				continue;
			tree.Set(name, (int)AnimationNodeOneShot.OneShotRequest.Fire);
			tree.Set(name, (int)AnimationNodeOneShot.OneShotRequest.Abort);
			count++;
		}
		if (count > 0)
			Dbg.Print($"[prewarm] pre-fired {count} one-shot(s) on '{tree.Name}' ({(IsPuppet ? "puppet" : IsServerAgent ? "server-agent" : "local")})");
	}

	private void FixedTick(float dt)
	{
		CurrentTick++;
		_ticksSinceSpawn++;

		if (IsDead)
		{
			Velocity = Vector3.Zero;
			return;
		}

		if (_isMantling)
		{
			StepMantle(dt);
			ApplyCrouchHeight();
			return;
		}

		MovementInput moveIn;
		using (MiniProfiler.SampleClient("NetworkPlayer.BuildMovementInput")) moveIn = BuildMovementInput(dt);
		_lastMovementInput = moveIn;
		Movement.Velocity = Velocity;
		using (MiniProfiler.SampleClient("NetworkPlayer.Movement.Step")) Movement.Step(moveIn);
		Velocity = Movement.Velocity;

		FireInput fireIn;
		using (MiniProfiler.SampleClient("NetworkPlayer.BuildFireInput")) fireIn = BuildFireInput(dt);
		Movement.FireStep(fireIn);
		if (Movement.DidFireThisFrame)
		{
			using (MiniProfiler.SampleClient("NetworkPlayer.HandleHitscan")) HandleHitscan();
			Dbg.Print($"[fire] tick={CurrentTick} shot #{Movement.ShotIndex} ({ConVars.Weapons.AR15?.Name}) | next in {Movement.FireCooldown * 1000f:0}ms");
		}

		using (MiniProfiler.SampleClient("NetworkPlayer.HandleGrenades")) HandleGrenades(dt);

		_preMoveVelocityY = Velocity.Y;
		Movement.PreMoveHorizSpeed = new Vector3(Velocity.X, 0f, Velocity.Z).Length();

		using (MiniProfiler.SampleClient("NetworkPlayer.TryStepUp")) TryStepUp(dt);
		using (MiniProfiler.SampleClient("NetworkPlayer.MoveAndSlide")) MoveAndSlide();
		using (MiniProfiler.SampleClient("NetworkPlayer.TryMantle")) TryMantle();

		ApplyCrouchHeight();
		using (MiniProfiler.SampleClient("NetworkPlayer.HandleLandingDetection")) HandleLandingDetection();
		HandleJumpAnimation();
		if (Movement.DidReloadThisFrame && !_isReplaying)
			OnDropMagEvent();
		using (MiniProfiler.SampleClient("NetworkPlayer.HandleFootsteps")) HandleFootsteps();
		using (MiniProfiler.SampleClient("NetworkPlayer.HandleWeaponAudio")) HandleWeaponAudio();

		OnTickApplied();

		if (_correctionPending.LengthSquared() > 0.0001f)
		{
			float step = Mathf.Min(1f, _fixedDt * 8f);
			Vector3 delta = _correctionPending * step;
			GlobalPosition += delta;
			_currentPhysicsPos += delta;
			_prevPhysicsPos += delta;
			_correctionPending -= delta;
		}

		if (_visualErrorOffset.LengthSquared() > 0.0001f)
		{
			float bleedStep = Mathf.Min(1f, _fixedDt * _activeBleedRate);
			_visualErrorOffset *= 1f - bleedStep;
			if (_visualErrorOffset.LengthSquared() < 0.0001f) _visualErrorOffset = Vector3.Zero;
		}
	}

	private void HandleFootsteps()
	{
		using (MiniProfiler.SampleClient("NetworkPlayer.HandleFootsteps.Cadence"))
		{
			_footstepLogic.Step(new FootstepInput
			{
				Dt = _fixedDt,
				HorizontalSpeed = Movement.HorizontalSpeed,
				OnFloor = IsOnFloor(),
				ShiftHeld = _lastMovementInput.ShiftHeld,
				CrouchHeld = Movement.CrouchBlend > 0.5f,
				IsSprinting = Movement.ActuallySprinting,
				IsSliding = Movement.IsSliding,
			});
		}

		if (!_footstepLogic.DidStepThisFrame) return;
		if (_isReplaying) return;

		HitInfo ground;
		using (MiniProfiler.SampleClient("NetworkPlayer.HandleFootsteps.CastGround"))
			ground = CastGround();
		StringName material = ground.Hit ? ground.Material : _defaultMaterial;

		OnFootstepEvent(ground, material);
	}

	private void HandleHitscan()
	{
		if (_isReplaying) return;
		ResolveShot(GetWorld3D().DirectSpaceState);
	}

	private FireInput BuildFireInput(float dt)
	{
		bool weaponSlot = _activeSlot == 0;
		WeaponButtons btn = SampleWeaponButtons();
		bool firePressed = weaponSlot && btn.Fire;
		bool reloadPressed = weaponSlot && btn.Reload;
		bool inspectPressed = weaponSlot && btn.Inspect;
		bool adsHeld = weaponSlot && btn.Ads;

		WeaponStats weapon = ConVars.Weapons.AR15;

		Vector3 shootOrigin = HeadPitch != null ? HeadPitch.GlobalPosition : GlobalPosition;

		uint fireTick = (IsServerAgent && NetInputSource.HasValue) ? NetInputSource.Value.TickIndex : CurrentTick;

		float fireYaw = Movement.SubtickFireViewValid ? Movement.SubtickFireViewYaw : Rotation.Y;
		float firePitch = Movement.SubtickFireViewValid
			? Movement.SubtickFireViewPitch
			: (HeadPitch != null ? HeadPitch.Rotation.X : 0f);

		return new FireInput
		{
			TickIndex = fireTick,
			FirePressed = firePressed,
			ReloadPressed = reloadPressed,
			InspectPressed = inspectPressed,
			AdsHeld = adsHeld,
			CanFire = CanFire && weapon != null,
			Weapon = weapon,
			Speed = Movement.HorizontalSpeed,
			ShooterPosition = shootOrigin,
			ViewYaw = fireYaw,
			ViewPitch = firePitch,
			Dt = dt,
		};
	}

	private void HandleGrenades(float dt)
	{
		ResolveActiveSlot();
		bool grenadeSlot = _activeSlot == 1;
		bool firePressed = SampleWeaponButtons().Fire;
		_grenade.Step(new GrenadeInput
		{
			SlotActive = grenadeSlot,
			ThrowHeld = grenadeSlot && firePressed,
			Dt = dt,
		});

		if (_grenade.DidThrowThisFrame)
		{
			ThrowGrenade();
		}
		else
		{
			_pendingThrowValid = grenadeSlot
				&& ComputeThrow(_grenade.Charge, out _pendingThrowOrigin, out _pendingThrowVel);
		}
	}

	private void ThrowGrenade()
	{
		if (!_pendingThrowValid) return;
		var (projectileId, ownerNetId) = RegisterGrenadeThrow(_pendingThrowOrigin, _pendingThrowVel);
		SmokeGrenade.Spawn(GetParent(), _pendingThrowOrigin, _pendingThrowVel, GetRid(),
			ownerNetId: ownerNetId, projectileId: projectileId, isPuppet: false);
		Dbg.Print($"[grenade] thrown tick={CurrentTick} vel={_pendingThrowVel:F1} pid={projectileId}");
	}

	private bool ComputeThrow(float charge, out Vector3 origin, out Vector3 vel)
	{
		origin = vel = Vector3.Zero;
		if (HeadPitch == null) return false;
		Vector3 fwd = -HeadPitch.GlobalTransform.Basis.Z;
		Vector3 dir = (fwd + Vector3.Up * ConVars.Sv.GrenadeThrowUpBias).Normalized();
		float speed = Mathf.Lerp(ConVars.Sv.GrenadeMinThrowSpeed, ConVars.Sv.GrenadeMaxThrowSpeed, charge);
		Vector3 inherit = new Vector3(Velocity.X, 0f, Velocity.Z) * ConVars.Sv.GrenadeInheritVelocity;
		vel = dir * speed + inherit;
		origin = HeadPitch.GlobalPosition + fwd * 0.4f;
		return true;
	}

	private void SetupHeadPitch()
	{
		if (HeadPitch != null)
		{
			HeadPitch.Position = new Vector3(HeadPitch.Position.X, StandEyeHeight, HeadPitch.Position.Z);
			_headBasePos = HeadPitch.Position;
			Dbg.Print($"[character] Eye height set to {StandEyeHeight:0.00}m");
		}
		else GD.PushWarning("[character] HeadPitch reference missing — camera will not drop on crouch");
	}

	private void TryMantle()
	{
		if (_isMantling) return;
		if (!Movement.IsAirborne) return;
		if (Velocity.Y < -0.5f) return;
		if (Movement.LastWishDir.LengthSquared() < 0.01f) return;
		if (!_lastMovementInput.CrouchHeld) return;

		Vector3 forward = -Transform.Basis.Z;

		Vector3 chest = GlobalPosition + new Vector3(0f, StandHeight * 0.5f, 0f);
		var space = GetWorld3D().DirectSpaceState;

		_rayQuery.CollisionMask = 1u;
		_rayQuery.From = chest;
		_rayQuery.To = chest + forward * MantleReach;
		if (!space.IntersectRayInto(_rayQuery, _rayResult)) return;

		Vector3 fwdNormal = _rayResult.GetNormal();
		if (Mathf.Abs(fwdNormal.Y) > 0.4f) return;
		Vector3 fwdHit = _rayResult.GetPosition();

		Vector3 topPos = default;
		float heightDiff = 0f;
		bool foundTop = false;
		float lastRejectedDiff = float.NaN;
		foreach (float fwdOff in _mantleForwardOffsets)
		{
			Vector3 above = fwdHit + forward * fwdOff + new Vector3(0f, MantleMaxHeight, 0f);
			_rayQuery.From = above;
			_rayQuery.To = above + new Vector3(0f, -MantleMaxHeight * 1.5f, 0f);
			if (!space.IntersectRayInto(_rayQuery, _rayResult)) continue;
			if (_rayResult.GetNormal().Y < 0.7f) continue;
			Vector3 p = _rayResult.GetPosition();
			float hd = p.Y - GlobalPosition.Y;
			if (hd < MantleMinHeight || hd > MantleMaxHeight)
			{
				lastRejectedDiff = hd;
				continue;
			}
			if (!foundTop || hd < heightDiff)
			{
				topPos = p; heightDiff = hd;
				foundTop = true;
			}
		}
		if (!foundTop)
		{
			if (Dbg.Enabled && !float.IsNaN(lastRejectedDiff))
			{
				string reason = lastRejectedDiff < MantleMinHeight
					? $"below mantle-min ({MantleMinHeight:F2}m) → crouch-jump physics suffices"
					: $"above mantle-max ({MantleMaxHeight:F2}m) → obstacle too tall to climb";
				Dbg.Print($"[mantle] REJECTED heightDiff={lastRejectedDiff:F2}m → {reason} | playerY={GlobalPosition.Y:F2}");
			}
			return;
		}

		Transform3D testTrans = new(GlobalTransform.Basis, topPos + new Vector3(0f, 0.05f, 0f));
		if (TestMove(testTrans, new Vector3(0f, StandHeight, 0f)))
		{
			Dbg.Print($"[mantle] REJECTED capsule blocked at top | heightDiff={heightDiff:F2}m topY={topPos.Y:F2}");
			return;
		}

		Vector3 landingPos = topPos;
		Vector3 inwardProbe = topPos + forward * (CapsuleRadius * 0.5f) + new Vector3(0f, 0.3f, 0f);
		_rayQuery.From = inwardProbe;
		_rayQuery.To = inwardProbe + new Vector3(0f, -0.6f, 0f);
		if (space.IntersectRayInto(_rayQuery, _rayResult)
			&& _rayResult.GetNormal().Y >= 0.7f
			&& Mathf.Abs(_rayResult.GetPosition().Y - topPos.Y) < 0.15f)
		{
			landingPos = _rayResult.GetPosition();
		}

		Dbg.Print($"[mantle] FIRED (climb) heightDiff={heightDiff:F2}m | topY={topPos.Y:F2} playerY={GlobalPosition.Y:F2} → target=({landingPos.X:F2},{landingPos.Y:F2},{landingPos.Z:F2})");

		_isMantling = true;
		_mantleStart = GlobalPosition;
		_mantleTarget = landingPos + new Vector3(0f, 0.05f, 0f);
		_mantleTimer = MantleDuration;
		Velocity = Vector3.Zero;
		_mantleReconcileBlockUntilTick = CurrentTick + (uint)Mathf.CeilToInt(MantleDuration * TickRate) + 30;
	}

	private void StepMantle(float dt)
	{
		_mantleTimer -= dt;
		if (_mantleTimer <= 0f)
		{
			GlobalPosition = _mantleTarget;
			Velocity = Vector3.Zero;
			_isMantling = false;
			if (IsLocalPlayer) Prediction.Clear();
			return;
		}
		float progress = 1f - (_mantleTimer / MantleDuration);
		float eased = progress * progress * (3f - 2f * progress);
		GlobalPosition = _mantleStart.Lerp(_mantleTarget, eased);
		Velocity = Vector3.Zero;
	}

	private void HandleLandingDetection()
	{
		bool onFloorNow = IsOnFloor();
		if (onFloorNow && !_wasOnFloor)
		{
			if (!_isReplaying)
				OnLandEvent(Mathf.Max(0f, -_preMoveVelocityY));
		}
		else if (!onFloorNow && _wasOnFloor)
		{
			Dbg.Print($"[floor] left — airborne | vY={Velocity.Y:F1}");
		}
		_wasOnFloor = onFloorNow;
	}

	private void HandleJumpAnimation()
	{
		if (Movement.DidJumpThisFrame && !_isReplaying)
			OnJumpEvent();
		if (Movement.DidWallJumpThisFrame && Dbg.Enabled)
			Dbg.Print($"[walljump] vY={Velocity.Y:F2} | horizSpeed={Movement.HorizontalSpeed:F1}");
	}

	protected void SetupCapsule() =>
		_capsule = CharacterSetup.SetupCapsule(this, BodyCollision, StandHeight, CapsuleRadius, FloorMaxAngleDeg, FloorSnapDist);

	protected virtual void OnTickApplied() { }

	protected virtual void OnSimReady()
	{
		CollisionLayer = 1u << 1;
		CollisionMask = 1u | (1u << 1);
	}

	protected virtual void DisableExpensiveSubtreeProcessing() { }

	protected virtual void WarmUpAudio() { }

	protected virtual void HandleWeaponAudio() { }

	protected HitInfo CastGround()
	{
		var space = GetWorld3D().DirectSpaceState;
		Vector3 from = GlobalPosition + Vector3.Up * 0.4f;
		return Hitscan.CastMulti(space, from, Vector3.Down, 1.0f, _selfExclude, mask: 1u);
	}

	protected static bool IsTunnelGround(HitInfo ground)
		=> ground.Hit && ground.Collider != null && ground.Collider.IsInGroup("tunnel");

	protected virtual void ResolveShot(PhysicsDirectSpaceState3D space)
	{
		HitInfo hit;
		using (MiniProfiler.SampleClient("NetworkPlayer.Hitscan.Cast")) hit = Hitscan.Cast(space, Movement.LastShotOrigin, Movement.LastShotDirection,
			HitscanRange, exclude: GetRid(), mask: HitscanMask);

		float shotLength = hit.Hit ? hit.Distance : HitscanRange;
		using (MiniProfiler.SampleClient("NetworkPlayer.SmokeVoxelField.DisturbAll")) SmokeVoxelField.DisturbAll(Movement.LastShotOrigin, Movement.LastShotDirection, shotLength);

		if (_fpsWeapon != null && _currentWeapon != null)
		{
			Vector3 start = _currentWeapon.GetMuzzleWorldPosition();
			Vector3 end = hit.Hit ? hit.Position : start + Movement.LastShotDirection * HitscanRange;
			if (_currentWeapon.ShouldSpawnTracer())
				BulletTracer.Spawn(GetTree(), start, end, _currentWeapon.TracerColor, _currentWeapon.TracerWidth, _currentWeapon.TracerSpeed, _currentWeapon.TracerStreakLength);
			_currentWeapon.MuzzleSmoke();
			_currentWeapon.MuzzleFlash();
		}

		if (hit.Hit)
		{
			using (MiniProfiler.SampleClient("NetworkPlayer.BulletImpactManager.Spawn")) BulletImpactManager.Instance?.Spawn(hit.Position, hit.Normal, hit.Material);
			if (Dbg.Enabled)
			{
				string typeName = hit.Collider?.GetType().Name ?? "?";
				string parent = hit.Collider?.GetParent()?.Name ?? "?";
				string groupsStr = "<none>";
				if (hit.Collider != null)
				{
					var groups = hit.Collider.GetGroups();
					if (groups.Count > 0)
					{
						var parts = new string[groups.Count];
						for (int i = 0; i < groups.Count; i++) parts[i] = groups[i].ToString();
						groupsStr = string.Join(",", parts);
					}
				}
				Dbg.Print($"[hit] tick={CurrentTick} | {hit.Collider?.Name ?? "?"} ({typeName}) parent={parent} | mat={hit.Material} groups=[{groupsStr}] | pos={hit.Position:F2} dist={hit.Distance:F2}m");
			}
		}
		else
		{
			Dbg.Print($"[hit] tick={CurrentTick} | NO HIT (origin={Movement.LastShotOrigin:F2} dir={Movement.LastShotDirection:F2})");
		}
	}

	protected virtual WeaponButtons SampleWeaponButtons() => default;

	protected virtual void ResolveActiveSlot() { }

	protected virtual (uint projectileId, byte ownerNetId) RegisterGrenadeThrow(Vector3 origin, Vector3 vel) => (0u, 0);

	protected virtual MovementInput BuildMovementInput(float dt) => new()
	{
		TickIndex = CurrentTick,
		Dt = dt,
		Weapon = ConVars.Weapons.AR15,
		OnFloor = IsOnFloor(),
		TouchingWall = IsOnWall(),
		WallNormal = IsOnWall() ? GetWallNormal() : Vector3.Zero,
		HeadroomBlocked = ComputeHeadroomBlocked(false),
		ViewYaw = Rotation.Y,
		ViewPitch = HeadPitch != null ? HeadPitch.Rotation.X : 0f,
	};

	/// <summary>True when a ceiling is too low to stand up from a crouch — sweeps the capsule upward by the
	/// crouch-to-stand rise (plus clearance). Gated on the crouch blend so upright players never pay for it.</summary>
	protected bool ComputeHeadroomBlocked(bool crouchHeld)
	{
		if (crouchHeld || Movement.CrouchBlend <= 0.01f) return false;
		float rise = StandHeight - CrouchHeight + 0.02f;
		if (rise <= 0f) return false;
		return TestMove(GlobalTransform, new Vector3(0f, rise, 0f));
	}

	protected void ApplyCrouchHeight()
	{
		CharacterSetup.ApplyCrouchHeight(_capsule, BodyCollision, StandHeight, CrouchHeight, Movement.CrouchBlend);
		if (HeadPitch != null)
		{
			float blend = Movement.CrouchBlend;
			float y = Mathf.Lerp(StandEyeHeight, CrouchEyeHeight, blend);
			HeadPitch.Position = new Vector3(_headBasePos.X, y, _headBasePos.Z);
		}
	}

	protected void TryStepUp(float dt)
	{
		if (StepMaxHeight <= 0f || !IsOnFloor()) return;

		Vector3 horizVel = new Vector3(Velocity.X, 0f, Velocity.Z);
		Vector3 wishLocal = _lastMovementInput.WishDir;
		bool hasInput = wishLocal.LengthSquared() > 0.01f;
		bool hasVel = horizVel.LengthSquared() >= 0.25f;
		if (!hasInput && !hasVel) return;

		if (CurrentTick - _stepupLastBlockedTick < StepupBlockedCooldownTicks
			&& GlobalPosition.DistanceSquaredTo(_stepupLastBlockedPos) < 0.01f)
			return;

		Vector3 inputDir = hasInput
			? (Transform.Basis * wishLocal.Normalized())
			: horizVel.Normalized();
		inputDir.Y = 0f;
		if (inputDir.LengthSquared() < 0.0001f) return;
		inputDir = inputDir.Normalized();
		float testSpeed = Mathf.Max(horizVel.Length(), ConVars.Sv.WalkSpeed);
		Vector3 horizMove = inputDir * testSpeed * dt * 2f;
		Transform3D startTrans = GlobalTransform;

		if (!TestMove(startTrans, horizMove)) return;

		Vector3 upMove = new Vector3(0f, StepMaxHeight, 0f);
		if (TestMove(startTrans, upMove))
		{
			Dbg.Print($"[stepup] BLOCKED — no headroom (ceiling above, StepMaxHeight={StepMaxHeight:F2}m)");
			_stepupLastBlockedPos = GlobalPosition;
			_stepupLastBlockedTick = CurrentTick;
			return;
		}

		Transform3D elevated = startTrans.Translated(upMove);
		if (TestMove(elevated, horizMove))
		{
			if (Dbg.Enabled && Time.GetTicksMsec() - _lastStepupBlockedLogMs > 1000)
			{
				_lastStepupBlockedLogMs = Time.GetTicksMsec();
				Vector3 dbgProbeFrom = startTrans.Origin + inputDir * (CapsuleRadius + 0.1f) + Vector3.Up * 5f;
				_rayQuery.From = dbgProbeFrom;
				_rayQuery.To = dbgProbeFrom + Vector3.Down * 10f;
				if (GetWorld3D().DirectSpaceState.IntersectRayInto(_rayQuery, _rayResult))
				{
					float h = _rayResult.GetPosition().Y - startTrans.Origin.Y;
					if (h > StepMaxHeight && h < 2.0f)
						Dbg.Print($"[stepup] BLOCKED — obstacle height ≈ {h:F2}m → crouch-jump (up to ~{MantleMinHeight:F1}m) or mantle (up to {MantleMaxHeight:F2}m) required.");
				}
			}
			_stepupLastBlockedPos = GlobalPosition;
			_stepupLastBlockedTick = CurrentTick;
			return;
		}

		Vector3 fwd = inputDir;
		Vector3 probeFrom = startTrans.Origin + fwd * (CapsuleRadius + 0.1f)
			+ new Vector3(0f, StepMaxHeight + 0.15f, 0f);
		var space = GetWorld3D().DirectSpaceState;
		_rayQuery.From = probeFrom;
		_rayQuery.To = probeFrom + new Vector3(0f, -(StepMaxHeight + 0.35f), 0f);
		if (!space.IntersectRayInto(_rayQuery, _rayResult))
		{
			if (Dbg.Enabled && Time.GetTicksMsec() - _lastStepupBlockedLogMs > 1000)
			{
				_lastStepupBlockedLogMs = Time.GetTicksMsec();
				Dbg.Print("[stepup] no step surface detected → no lift");
			}
			_stepupLastBlockedPos = GlobalPosition;
			_stepupLastBlockedTick = CurrentTick;
			return;
		}
		float actualStep = _rayResult.GetPosition().Y - startTrans.Origin.Y;
		if (Mathf.Abs(actualStep) < 0.05f) return;
		if (actualStep <= 0.02f || actualStep > StepMaxHeight)
		{
			if (Dbg.Enabled && Time.GetTicksMsec() - _lastStepupBlockedLogMs > 1000)
			{
				_lastStepupBlockedLogMs = Time.GetTicksMsec();
				Dbg.Print($"[stepup] obstacle {actualStep:F2}m outside [0.02..{StepMaxHeight:F2}m] → no lift (jump/mantle required)");
			}
			_stepupLastBlockedPos = GlobalPosition;
			_stepupLastBlockedTick = CurrentTick;
			return;
		}
		GlobalPosition += new Vector3(0f, actualStep, 0f);
		var v = Velocity;
		if (v.Y < 0f) v.Y = 0f;
		float stepPen = ConVars.Sv.StepUpSpeedPenalty * Mathf.Clamp(actualStep / Mathf.Max(0.01f, StepMaxHeight), 0f, 1f);
		if (stepPen > 0f) { v.X *= 1f - stepPen; v.Z *= 1f - stepPen; }
		Velocity = v;
		if (Dbg.Enabled && Time.GetTicksMsec() - _lastStepupSuccessLogMs > 200)
		{
			_lastStepupSuccessLogMs = Time.GetTicksMsec();
			Dbg.Print($"[stepup] +{actualStep:F3}m (ground threshold) | speed={horizVel.Length():F1}");
		}
	}

	protected virtual void OnFootstepEvent(HitInfo ground, StringName material) { }

	protected virtual void OnLandEvent(float impact) { }

	protected virtual void OnJumpEvent() { }

	protected virtual void OnDropMagEvent() { }

	/// <summary>Hitbox rig accessor (NetServer debug broadcasts need positions).</summary>
	/// <summary>The lag-compensation hitbox rig.</summary>
	public HitboxRig GetHitboxRig() => _hitboxRig;

	/// <summary>Called by NetServer once per tick (after BoneAttachment3D updates) — snapshots all hitbox
	/// GlobalTransforms into the ring buffer. Server agent only.</summary>
	/// <summary>Stores the current hitbox transforms for the tick, for lag-compensated hit checks.</summary>
	public void PushBoneHistory(uint tick)
	{
		if (BoneHistory == null || _hitboxRig == null) return;
		BoneHistory.Push(tick, _hitboxRig.CollisionShapes);
	}

	/// <summary>Resets render-interp and reconciliation after a teleport so the first frame doesn't lerp from the old position.</summary>
	public void ResetInterpToCurrentPos()
	{
		_prevPhysicsPos = GlobalPosition;
		_currentPhysicsPos = GlobalPosition;
		_ticksSinceSpawn = 0;
		Prediction.Clear();
		_visualErrorOffset = Vector3.Zero;
		_activeBleedRate = Mathf.Max(0.01f, ConVars.Cl.ReconBleedNormal);
		_lastFirePressUsec = 0;
	}

	/// <summary>Per-physics-tick driver for the local player and server agent; skips puppets and frozen agents.</summary>
	public override void _PhysicsProcess(double delta)
	{
		if (IsPuppet) return;
		if (IsFrozen) return;
		if (!IsLocalPlayer && !IsServerAgent) return;
		using var _prof = IsServerAgent ? MiniProfiler.SampleServer("NetworkPlayer._PhysicsProcess") : MiniProfiler.SampleClient("NetworkPlayer._PhysicsProcess (Local)");

		_prevTickStartUsec = _tickStartUsec;
		_tickStartUsec = Time.GetTicksUsec();

		if (IsLocalPlayer)
		{
			double nowSec = Time.GetTicksMsec() / 1000.0;
			if (nowSec - _reconcileWindowStartSec >= 1.0)
			{
				NetStats.ReconcilesPerSec = _reconcileCountWindow;
				_reconcileCountWindow = 0;
				_reconcileWindowStartSec = nowSec;
			}
		}

		GlobalPosition = _currentPhysicsPos;
		_prevPhysicsPos = _currentPhysicsPos;

		FixedTick(_fixedDt);

		_currentPhysicsPos = GlobalPosition;

		using (MiniProfiler.SampleClient("NetworkPlayer.UpdateTpsBodyAim")) UpdateTpsBodyAim();
	}

	/// <summary>Syncs spine twist/pitch onto AimModifier; the server-agent path mirrors the puppet's body-yaw so lag-comp poses match.</summary>
	public void UpdateTpsBodyAim()
	{
		if (IsServerAgent && AimModifier != null && NetInputSource.HasValue)
		{
			float svViewYaw = NetInputSource.Value.ViewYaw;
			if (!_serverBodyYawInitialized)
			{
				_serverSmoothedBodyYaw = svViewYaw;
				_serverBodyYawInitialized = true;
			}
			Vector3 hVel = new Vector3(Velocity.X, 0f, Velocity.Z);
			bool moving = hVel.LengthSquared() > 1.0f;
			float rate = moving ? ServerBodyYawRateMoving : ServerBodyYawRateStanding;
			float lerpT = Mathf.Min(1f, rate * _fixedDt);
			_serverSmoothedBodyYaw = Mathf.LerpAngle(_serverSmoothedBodyYaw, svViewYaw, lerpT);
			float postTwist = Mathf.Wrap(svViewYaw - _serverSmoothedBodyYaw, -Mathf.Pi, Mathf.Pi);
			if (Mathf.Abs(postTwist) > ServerBodyYawMaxTwistRad)
				_serverSmoothedBodyYaw = svViewYaw - Mathf.Sign(postTwist) * ServerBodyYawMaxTwistRad;
			var rot = Rotation; rot.Y = _serverSmoothedBodyYaw; Rotation = rot;
			AimModifier.SpineTwist = Mathf.Wrap(svViewYaw - _serverSmoothedBodyYaw, -Mathf.Pi, Mathf.Pi);
			return;
		}

		if (ViewMode != ViewMode.Tps)
		{
			if (AimModifier != null) AimModifier.SpineTwist = 0f;
			return;
		}

		float twist = IsPuppet ? PuppetSpineTwist : 0f;
		if (AimModifier != null)
			AimModifier.SpineTwist = twist;
		if (TpsAnimTree != null)
			TpsAnimTree.Set(_pTpsAimAdd, Movement?.AdsBlend ?? 0f);
	}
}
