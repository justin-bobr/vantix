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

namespace Vantix.Character;

/// <summary>Local-player cosmetic view (per-frame): viewmodel, cameras, procedural sway, locomotion tree,
/// ADS crosshair, editor preview. Only the local player runs this; puppets/server use UpdateTpsBodyAim.</summary>
public partial class LocalPlayer
{
	private GrenadeAimGuide _aimGuide;
	private readonly List<Vector3> _aimPath = new();
	private int _aimDbg;

	private static readonly StringName _pStandWalk = "parameters/StandWalk/blend_position";
	private static readonly StringName _pAimLoco = "parameters/AimLoco/blend_position";
	private static readonly StringName _pCrouchLoco = "parameters/CrouchLoco/blend_position";
	private static readonly StringName _pStandRun = "parameters/StandRun/blend_amount";
	private static readonly StringName _pStandSprint = "parameters/StandSprint/blend_amount";
	private static readonly StringName _pAimMix = "parameters/AimMix/blend_amount";
	private static readonly StringName _pCrouchMix = "parameters/CrouchMix/blend_amount";
	private static readonly StringName _pGripAdd = "parameters/GripAdd/add_amount";
	private static readonly StringName _pGripAimBlend = "parameters/GripAimBlend/blend_amount";
	private static readonly StringName _pActionActive = "parameters/Action/active";
	private static readonly StringName _pActionRequest = "parameters/Action/request";
	private static readonly StringName _pLocoStopRequest = "parameters/LocoStop/request";
	private static readonly StringName _pJumpLoopBlend = "parameters/JumpLoopBlend/blend_amount";
	private static readonly StringName _pJumpStartRequest = "parameters/JumpStartShot/request";
	private static readonly StringName _pJumpEndRequest = "parameters/JumpEndShot/request";
	private static readonly StringName _pJumpAddAmount = "parameters/JumpAdd/add_amount";
	private static readonly StringName _pInfluence = "influence";

	private int _vmLastShotIndex;
	private bool _vmWasReloading, _vmWasInspecting;
	private float _jumpLoopBlend;
	private float _airTime;
	private bool _vmWasAirborne;
	private bool _wasAirborneRaw;
	private bool _jumpInitiated;
	private float _fallStartY;
	private float _airMaxFallDist;
	private const float JumpBlendInSpeed = 12f;
	private const float JumpBlendOutSpeed = 9f;

	private Vector3 _jumpKickPos;
	private Vector3 _jumpKickVel;
	private float _jumpKickPitch;
	private float _jumpKickPitchVel;

	private float _stepSmoothOffset;
	private float _smoothBodyY;
	private bool _stepYInit;
	private float _bobScale = 1f;

	private float _sprintFovBlend;
	private float _sprintBlurBlend;
	private ShaderMaterial _sprintBlurMat;
	private ColorRect _sprintBlurRect;
	private CanvasLayer _sprintBlurLayer;
	private static readonly StringName _pSprintShader = "sprint";

	private PostProcessEffect _cachedPostFx;
	private bool _postFxLookupDone;
	private ShaderMaterial _viewmodelBlurMat;
	private bool _viewmodelBlurLookupDone;
	private static readonly StringName _pAdsBlendShader = "ads_blend";
	private static readonly StringName _pSharpenShader = "sharpen_strength";

	private Vector2 _lookDelta;
	private float _crouchBlend;
	private float _cantedBlend;
	private Vector3 _swayCurrent;
	private Vector3 _recoilVel;
	private Transform3D _camRestLocal;
	private Transform3D _eyeRest;
	private bool _camRigCaptured;
	private bool _adsTestPrev;
	private MeshInstance3D _adsMarker, _adsLineH, _adsLineV;
	private float _gripAimBlend;
	private bool _montageActive;
	private GripType _pendingGrip;
	private float _gripSwitchDelay = -1f;
	private float _gripBlend;
	private bool _editorTreeReady;
	private bool _wasNearStop = true;
	private bool _sprintStopArmed;
	private bool _walkStopArmed;
	private Vector3 _smoothedDirRatio;
	private Vector3 _dirLeanSpringVel;
	private Vector3 _prevProcVelocity;
	private Vector3 _inertiaTilt;
	private float _prevBodyYaw;
	private float _prevBodyPitch;
	private bool _bodyYawInit;
	private float _bodyYawLag;
	private float _bodyPitchLag;
	private Vector3 _mouseInertia;
	private Vector3 _mouseInertiaSmoothed;
	private Vector3 _viewSwayPos;
	private Vector3 _viewSwayRotDeg;

	private void RenderLocalView(double delta)
	{
		if (Engine.IsEditorHint())
		{ ApplyEditorPreview((float)delta); return; }
		if (IsDead)
			return;
		using var _prof = MiniProfiler.SampleClient("LocalPlayer.RenderLocalView");

		float dt = (float)delta;
		using (MiniProfiler.SampleClient("View.UpdateVisualBlends")) UpdateVisualBlends(dt);
		using (MiniProfiler.SampleClient("View.UpdateGripBlend")) UpdateGripBlend(dt);
		using (MiniProfiler.SampleClient("View.DriveLocomotionTree")) DriveLocomotionTree(dt);
		using (MiniProfiler.SampleClient("View.UpdateViewmodelMontages")) UpdateViewmodelMontages();
		using (MiniProfiler.SampleClient("View.UpdateJumpLayer")) UpdateJumpLayer(dt);
		using (MiniProfiler.SampleClient("View.PollMontageState")) PollMontageState();
		using (MiniProfiler.SampleClient("View.ApplyHandIk")) ApplyHandIk();
		using (MiniProfiler.SampleClient("View.ApplyWeaponOffset")) ApplyWeaponOffset();
		using (MiniProfiler.SampleClient("View.FpsTree.Advance")) _tree?.Advance(dt);
		using (MiniProfiler.SampleClient("View.StepViewmodelProcedural")) StepViewmodelProcedural(dt);
		using (MiniProfiler.SampleClient("View.UpdateProceduralSprings")) UpdateProceduralSprings(dt);
		using (MiniProfiler.SampleClient("View.ApplyModeVisibility")) ApplyModeVisibility();
		using (MiniProfiler.SampleClient("View.ApplyViewmodelProcedural")) ApplyViewmodelProcedural();
		if (ViewMode == ViewMode.Tps && _tpsCam != null)
		{
			using (MiniProfiler.SampleClient("View.UpdateTpsCamera")) UpdateTpsCamera(dt);
		}
		else
		{
			using (MiniProfiler.SampleClient("View.RenderWorldCamera")) RenderWorldCamera(dt);
			using (MiniProfiler.SampleClient("View.RenderFpsCamera")) RenderFpsCamera();
		}
		using (MiniProfiler.SampleClient("View.UpdateAdsPostFx")) UpdateAdsPostFx();
		using (MiniProfiler.SampleClient("View.UpdateAimGuide")) UpdateAimGuide();
	}

	private void UpdateAimGuide()
	{
		bool show = ActiveSlot == 1 && !InputGate.Blocked
			&& Input.IsActionPressed(InputActions.Fire) && _pendingThrowValid;

		if (_aimGuide == null)
		{
			if (!show)
				return;
			_aimGuide = new GrenadeAimGuide();
			GetParent()?.CallDeferred(Node.MethodName.AddChild, _aimGuide);
			return;
		}

		_aimGuide.SetGuideVisible(show);
		if (!show)
			return;

		var space = GetWorld3D()?.DirectSpaceState;
		if (space == null)
			return;

		GrenadeTrajectory.Predict(space, _pendingThrowOrigin, _pendingThrowVel, GetRid(), _aimPath,
			out Vector3 landing, out Vector3 landingNormal);
		_aimGuide.UpdatePath(_aimPath, landing, landingNormal);

		if (Dbg.Enabled && (++_aimDbg & 31) == 0)
			Dbg.Print($"[aimguide] slot={ActiveSlot} charge={GrenadeCharge:F2} pts={_aimPath.Count} " +
				$"landing=({landing.X:F1},{landing.Y:F1},{landing.Z:F1})");
	}

	private void UpdateVisualBlends(float dt)
	{
		float adsTarget = Movement?.AdsBlend ?? (_isAiming ? 1f : 0f);
		_aimBlend = Mathf.MoveToward(_aimBlend, adsTarget, AimBlendSpeed * dt);
		_crouchBlend = Movement?.CrouchBlend ?? 0f;
		_cantedBlend = Mathf.MoveToward(_cantedBlend, _cantedAim && _isAiming ? 1f : 0f, AimBlendSpeed * dt);
		if (_bodyNode != null && _bodyRestCaptured)
			_bodyNode.Position = _bodyRest + Vector3.Down * (CrouchCameraDrop * _crouchBlend);
	}

	private void UpdateGripBlend(float dt)
	{
		if (_gripSwitchDelay >= 0f)
		{
			_gripSwitchDelay -= dt;
			if (_gripSwitchDelay < 0f)
			{ _grip = _pendingGrip; UpdateGripLayer(); }
		}
		bool fastMovement = _sprintAmt > 0.05f || _runAmt > 0.5f;
		_gripBlend = Mathf.MoveToward(_gripBlend, _grip != GripType.Standard && !fastMovement ? 1f : 0f, GripPoseBlendSpeed * dt);
	}

	private void DriveLocomotionTree(float dt)
	{
		if (_tree == null)
			return;
		Vector3 wish = _lastMovementInput.WishDir;
		bool hasMoveInput = wish.LengthSquared() > 0.01f;
		float strafe = Mathf.Clamp(wish.X, -1f, 1f);
		float fwd = Mathf.Clamp(-wish.Z, -1f, 1f);
		Vector2 wishDir = new Vector2(strafe, fwd);
		if (wishDir.LengthSquared() > 1f)
			wishDir = wishDir.Normalized();

		float rawHorizSpeed = new Vector2(Velocity.X, Velocity.Z).Length();
		_smoothedHorizSpeed = Mathf.Lerp(_smoothedHorizSpeed, rawHorizSpeed, Mathf.Clamp(ConVars.Cl.LocoSpeedSmoothRate * dt, 0f, 1f));
		float horizSpeed = _smoothedHorizSpeed;
		float walkMul = ConVars.Weapons.AR15?.MoveSpeedMul ?? 1f;
		float sprintMul = ConVars.Weapons.AR15?.SprintSpeedMul ?? 1f;
		float walkStartSpeed = Mathf.Min(ConVars.Sv.ShiftSpeed, ConVars.Sv.CrouchSpeed) * walkMul;
		float walkSpeed = ConVars.Sv.WalkSpeed * walkMul;
		float sprintSpeed = ConVars.Sv.SprintSpeed * sprintMul;

		float walkMag = Mathf.Clamp(horizSpeed / Mathf.Max(0.01f, walkStartSpeed), 0f, 1f);
		Vector2 targetVel = hasMoveInput ? wishDir * (walkMag * 100f) : Vector2.Zero;
		_simVel = _simVel.Lerp(targetVel, Mathf.Clamp(LocomotionSmoothing * dt, 0f, 1f));
		_tree.Set(_pStandWalk, _simVel);
		_tree.Set(_pAimLoco, _simVel);
		_tree.Set(_pCrouchLoco, _simVel);

		bool fwdMoving = fwd > 0.3f;
		bool backpedalling = fwd < -0.3f;
		bool strafingOnly = !fwdMoving && Mathf.Abs(strafe) > 0.3f;
		bool allowRun = !_vmWasAirborne && !_lastMovementInput.ShiftHeld && !backpedalling && !strafingOnly;

		float runBlend = allowRun ? Mathf.Clamp((horizSpeed - walkStartSpeed) / Mathf.Max(0.01f, walkSpeed - walkStartSpeed), 0f, 1f) : 0f;
		bool sprinting = allowRun && (Movement?.ActuallySprinting ?? false);
		float sprintBlend = sprinting ? Mathf.Clamp((horizSpeed - walkSpeed) / Mathf.Max(0.01f, sprintSpeed - walkSpeed), 0f, 1f) : 0f;
		_runAmt = Mathf.MoveToward(_runAmt, runBlend, SpeedBlendRate * dt);
		_sprintAmt = Mathf.MoveToward(_sprintAmt, sprintBlend, SpeedBlendRate * dt);
		_tree.Set(_pStandRun, _runAmt);
		_tree.Set(_pStandSprint, _sprintAmt);

		float gaitBob = _lastMovementInput.ShiftHeld ? ConVars.Cl.LocoShiftBobScale : Mathf.Lerp(ConVars.Cl.LocoWalkBobScale, ConVars.Cl.LocoSprintBobScale, _sprintAmt);
		_bobScale = gaitBob * Mathf.Lerp(1f, ConVars.Cl.LocoAdsBobScale, _aimBlend);
		_tree.Set(_pAimMix, _aimBlend);
		_tree.Set(_pCrouchMix, _crouchBlend);
		_tree.Set(_pGripAdd, _gripBlend);
		_gripAimBlend = Mathf.MoveToward(_gripAimBlend, _aimBlend > 0.5f ? 1f : 0f, dt / Mathf.Max(GripAimBlendTime, 0.001f));
		_tree.Set(_pGripAimBlend, _gripAimBlend);

		if (horizSpeed >= ConVars.Sv.SprintSpeed * (ConVars.Weapons.AR15?.SprintSpeedMul ?? 1f) * 0.85f) _sprintStopArmed = true;
		if (fwdMoving && horizSpeed > 0.5f) _walkStopArmed = true;
		bool nearStopNow = horizSpeed < 1.5f;
		if (nearStopNow && !_wasNearStop && !hasMoveInput && IsOnFloor())
		{
			if (_sprintStopArmed) TriggerLocoStop(RunEnd);
			else if (_walkStopArmed) TriggerLocoStop(WalkEnd);
		}
		if (nearStopNow) { _sprintStopArmed = false; _walkStopArmed = false; }
		_wasNearStop = nearStopNow;
	}

	private void UpdateViewmodelMontages()
	{
		if (Movement == null)
			return;
		if (Movement.ShotIndex != _vmLastShotIndex)
		{
			bool didFire = Movement.ShotIndex > _vmLastShotIndex;
			_vmLastShotIndex = Movement.ShotIndex;
			if (didFire)
			{
				bool aimed = _aimBlend > 0.5f;
				PlayOneShot(Movement.CurrentMag <= 0 ? FireEmpty : aimed ? FireAimed : Movement.FireMode == 1 ? FireSemi : FireAuto, aimed);
				_currentWeapon?.Fire();
				AddRecoilKick(aimed ? RecoilImpulseAimed : RecoilImpulseHipfire);
			}
		}
		bool reloading = Movement.IsReloading;
		if (reloading && !_vmWasReloading)
		{
			bool aimed = _aimBlend > 0.5f;
			bool empty = Movement.CurrentMag <= 0;
			PlayOneShot(empty ? (aimed ? ReloadEmptyAimed : ReloadEmpty) : (aimed ? ReloadAimed : Reload), aimed);
			if (empty)
				_currentWeapon?.ReloadEmpty();
			else
			{ _currentWeapon?.Reload(); _currentWeapon?.DropMagazine(); }
		}
		_vmWasReloading = reloading;
		bool inspecting = Movement.IsInspecting;
		if (inspecting && !_vmWasInspecting)
		{ PlayOneShot(Inspect); _currentWeapon?.Inspect(); }
		_vmWasInspecting = inspecting;
	}

	private void UpdateJumpLayer(float dt)
	{
		if (_tree == null)
			return;
		bool airborneRaw = !IsOnFloor();
		_airTime = airborneRaw ? _airTime + dt : 0f;
		if (!airborneRaw)
		{
			if (_wasAirborneRaw) _jumpInitiated = false;
			_fallStartY = GlobalPosition.Y;
			_airMaxFallDist = 0f;
		}
		else
		{
			_airMaxFallDist = Mathf.Max(_airMaxFallDist, _fallStartY - GlobalPosition.Y);
		}
		_wasAirborneRaw = airborneRaw;
		bool airborne = airborneRaw && (_jumpInitiated || _airMaxFallDist > ConVars.Cl.JumpMinFallHeight);

		if (airborne && !_vmWasAirborne)
			Dbg.Print($"[Jump] loop begin (airTime={_airTime:0.00}, fallDist={_airMaxFallDist:0.00}, jump={_jumpInitiated})");
		_vmWasAirborne = airborne;

		float target = airborne ? 1f : 0f;
		_jumpLoopBlend = Mathf.MoveToward(_jumpLoopBlend, target, (airborne ? JumpBlendInSpeed : JumpBlendOutSpeed) * dt);
		float addAmount = Mathf.Lerp(1f, 0.5f, _aimBlend);
		_tree.Set(_pJumpLoopBlend, _jumpLoopBlend);
		_tree.Set(_pJumpAddAmount, addAmount);
		if (Dbg.Enabled && _jumpLoopBlend > 0.01f && _jumpLoopBlend < 0.99f)
			Dbg.Print($"[Jump] loop blend={_jumpLoopBlend:0.00} addAmount={addAmount:0.00}");
	}

	private void AddJumpKick()
	{
		_jumpInitiated = true;
		_tree.Set(_pJumpStartRequest, (int)AnimationNodeOneShot.OneShotRequest.Fire);
		if (ConVars.Cl.JumpKickEnabled)
			_jumpKickPos.Y -= ConVars.Cl.JumpImpulseDip;
		Dbg.Print("[Jump] start fired");
	}

	private bool AddLandKick(float impactSpeed)
	{
		if (!_jumpInitiated && _airMaxFallDist < ConVars.Cl.JumpMinFallHeight)
		{
			Dbg.Print($"[Jump] land ignored (fallDist {_airMaxFallDist:0.00} < {ConVars.Cl.JumpMinFallHeight:0.00}m)");
			return false;
		}
		_tree.Set(_pJumpEndRequest, (int)AnimationNodeOneShot.OneShotRequest.Fire);
		if (ConVars.Cl.JumpKickEnabled)
		{
			float scale = Mathf.Min(impactSpeed / ConVars.Cl.LandImpactSpeedRef, ConVars.Cl.LandImpactMaxScale);
			_jumpKickPos.Y -= ConVars.Cl.LandImpulseDip * scale;
			_jumpKickPos.Z -= ConVars.Cl.LandImpulseForward * scale;
			_jumpKickPitch += ConVars.Cl.LandPitchDown * scale;
		}
		Dbg.Print($"[Jump] end fired (impact={impactSpeed:0.0} m/s)");
		return true;
	}

	private void PollMontageState()
	{
		if (_tree == null)
			return;
		if (_montageActive && !_tree.Get(_pActionActive).AsBool())
			_montageActive = false;
	}

	private void ApplyHandIk()
	{
		float ikInfluence = IkEnabled ? 1f : 0f;
		_leftHandFabrik?.Set(_pInfluence, ikInfluence);
		_rightHandFabrik?.Set(_pInfluence, ikInfluence);
	}

	private void UpdateProceduralSprings(float dt)
	{
		Vector3 swayTarget = new(
			Mathf.Clamp(-_lookDelta.Y * SwayLookFactor, -SwayMaxDegrees, SwayMaxDegrees),
			Mathf.Clamp(-_lookDelta.X * SwayLookFactor, -SwayMaxDegrees, SwayMaxDegrees),
			0f);
		_swayCurrent = _swayCurrent.Lerp(swayTarget, Mathf.Clamp(dt * SwaySpringSpeed, 0f, 1f));
		float rk = RecoilStiffness, rm = Mathf.Max(0.05f, RecoilMass);
		float rc = RecoilDamping * 2f * Mathf.Sqrt(rk * rm);
		_recoilVel += (-_recoilCurrent * rk - _recoilVel * rc) / rm * dt;
		_recoilCurrent += _recoilVel * dt;

		float jk = ConVars.Cl.JumpKickStiffness, jc = ConVars.Cl.JumpKickDamping;
		_jumpKickVel += (-_jumpKickPos * jk - _jumpKickVel * jc) * dt;
		_jumpKickPos += _jumpKickVel * dt;
		_jumpKickPitchVel += (-_jumpKickPitch * jk - _jumpKickPitchVel * jc) * dt;
		_jumpKickPitch += _jumpKickPitchVel * dt;

		float bodyY = GlobalPosition.Y;
		bool realAir = _airTime > 0.15f || Mathf.Abs(Velocity.Y) > 3.0f;
		if (!_stepYInit || realAir)
		{
			_smoothBodyY = bodyY;
			_stepYInit = true;
			_stepSmoothOffset = 0f;
		}
		else
		{
			_smoothBodyY = Mathf.Lerp(_smoothBodyY, bodyY, Mathf.Clamp(ConVars.Cl.StepSmoothRate * dt, 0f, 1f));
			_stepSmoothOffset = ConVars.Cl.StepSmoothEnabled
				? Mathf.Clamp(bodyY - _smoothBodyY, -ConVars.Cl.StepSmoothMaxOffset, ConVars.Cl.StepSmoothMaxOffset)
				: 0f;
		}

		_lookDelta = Vector2.Zero;
	}

	private void StepViewmodelProcedural(float dt)
	{
		Vector3 vel = GlobalTransform.Basis.Inverse() * new Vector3(Velocity.X, 0f, Velocity.Z);

		float speed = vel.Length();
		Vector3 dir = speed > 0.01f ? vel / speed : Vector3.Zero;
		Vector3 dirRatio = dir * Mathf.Min(speed / Mathf.Max(0.01f, LeanReferenceSpeed), 1.2f);
		Vector3 leanAccel = (dirRatio - _smoothedDirRatio) * DirectionLeanStiffness - _dirLeanSpringVel * DirectionLeanDamping;
		_dirLeanSpringVel += leanAccel * dt;
		_smoothedDirRatio += _dirLeanSpringVel * dt;

		Vector3 accel = dt > 0.0001f ? (vel - _prevProcVelocity) / dt : Vector3.Zero;
		_prevProcVelocity = vel;
		_inertiaTilt += new Vector3(-accel.Z, 0f, accel.X) * InertiaTiltStrength * dt;
		_inertiaTilt.X = Mathf.Clamp(_inertiaTilt.X, -InertiaTiltMax, InertiaTiltMax);
		_inertiaTilt.Z = Mathf.Clamp(_inertiaTilt.Z, -InertiaTiltMax, InertiaTiltMax);
		_inertiaTilt = _inertiaTilt.Lerp(Vector3.Zero, Mathf.Min(1f, InertiaTiltRecovery * dt));

		if (!_bodyYawInit)
		{ _prevBodyYaw = _lookYaw; _prevBodyPitch = _lookPitch; _bodyYawInit = true; }
		float yawDelta = Mathf.AngleDifference(_prevBodyYaw, _lookYaw);
		_prevBodyYaw = _lookYaw;
		float yawRateDeg = Mathf.RadToDeg(yawDelta / Mathf.Max(0.0001f, dt));
		float targetLag = Mathf.Clamp(-yawRateDeg * BodyYawLagStrength, -BodyYawLagMax, BodyYawLagMax);
		_bodyYawLag = Mathf.Lerp(_bodyYawLag, targetLag, Mathf.Min(1f, BodyYawLagSmoothing * dt));

		float pitchDelta = _lookPitch - _prevBodyPitch;
		_prevBodyPitch = _lookPitch;
		float pitchRateDeg = Mathf.RadToDeg(pitchDelta / Mathf.Max(0.0001f, dt));
		float targetPitchLag = Mathf.Clamp(-pitchRateDeg * BodyYawLagStrength, -BodyYawLagMax, BodyYawLagMax);
		_bodyPitchLag = Mathf.Lerp(_bodyPitchLag, targetPitchLag, Mathf.Min(1f, BodyYawLagSmoothing * dt));

		_mouseInertia.Y += _lookDelta.X * MouseInertiaYaw;
		_mouseInertia.X += _lookDelta.Y * MouseInertiaPitch;
		_mouseInertia.X = Mathf.Clamp(_mouseInertia.X, -MouseInertiaMaxPitch, MouseInertiaMaxPitch);
		_mouseInertia.Y = Mathf.Clamp(_mouseInertia.Y, -MouseInertiaMaxYaw, MouseInertiaMaxYaw);
		_mouseInertia = _mouseInertia.Lerp(Vector3.Zero, Mathf.Min(1f, MouseInertiaRecovery * dt));
		bool building = _mouseInertia.LengthSquared() > _mouseInertiaSmoothed.LengthSquared();
		float smoothRate = building ? MouseInertiaSmoothingIn : MouseInertiaSmoothingOut;
		_mouseInertiaSmoothed = _mouseInertiaSmoothed.Lerp(_mouseInertia, Mathf.Min(1f, smoothRate * dt));
	}

	private void ApplyViewmodelProcedural()
	{
		Vector3 movePos = Vector3.Zero;
		Vector3 moveRotDeg = Vector3.Zero;
		Vector3 lookRotDeg = Vector3.Zero;
		if (ViewSwayEnabled)
		{
			if (DirectionLeanEnabled)
			{
				float strafe = _smoothedDirRatio.X;
				float forward = -_smoothedDirRatio.Z;
				movePos += new Vector3(
					strafe * StrafeLeanPos,
					-Mathf.Max(0f, forward) * ForwardLeanPosDown + Mathf.Max(0f, -forward) * ForwardLeanPosDown * 0.6f,
					-forward * ForwardLeanPosForward);
				moveRotDeg += new Vector3(-forward * ForwardLeanPitch, 0f, -strafe * StrafeLeanRoll);
			}
			if (VelocityTiltEnabled)
				moveRotDeg += new Vector3(_inertiaTilt.X, 0f, _inertiaTilt.Z);
			if (BodyYawLagEnabled)
			{
				lookRotDeg.Y += _bodyYawLag;
				lookRotDeg.X += _bodyPitchLag;
			}
			if (MouseInertiaEnabled)
				lookRotDeg += new Vector3(_mouseInertiaSmoothed.X, _mouseInertiaSmoothed.Y, -_mouseInertiaSmoothed.Y * MouseInertiaRollMul);
		}

		float adsMove = Mathf.Lerp(1f, ViewSwayAdsMul, _aimBlend);
		float adsLook = Mathf.Lerp(1f, ViewSwayAdsLookMul, _aimBlend);
		_viewSwayPos = movePos * adsMove;
		_viewSwayRotDeg = moveRotDeg * adsMove + lookRotDeg * adsLook;

		float landKickMul = Mathf.Lerp(1f, ConVars.Cl.JumpKickAdsMul, _aimBlend);
		_viewSwayPos += _jumpKickPos * landKickMul;
		_viewSwayRotDeg.X += _jumpKickPitch * landKickMul;
	}

	private void RenderWorldCamera(float dt)
	{
		if (_cam == null)
			return;
		if (!_camRigCaptured)
		{
			_camRestLocal = _cam.Transform;
			if (_viewmodelCamAnchor != null)
				_eyeRest = _viewmodelCamAnchor.GlobalTransform;
			_cam.Fov = HipFov;
			_camRigCaptured = true;
		}
		Vector3 kick = _swayCurrent * Mathf.Lerp(1f, AimSwayMultiplier, _aimBlend)
			+ _recoilCurrent * Mathf.Lerp(1f, AimRecoilMultiplier, _aimBlend);
		Transform3D bob = _viewmodelCamAnchor != null
			? _eyeRest.AffineInverse() * _viewmodelCamAnchor.GlobalTransform
			: Transform3D.Identity;
		if (_bobScale < 0.999f)
			bob = Transform3D.Identity.InterpolateWith(bob, _bobScale);
		Vector3 swayRot = _viewSwayRotDeg * ViewSwayWorldMul;
		Vector3 swayPos = _viewSwayPos * ViewSwayWorldMul;
		Basis look = Basis.FromEuler(new Vector3(
			Mathf.DegToRad(kick.X + swayRot.X),
			Mathf.DegToRad(swayRot.Y),
			Mathf.DegToRad(kick.Z + swayRot.Z)));
		_cam.Transform = _camRestLocal * new Transform3D(look, swayPos) * bob;
		if (Mathf.Abs(_stepSmoothOffset) > 0.0001f)
		{
			Vector3 gp = _cam.GlobalPosition;
			gp.Y -= _stepSmoothOffset;
			_cam.GlobalPosition = gp;
		}
		bool sprinting = Movement?.ActuallySprinting ?? false;
		_sprintFovBlend = Mathf.Lerp(_sprintFovBlend, sprinting ? 1f : 0f, Mathf.Min(1f, ConVars.Cl.FovBlendSpeed * dt));
		float sprintEased = _sprintFovBlend * _sprintFovBlend * (3f - 2f * _sprintFovBlend);
		float baseFov = HipFov + ConVars.Cl.FovBoost * sprintEased;
		_sprintBlurBlend = Mathf.Lerp(_sprintBlurBlend, sprinting ? 1f : 0f, Mathf.Min(1f, ConVars.Cl.SprintBlurBlendSpeed * dt));
		float blurEased = _sprintBlurBlend * _sprintBlurBlend * (3f - 2f * _sprintBlurBlend);
		UpdateSprintBlur(blurEased);
		_cam.Fov = Mathf.Lerp(_cam.Fov, Mathf.Lerp(baseFov, AimFov, _aimBlend), Mathf.Clamp(dt * AimBlendSpeed, 0f, 1f));
	}

	private void SetupSprintBlur()
	{
		if (_sprintBlurLayer != null || Engine.IsEditorHint())
			return;
		var shader = GD.Load<Shader>("res://shaders/sprint_blur.gdshader");
		if (shader == null)
			return;
		_sprintBlurMat = new ShaderMaterial { Shader = shader };
		_sprintBlurRect = new ColorRect
		{
			Name = "_SprintBlur",
			Material = _sprintBlurMat,
			MouseFilter = Control.MouseFilterEnum.Ignore,
			Visible = false,
		};
		_sprintBlurRect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		_sprintBlurLayer = new CanvasLayer { Name = "_SprintBlurLayer", Layer = -1 };
		_sprintBlurLayer.AddChild(_sprintBlurRect);
		AddChild(_sprintBlurLayer);
	}

	private void UpdateSprintBlur(float sprintEased)
	{
		SetupSprintBlur();
		if (_sprintBlurRect == null)
			return;
		bool show = sprintEased > 0.002f && Settings.MotionBlur;
		_sprintBlurRect.Visible = show;
		if (show)
			_sprintBlurMat.SetShaderParameter(_pSprintShader, sprintEased);
	}

	private void RenderFpsCamera()
	{
		if (_viewmodelCam == null || _viewmodelCamAnchor == null)
			return;
		Transform3D sway = new(
			Basis.FromEuler(new Vector3(Mathf.DegToRad(_viewSwayRotDeg.X), Mathf.DegToRad(_viewSwayRotDeg.Y), Mathf.DegToRad(_viewSwayRotDeg.Z))),
			_viewSwayPos);
		Transform3D vmAnchor = _viewmodelCamAnchor.GlobalTransform;
		if (_bobScale < 0.999f && _camRigCaptured)
			vmAnchor = _eyeRest * Transform3D.Identity.InterpolateWith(_eyeRest.AffineInverse() * vmAnchor, _bobScale);
		_viewmodelCam.GlobalTransform = vmAnchor * MakeOffset(ViewmodelOffsetPosition, ViewmodelOffsetRotation).AffineInverse() * sway;
		if (_cam != null)
			_viewmodelCam.Fov = _cam.Fov;
	}

	private void UpdateAdsPostFx()
	{
		float adsBlend = _aimBlend;
		float dof = Settings.AdsDepthOfField ? adsBlend : 0f;
		ApplyViewmodelAdsBlur(dof);
		ApplyWorldAdsDof(Mathf.Lerp(0f, 0.04f, dof));

		if (!_postFxLookupDone)
		{
			_postFxLookupDone = true;
			foreach (Node n in GetTree().Root.FindChildren("*", "WorldEnvironment", true, false))
			{
				if (n is WorldEnvironment we && !ViewmodelMotionBlur.IsViewmodelEnvironment(we) && we.Compositor is Compositor c)
					foreach (CompositorEffect e in c.CompositorEffects)
						if (e is PostProcessEffect ppe)
						{ _cachedPostFx = ppe; break; }
				if (_cachedPostFx != null)
					break;
			}
		}
		if (_cachedPostFx != null)
			_cachedPostFx.AdsBlend = adsBlend;
		if (PostCanvasFx.Instance != null)
			PostCanvasFx.Instance.AdsBlend = adsBlend;
		if (ViewmodelMotionBlur.Effect != null)
			ViewmodelMotionBlur.Effect.AdsBlend = adsBlend;
	}

	private void ApplyViewmodelAdsBlur(float blend)
	{
		if (!_viewmodelBlurLookupDone)
		{
			_viewmodelBlurLookupDone = true;
			if (_viewmodelLayer != null)
				foreach (Node n in _viewmodelLayer.FindChildren("viewmodel_container", "SubViewportContainer", true, false))
					if (n is SubViewportContainer svc && svc.Material is ShaderMaterial sm)
					{ _viewmodelBlurMat = sm; break; }
		}
		_viewmodelBlurMat?.SetShaderParameter(_pAdsBlendShader, blend);
		_viewmodelBlurMat?.SetShaderParameter(_pSharpenShader, Settings.ViewmodelSharpenStrength);
	}

	private void ApplyWorldAdsDof(float amount)
	{
		if (_cam?.Attributes is not CameraAttributesPractical a)
			return;
		a.DofBlurNearEnabled = false;
		a.DofBlurFarEnabled = true;
		a.DofBlurFarDistance = 35.0f;
		a.DofBlurFarTransition = 30.0f;
		a.DofBlurAmount = amount;
	}

	private static Transform3D MakeOffset(Vector3 posMetres, Vector3 rotDegrees) =>
		new(Basis.FromEuler(new Vector3(Mathf.DegToRad(rotDegrees.X), Mathf.DegToRad(rotDegrees.Y), Mathf.DegToRad(rotDegrees.Z))), posMetres);

	private void ApplyWeaponOffset()
	{
		var wbm = _weaponBoneModifier ??= GetNodeOrNull<WeaponBoneModifier>(WeaponBoneModifierPath);
		if (wbm == null)
			return;
		Transform3D ads = Transform3D.Identity.InterpolateWith(MakeOffset(AdsOffsetPosition, AdsOffsetRotation), _aimBlend);
		Transform3D crouch = Transform3D.Identity.InterpolateWith(MakeOffset(CrouchOffsetPosition, CrouchOffsetRotation), _crouchBlend);
		Transform3D canted = Transform3D.Identity.InterpolateWith(MakeOffset(CantedOffsetPosition, CantedOffsetRotation), _cantedBlend);
		Transform3D recoil = MakeOffset(
			new Vector3(0f, 0f, Mathf.Abs(_recoilCurrent.X) * WeaponRecoilKickback),
			_recoilCurrent * WeaponRecoilRotScale);
		wbm.Transform = ads * crouch * canted * recoil;
	}

	private void PlayOneShot(string anim, bool aimed = false)
	{
		if (string.IsNullOrEmpty(anim) || _tree == null || _actionAnim == null || !_player.HasAnimation(anim))
			return;
		string actionRef = aimed ? ActionRefAim : ActionRefIdle;
		if (_actionRefNode != null)
			_actionRefNode.Animation = actionRef;
		if (_actionRef2Node != null)
			_actionRef2Node.Animation = actionRef;
		_actionAnim.Animation = anim;
		_tree.Set(_pActionRequest, (int)AnimationNodeOneShot.OneShotRequest.Fire);
		_montageActive = true;
	}

	private void TriggerLocoStop(string anim)
	{
		if (_locoStopAnim == null || _tree == null || string.IsNullOrEmpty(anim) || !_player.HasAnimation(anim))
			return;
		_locoStopAnim.Animation = anim;
		_tree.Set(_pLocoStopRequest, (int)AnimationNodeOneShot.OneShotRequest.Fire);
	}

	private void UpdateAdsCrosshair()
	{
		bool anyTest = AdsTestMode || CrouchTestMode || CantedTestMode;
		if (anyTest && !_adsTestPrev)
			SpawnAdsCrosshair();
		else if (!anyTest && _adsTestPrev)
			DespawnAdsCrosshair();
		_adsTestPrev = anyTest;
		if (anyTest)
			PoseAdsCrosshair();
	}

	private void SpawnAdsCrosshair()
	{
		Camera3D cam = _viewmodelCam ?? _cam;
		if (cam == null || _adsMarker != null)
			return;
		uint layer = cam.CullMask != 0 ? cam.CullMask : 1u;
		_adsMarker = MakeCrosshairMesh("_AdsMarker", new SphereMesh { Radius = AdsCalibrationSize, Height = AdsCalibrationSize * 2f }, layer, cam);
		_adsLineH = MakeCrosshairMesh("_AdsLineH", new BoxMesh { Size = new Vector3(100f, AdsCalibrationSize, AdsCalibrationSize) }, layer, cam);
		_adsLineV = MakeCrosshairMesh("_AdsLineV", new BoxMesh { Size = new Vector3(AdsCalibrationSize, 100f, AdsCalibrationSize) }, layer, cam);
		PoseAdsCrosshair();
	}

	private MeshInstance3D MakeCrosshairMesh(string name, Mesh mesh, uint layer, Camera3D parent)
	{
		var mi = new MeshInstance3D
		{
			Name = name,
			Mesh = mesh,
			MaterialOverride = new StandardMaterial3D { AlbedoColor = AdsCalibrationColor, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, NoDepthTest = true },
			Layers = layer,
		};
		parent.AddChild(mi);
		mi.Owner = null;
		return mi;
	}

	private void PoseAdsCrosshair()
	{
		Vector3 pos = new(0f, 0f, -AdsCalibrationDistance);
		float t = AdsCalibrationSize;
		if (_adsMarker != null)
		{ _adsMarker.Position = pos; if (_adsMarker.Mesh is SphereMesh s) { s.Radius = t; s.Height = t * 2f; } SetCrosshairColor(_adsMarker); }
		if (_adsLineH != null)
		{ _adsLineH.Position = pos; if (_adsLineH.Mesh is BoxMesh b) b.Size = new Vector3(100f, t, t); SetCrosshairColor(_adsLineH); }
		if (_adsLineV != null)
		{ _adsLineV.Position = pos; if (_adsLineV.Mesh is BoxMesh b) b.Size = new Vector3(t, 100f, t); SetCrosshairColor(_adsLineV); }
	}

	private void SetCrosshairColor(MeshInstance3D mi) { if (mi.MaterialOverride is StandardMaterial3D m) m.AlbedoColor = AdsCalibrationColor; }

	private void DespawnAdsCrosshair()
	{
		_adsMarker?.QueueFree();
		_adsLineH?.QueueFree();
		_adsLineV?.QueueFree();
		_adsMarker = _adsLineH = _adsLineV = null;
	}

	protected override void ApplyEditorPreview(float dt = 0f)
	{
		var player = GetNodeOrNull<AnimationPlayer>(CharacterAnimationPath);
		var tree = GetNodeOrNull<AnimationTree>(FpsTreePath);
		if (player == null || tree == null)
			return;

		_leftHandFabrik ??= GetNodeOrNull<Node3D>(LeftHandFabrikPath);
		_rightHandFabrik ??= GetNodeOrNull<Node3D>(RightHandFabrikPath);

		if (!_editorTreeReady)
		{
			ResolveWeaponPlayers();
			UpdateActiveWeapon();
			_player = player;
			BuildAnimationTree();
			ApplyViewmodelLayer();
			_editorTreeReady = true;
		}

		bool aiming = _isAiming || AdsTestMode || CantedTestMode;
		_aimBlend = aiming ? 1f : 0f;
		_crouchBlend = (_isCrouched || CrouchTestMode) ? 1f : 0f;
		_cantedBlend = ((_cantedAim && aiming) || CantedTestMode) ? 1f : 0f;

		tree.Set(_pAimMix, _aimBlend);
		tree.Set(_pStandSprint, _sprintAmt);
		tree.Set(_pStandRun, Mathf.Max(_runAmt, _sprintAmt));
		tree.Set(_pCrouchMix, _crouchBlend);
		tree.Set(_pStandWalk, _simVel);
		tree.Set(_pAimLoco, _simVel);
		tree.Set(_pCrouchLoco, _simVel);
		var editorFastMovement = _sprintAmt > 0.05f || _runAmt > 0.5f;
		float gripAmt = _grip != GripType.Standard && !editorFastMovement ? 1f : 0f;
		tree.Set(_pGripAdd, gripAmt);
		tree.Set(_pGripAimBlend, _aimBlend);
		if (_grip != GripType.Standard && tree.TreeRoot is AnimationNodeBlendTree bt)
		{
			string nonAim = _grip == GripType.Angled ? IdlePoseGripAngled : IdlePoseGripVertical;
			string aim = _grip == GripType.Angled ? AimPoseGripAngled : AimPoseGripVertical;
			if (bt.HasNode("GripPose") && bt.GetNode("GripPose") is AnimationNodeAnimation gp)
				gp.Animation = nonAim;
			if (bt.HasNode("GripPoseAim") && bt.GetNode("GripPoseAim") is AnimationNodeAnimation gpa)
				gpa.Animation = aim;
		}
		_cam ??= GetNodeOrNull<Camera3D>(HeadCameraPath);
		_viewmodelCam ??= GetNodeOrNull<Camera3D>(ViewmodelCameraPath);
		_viewmodelCamAnchor ??= GetNodeOrNull<Node3D>(ViewmodelCameraAnchorPath);
		_tpsCam ??= GetNodeOrNull<Camera3D>(TpsCameraPath);
		_viewmodelLayer ??= GetNodeOrNull<CanvasLayer>(ViewmodelLayerPath);
		ApplyModeVisibility();
		if (_cam != null)
			_cam.Fov = aiming ? AimFov : HipFov;

		var ikInfluence = IkEnabled ? 1f : 0f;
		_leftHandFabrik?.Set(_pInfluence, ikInfluence);
		_rightHandFabrik?.Set(_pInfluence, ikInfluence);

		ApplyWeaponOffset();
		tree.Advance(dt);
		RenderFpsCamera();
		UpdateAdsCrosshair();
	}
}
