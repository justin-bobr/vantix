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

/// <summary>
/// The client-controlled player. Drives its own sim, prediction, input and viewmodel.
/// Spawned from <c>local_player.tscn</c>.
/// </summary>
[Tool, GlobalClass]
public partial class LocalPlayer : NetworkPlayer
{
	private bool _reloadAudioWasActive;

	protected override bool NeedsHitboxRig => false;

	private void SendNetInput()
	{
		if (_isReplaying) return;
		if (InputGate.LocalPlayerFrozen) return;
		var client = NetMain.Instance?.Client;
		if (client == null || !client.Spawned) return;
		bool blocked = InputGate.Blocked;
		bool firePressed = !blocked && Input.IsActionPressed(InputActions.Fire);
		bool reloadPressed = !blocked && Input.IsActionPressed(InputActions.Reload);
		bool inspectPressed = !blocked && Input.IsActionPressed(InputActions.Inspect);
		bool slotIsGrenade = _activeSlot == 1;
		byte fireSubTick = ComputeFireSubTick(firePressed);
		client.SendInput(CurrentTick, _lastMovementInput, firePressed, reloadPressed, inspectPressed, slotIsGrenade, fireSubTick);
	}

	private byte ComputeFireSubTick(bool firePressed)
	{
		if (!firePressed || _lastFirePressUsec == 0) return 0;
		if (_prevTickStartUsec == 0 || _tickStartUsec <= _prevTickStartUsec) return 0;
		if (_lastFirePressUsec < _prevTickStartUsec) return 0;
		if (_lastFirePressUsec >= _tickStartUsec) return 0;
		ulong period = _tickStartUsec - _prevTickStartUsec;
		ulong offset = _lastFirePressUsec - _prevTickStartUsec;
		return (byte)Mathf.Clamp((int)((offset * 256UL) / period), 0, 255);
	}

	private void HandleMouseLook(InputEvent @event)
	{
		if (@event is not InputEventMouseMotion mm) return;
		if (!MouseLookEnabled || Input.MouseMode != Input.MouseModeEnum.Captured) return;

		float sensMul = 1f;
		var weapon = ConVars.Weapons.AR15;
		if (weapon != null && Movement.AdsBlend > 0f)
			sensMul = Mathf.Lerp(1f, weapon.AdsSensitivityMul, Movement.AdsBlendVisual);
		float masterSens = ConVars.Cl.MouseSensitivity * sensMul;
		float yawSens = masterSens * ConVars.Cl.MYaw;
		float pitchSens = masterSens * ConVars.Cl.MPitch;

		RotateY(Mathf.DegToRad(-mm.Relative.X * yawSens));

		float pitchDelta = mm.Relative.Y * pitchSens;
		float pitchDeg = Mathf.RadToDeg(_lookPitch) - (ConVars.Cl.InvertMouseY ? -pitchDelta : pitchDelta);
		pitchDeg = Mathf.Clamp(pitchDeg, ConVars.Cl.MinPitch, ConVars.Cl.MaxPitch);
		_lookPitch = Mathf.DegToRad(pitchDeg);
		if (HeadPitch != null)
		{
			Vector3 rot = HeadPitch.RotationDegrees;
			rot.X = pitchDeg;
			HeadPitch.RotationDegrees = rot;
		}
		_lookYaw = Rotation.Y;
		_lookDelta += new Vector2(-mm.Relative.X * yawSens, mm.Relative.Y * pitchSens);
	}

	private void HandleKeyToggles(InputEvent @event)
	{
		if (@event is not InputEventKey k || !k.Pressed || k.Echo) return;
		if (k.Keycode == Key.G)
			Movement.FireMode = (Movement.FireMode + 1) % 2;
		else if (k.Keycode == Key.F1)
		{
			Movement.UnlimitedAmmo = !Movement.UnlimitedAmmo;
			if (Movement.UnlimitedAmmo)
				Movement.CurrentMag = ConVars.Weapons.AR15.MagazineSize;
		}
		else if (@event.IsActionPressed(InputActions.CameraSwitch))
		{ ViewMode = ViewMode == ViewMode.Tps ? ViewMode.Fps : ViewMode.Tps; ApplyModeVisibility(); }
		else if (k.Keycode == Key.Escape)
			Input.MouseMode = Input.MouseMode == Input.MouseModeEnum.Captured
				? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;
	}

	private void RecordSubtickInputEvent()
	{
		InputBits newBits = ReadInputBitsFromGodot();
		if (newBits == _liveBits) return;
		_liveBits = newBits;
		if (_subtickBuffer.Count >= Packets.MaxSubtickEventsWire) return;
		float yaw = Rotation.Y;
		float pitch = HeadPitch != null ? HeadPitch.Rotation.X : 0f;
		_subtickBuffer.Add(new BufferedSubtickEvent
		{
			Usec = Time.GetTicksUsec(),
			State = newBits,
			Yaw = yaw,
			Pitch = pitch,
		});
	}

	private InputBits ReadInputBitsFromGodot()
	{
		if (InputGate.Blocked) return InputBits.None;
		InputBits b = InputBits.None;
		if (Input.IsActionPressed(InputActions.Forward))  b |= InputBits.Forward;
		if (Input.IsActionPressed(InputActions.Back))     b |= InputBits.Back;
		if (Input.IsActionPressed(InputActions.Left))     b |= InputBits.Left;
		if (Input.IsActionPressed(InputActions.Right))    b |= InputBits.Right;
		if (Input.IsActionPressed(InputActions.Jump))     b |= InputBits.Jump;
		if (Input.IsActionPressed(InputActions.Crouch))   b |= InputBits.Crouch;
		if (Input.IsActionPressed(InputActions.Sprint))   b |= InputBits.Sprint;
		if (Input.IsActionPressed(InputActions.Shift))    b |= InputBits.ShiftWalk;
		if (Input.IsActionPressed(InputActions.Fire))     b |= InputBits.Fire;
		if (Input.IsActionPressed(InputActions.Ads))      b |= InputBits.Ads;
		if (Input.IsActionPressed(InputActions.Reload))   b |= InputBits.Reload;
		if (Input.IsActionPressed(InputActions.Inspect))  b |= InputBits.Inspect;
		if (Input.IsActionPressed(InputActions.Breath))   b |= InputBits.BreathHold;
		return b;
	}

	private SubtickEvent[] FlushSubtickEvents()
	{
		int count = _subtickBuffer.Count;
		if (count == 0 || _prevTickStartUsec == 0 || _tickStartUsec <= _prevTickStartUsec)
		{
			_subtickBuffer.Clear();
			return null;
		}
		ulong period = _tickStartUsec - _prevTickStartUsec;
		float invPeriod = 1f / (float)period;
		SubtickEvent[] arr = new SubtickEvent[count];
		for (int i = 0; i < count; i++)
		{
			BufferedSubtickEvent b = _subtickBuffer[i];
			ulong offset = b.Usec >= _prevTickStartUsec ? b.Usec - _prevTickStartUsec : 0UL;
			if (offset > period) offset = period;
			arr[i] = new SubtickEvent
			{
				TFraction = Mathf.Clamp((float)offset * invPeriod, 0f, 1f),
				StateAfter = b.State,
				ViewYaw = b.Yaw,
				ViewPitch = b.Pitch,
			};
		}
		_subtickBuffer.Clear();
		return arr;
	}

	private ReverbEnv ProbeReverbEnv(HitInfo ground)
	{
		if (IsTunnelGround(ground)) return ReverbEnv.Tunnel;
		var space = GetWorld3D()?.DirectSpaceState;
		if (space == null) return ReverbEnv.Outdoor;
		Vector3 from = GlobalPosition + Vector3.Up * 1.0f;
		HitInfo ceiling = Hitscan.Cast(space, from, Vector3.Up, 8f, exclude: GetRid(), mask: 1u);
		return ceiling.Hit ? ReverbEnv.Indoor : ReverbEnv.Outdoor;
	}

	private void ReplayOneTick(MovementInput savedInput)
	{
		Movement.Velocity = Velocity;
		Movement.Step(savedInput);
		Velocity = Movement.Velocity;

		var fireIn = new FireInput
		{
			TickIndex = savedInput.TickIndex,
			FirePressed = false,
			ReloadPressed = false,
			InspectPressed = false,
			AdsHeld = savedInput.AdsHeld,
			CanFire = false,
			Weapon = savedInput.Weapon,
			Speed = Movement.HorizontalSpeed,
			ShooterPosition = GlobalPosition,
			ViewYaw = savedInput.ViewYaw,
			ViewPitch = savedInput.ViewPitch,
			Dt = savedInput.Dt,
		};
		Movement.FireStep(fireIn);

		_preMoveVelocityY = Velocity.Y;
		Movement.PreMoveHorizSpeed = new Vector3(Velocity.X, 0f, Velocity.Z).Length();
		TryStepUp(_fixedDt);
		MoveAndSlide();
	}

	protected override void OnTickApplied()
	{
		Prediction.Push(CurrentTick, _lastMovementInput, Movement.Snapshot(), GlobalPosition, Velocity);
		LastAppliedInputTick = CurrentTick;
		SendNetInput();
	}

	protected override void OnSimReady()
	{
		base.OnSimReady();
		_waitingForFadeOut = true;
		InputGate.LocalPlayerFrozen = true;
	}

	protected override MovementInput BuildMovementInput(float dt)
	{
		bool blocked = InputGate.Blocked;

		Vector3 wish = Vector3.Zero;
		bool sprintHeld, shiftHeld, crouchHeld, crouchPressed, adsHeld, breathHeld, jumpPressed;
		using (MiniProfiler.SampleClient("LocalPlayer.BuildMovementInput.InputReads"))
		{
			if (!blocked)
			{
				if (Input.IsActionPressed(InputActions.Forward)) wish.Z -= 1f;
				if (Input.IsActionPressed(InputActions.Back)) wish.Z += 1f;
				if (Input.IsActionPressed(InputActions.Left)) wish.X -= 1f;
				if (Input.IsActionPressed(InputActions.Right)) wish.X += 1f;
				if (wish.LengthSquared() > 1f) wish = wish.Normalized();
			}
			sprintHeld    = !blocked && Input.IsActionPressed(InputActions.Sprint);
			shiftHeld     = !blocked && Input.IsActionPressed(InputActions.Shift);
			crouchHeld    = !blocked && Input.IsActionPressed(InputActions.Crouch);
			crouchPressed = !blocked && Input.IsActionJustPressed(InputActions.Crouch);
			adsHeld       = !blocked && Input.IsActionPressed(InputActions.Ads);
			breathHeld    = !blocked && Input.IsActionPressed(InputActions.Breath);
			jumpPressed   = !blocked && Input.IsActionJustPressed(InputActions.Jump);
		}

		bool onFloor, onWall;
		Vector3 wallNormal;
		using (MiniProfiler.SampleClient("LocalPlayer.BuildMovementInput.CollisionState"))
		{
			onFloor    = IsOnFloor();
			onWall     = IsOnWall();
			wallNormal = onWall ? GetWallNormal() : Vector3.Zero;
		}

		WeaponStats weapon;
		using (MiniProfiler.SampleClient("LocalPlayer.BuildMovementInput.WeaponLookup"))
		{
			weapon = ConVars.Weapons.AR15;
		}

		float currentYaw = Rotation.Y;
		float currentPitch = HeadPitch != null ? HeadPitch.Rotation.X : 0f;

		InputBits initialBits = _intervalStartBits;
		float initialYaw = _intervalStartViewYaw;
		float initialPitch = _intervalStartViewPitch;
		SubtickEvent[] events = FlushSubtickEvents();

		_intervalStartBits = _liveBits;
		_intervalStartViewYaw = currentYaw;
		_intervalStartViewPitch = currentPitch;

		return new MovementInput
		{
			TickIndex = CurrentTick,
			WishDir = wish,
			ViewYaw = currentYaw,
			ViewPitch = currentPitch,
			SprintHeld = sprintHeld,
			ShiftHeld = shiftHeld,
			CrouchHeld = crouchHeld,
			CrouchPressed = crouchPressed,
			AdsHeld = adsHeld,
			BreathHoldHeld = breathHeld,
			Weapon = weapon,
			JumpPressed = jumpPressed,
			OnFloor = onFloor,
			TouchingWall = onWall,
			WallNormal = wallNormal,
			HeadroomBlocked = ComputeHeadroomBlocked(crouchHeld),
			Dt = dt,
			Events = events,
			InitialBits = initialBits,
			InitialViewYaw = initialYaw,
			InitialViewPitch = initialPitch,
		};
	}

	protected override WeaponButtons SampleWeaponButtons()
	{
		if (InputGate.Blocked) return default;
		return new WeaponButtons
		{
			Fire = Input.IsActionPressed(InputActions.Fire),
			Reload = Input.IsActionPressed(InputActions.Reload),
			Inspect = Input.IsActionPressed(InputActions.Inspect),
			Ads = Input.IsActionPressed(InputActions.Ads),
		};
	}

	protected override void ResolveActiveSlot()
	{
		if (InputGate.Blocked) return;
		if (Input.IsActionJustPressed(InputActions.SlotWeapon)) _activeSlot = 0;
		if (Input.IsActionJustPressed(InputActions.SlotGrenade)) _activeSlot = 1;
	}

	protected override (uint projectileId, byte ownerNetId) RegisterGrenadeThrow(Vector3 origin, Vector3 vel)
	{
		var client = NetMain.Instance?.Client;
		uint pid = client != null ? client.AllocateProjectileId() : 0u;
		if (client != null)
			client.SendGrenadeSpawn(pid, grenadeType: 0, origin, vel);
		return (pid, NetId);
	}

	protected override void WarmUpAudio()
	{
		if (Audio == null) return;
		Vector3 hiddenPos = new Vector3(0f, -10000f, 0f);
		StringName warmMat = (StringName)"default";
		Audio.PlayStep(hiddenPos, warmMat, 0f, false, sprinting: false);
		Audio.PlayStep(hiddenPos, warmMat, 0f, false, sprinting: true);
		Audio.PlayJump(hiddenPos, warmMat, 0f, false);
		Audio.PlayLand(hiddenPos, warmMat, 0f, false);
	}

	protected override void HandleWeaponAudio()
	{
		if (_isReplaying) return;
		WeaponStats weapon = ConVars.Weapons.AR15;
		if (weapon == null) return;

		Vector3 muzzlePos = GlobalPosition;

		if (Movement.DidFireThisFrame)
			Audio.PlayShoot(weapon, muzzlePos, ProbeReverbEnv(CastGround()));

		if (Movement.DidDryFireThisFrame)
			Audio.PlayDryFire(weapon, muzzlePos);

		bool reloadingNow = Movement.IsReloading;
		if (reloadingNow && !_reloadAudioWasActive)
			Audio.PlayReload(weapon, muzzlePos);
		_reloadAudioWasActive = reloadingNow;
	}

	protected override void OnFootstepEvent(HitInfo ground, StringName material)
	{
		bool inTunnel = IsTunnelGround(ground);
		Audio.PlayStep(GlobalPosition, material, FootstepLogic.StepLoudness, inTunnel, Movement.ActuallySprinting);
		Dbg.Print($"[footstep] tick={CurrentTick} {(FootstepLogic.StepIsLeftFoot ? "L" : "R")} mat={material}{(inTunnel ? " tunnel" : "")} loud={FootstepLogic.StepLoudness:F2} speed={Movement.HorizontalSpeed:F1}");
	}

	protected override void OnLandEvent(float impact)
	{
		bool realLanding = AddLandKick(impact);
		if (realLanding)
		{
			float impact01 = Mathf.Clamp((impact - 1.5f) / 7f, 0f, 1f);
			HitInfo ground = CastGround();
			StringName mat = ground.Hit ? ground.Material : (StringName)"default";
			Audio.PlayLand(GlobalPosition, mat, impact01, IsTunnelGround(ground));
			Dbg.Print($"[land] impact={impact:F1} m/s | pos=({GlobalPosition.X:F1},{GlobalPosition.Y:F1},{GlobalPosition.Z:F1})");
		}
	}

	protected override void OnJumpEvent()
	{
		AddJumpKick();
		HitInfo ground = CastGround();
		StringName mat = ground.Hit ? ground.Material : (StringName)"default";
		Audio.PlayJump(GlobalPosition, mat, Movement.ActuallySprinting ? 1f : 0.75f, IsTunnelGround(ground));
		if (Dbg.Enabled)
		{
			string label = _lastMovementInput.CrouchHeld ? "crouch-jump" : "jump";
			Dbg.Print($"[{label}] vY={Velocity.Y:F2} | horizSpeed={Movement.HorizontalSpeed:F1} | crouch={Movement.CrouchBlend:F1}");
		}
	}

	/// <summary>Renders the interpolated local view each frame and finishes the world-load fade-out once preloads complete.</summary>
	public override void _Process(double delta)
	{
		if (Engine.IsEditorHint()) { RenderLocalView(delta); return; }
		using var _prof = MiniProfiler.SampleClient("LocalPlayer._Process");
		float fraction = (float)Engine.GetPhysicsInterpolationFraction();
		GlobalPosition = _prevPhysicsPos.Lerp(_currentPhysicsPos, fraction) + _visualErrorOffset;
		if (_waitingForFadeOut && FootstepAudio.PendingLoadCount == 0)
		{
			_waitingForFadeOut = false;
			InputGate.LocalPlayerFrozen = false;
			NetMain.Instance?.Client?.SendWorldInitComplete();
			WorldFadeOverlay.Instance?.RequestFadeOut();
			Dbg.Print("[LocalPlayer] world preloads done → unfrozen + WorldInitComplete sent + fade-out requested");
		}
		RenderLocalView(delta);
	}

	/// <summary>Routes input to look and toggles, and records fire timing plus subtick events.</summary>
	public override void _Input(InputEvent @event)
	{
		if (Engine.IsEditorHint()) return;
		HandleMouseLook(@event);
		HandleKeyToggles(@event);
		if (@event.IsActionPressed(InputActions.Fire))
			_lastFirePressUsec = Time.GetTicksUsec();
		if (@event is InputEventKey || @event is InputEventMouseButton || @event is InputEventJoypadButton)
			RecordSubtickInputEvent();
	}

	/// <summary>Reconciles prediction against the server snapshot at the acked tick: small drift bleeds out, large drift replays.</summary>
	public void ApplyServerCorrection(uint ackedTick, Vector3 serverPos, Vector3 serverVel)
	{
		if (!IsLocalPlayer || ackedTick == 0u) return;
		if (_ticksSinceSpawn < SpawnSettleTicks) return;
		if (_isMantling || CurrentTick < _mantleReconcileBlockUntilTick) return;
		if (!Prediction.TryGet(ackedTick, out var entry)) return;

		Vector3 drift = serverPos - entry.PostPos;
		float driftLen = drift.Length();

		Vector3 horizDrift = new Vector3(drift.X, 0f, drift.Z);
		float horizDriftLen = horizDrift.Length();
		float vertDriftLen = Mathf.Abs(drift.Y);
		const float horizEpsilon = 0.08f;
		const float vertEpsilon = 0.20f;
		if (horizDriftLen < horizEpsilon && vertDriftLen < vertEpsilon) return;

		Vector3 visualPosBefore = _currentPhysicsPos + _visualErrorOffset;

		Movement.Restore(entry.State);
		GlobalPosition = serverPos;
		Velocity = serverVel;
		Movement.Velocity = serverVel;
		_isMantling = false;

		const int MaxReplayPerFrame = 64;
		_isReplaying = true;
		try
		{
			int startIdx = Prediction.FindFirstIndexAfter(ackedTick);
			int endIdx = Mathf.Min(Prediction.Count, startIdx + MaxReplayPerFrame);
			for (int i = startIdx; i < endIdx; i++)
			{
				var laterEntry = Prediction.GetAt(i);
				ReplayOneTick(laterEntry.Input);
				Prediction.UpdateEntryState(laterEntry.Tick, Movement.Snapshot(), GlobalPosition, Velocity);
			}
		}
		finally
		{
			_isReplaying = false;
		}

		_prevPhysicsPos = GlobalPosition;
		_currentPhysicsPos = GlobalPosition;
		_correctionPending = Vector3.Zero;

		Vector3 visualDelta = visualPosBefore - GlobalPosition;
		float visualMag = visualDelta.Length();
		float hardSnapThreshold = Mathf.Max(0.01f, ConVars.Cl.ReconSnapThresholdM);
		if (visualMag > hardSnapThreshold)
		{
			_visualErrorOffset = Vector3.Zero;
			_activeBleedRate = Mathf.Max(0.01f, ConVars.Cl.ReconBleedNormal);
		}
		else
		{
			_visualErrorOffset = visualDelta;
			float largeThreshold = Mathf.Max(0f, ConVars.Cl.ReconBleedLargeThresholdM);
			_activeBleedRate = visualMag > largeThreshold
				? Mathf.Max(0.01f, ConVars.Cl.ReconBleedLarge)
				: Mathf.Max(0.01f, ConVars.Cl.ReconBleedNormal);
		}

		if (driftLen > 0.5f && Dbg.Enabled)
			Dbg.Print($"[NetReconcile] REPLAY @ tick={ackedTick} drift={driftLen:F2}m replayed-ticks={Prediction.Count - 1} visualBleed={visualDelta.Length():F2}m");

		NetStats.LastReconcileDriftM = driftLen;
		NetStats.LastReconcileDriftHorizM = horizDriftLen;
		NetStats.LastReconcileDriftVertM = vertDriftLen;
		NetStats.LastReconcileTimeSec = Time.GetTicksMsec() / 1000.0;
		_reconcileCountWindow++;
	}
}
