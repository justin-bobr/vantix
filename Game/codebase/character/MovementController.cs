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
/// Pure movement logic. Tuning injected via <see cref="Sv"/>. Deterministic, no Node3D/physics,
/// so it runs in the server tick and in replay.
/// </summary>
public class MovementController
{
	/// <summary>Tuning. Default = global Sv; replaceable for tests.</summary>
	public SvConVars Sv = ConVars.Sv;

	public Vector3 Velocity;
	public float Stamina = 100f;
	public float CrouchBlend;
	public bool SprintExhausted;
	public bool SprintNeedsRelease;
	public float StaminaRegenTimer;

	/// <summary>0 = automatic, 1 = single-shot.</summary>
	public int FireMode;
	public float FireCooldown;
	public int ShotIndex;
	public bool FirePressedLast;
	public float TimeSinceLastShot = 999f;
	/// <summary>Gameplay aim shift (server-authoritative).</summary>
	public Vector3 AimPunch;
	/// <summary>1 = fully raised (fire-ready), 0 = fully lowered (sprint).</summary>
	public float WeaponRaiseBlend = 1f;
	/// <summary>&gt;0 while reloading; fire blocked.</summary>
	public float ReloadTimer;
	public bool ReloadPressedLast;
	public bool IsReloading => ReloadTimer > 0f;

	public int CurrentMag;
	public int ReserveAmmo;
	/// <summary>Test mode: bullets are not decremented and reserves are refilled forever.</summary>
	public bool UnlimitedAmmo;
	private bool _reloadWasActive;
	private int _pendingReloadIntoMag;
	/// <summary>Bullets moved on the last reload (SFX/HUD/replay).</summary>
	public int LastReloadMoved { get; private set; }
	/// <summary>&gt;0 while inspecting; ADS blocked.</summary>
	public float InspectTimer;
	public bool InspectPressedLast;
	public bool IsInspecting => InspectTimer > 0f;
	/// <summary>0 = hipfire, 1 = full ADS. Lerps with WeaponStats.AdsBlendTime.</summary>
	public float AdsBlend;

	/// <summary>Hold stamina (seconds); drains during a hold, regens otherwise.</summary>
	public float BreathHoldTimer;
	/// <summary>&gt;0 in the shaky recover phase (sway amplified).</summary>
	public float BreathRecoverTimer;
	/// <summary>&gt;0 in the post-recover cooldown; no new hold can start.</summary>
	public float BreathCooldownTimer;
	/// <summary>Recomputed each tick: hold effectively active. Drives the sway multiplier.</summary>
	public bool BreathHoldActiveNow;

	private bool _isAirborne;
	private float _prevVelocityY;
	/// <summary>Read by external systems (e.g. NetworkPlayer's mantle check).</summary>
	public bool IsAirborne => _isAirborne;

	/// <summary>Horizontal speed before MoveAndSlide. Set by NetworkPlayer; the wall-jump check uses it
	/// so wall-absorbed velocity doesn't kill the speed gate.</summary>
	public float PreMoveHorizSpeed;

	private float _coyoteTimer;
	private float _jumpBufferTimer;
	private float _crouchBufferTimer;
	private bool _jumpAwaitingCrouchBoost;

	private float _timeSinceJump = 999f;
	private bool _crouchCancelJumpUsed;

	public bool IsSliding;
	public float SlideTimer;
	/// <summary>&gt;0 inside the slide-stop accuracy window; first shot here gets SlideStopAccuracySpreadMul.</summary>
	public float SlideStopAccuracyTimer;

	/// <summary>Smoothstepped AdsBlend for visuals (pose, FOV, sensitivity). Gameplay uses the linear AdsBlend.</summary>
	public float AdsBlendVisual => AdsBlend * AdsBlend * (3f - 2f * AdsBlend);

	/// <summary>One wall jump per airtime, reset on landing.</summary>
	public bool WallJumpAvailable = true;

	public bool IsWallClinging;
	public float WallClingTimer;
	/// <summary>-1 = uninitialized; lazy-inits from Sv.WallClingChargesPerSpawn on first tick.</summary>
	public int WallClingChargesRemaining = -1;
	/// <summary>Horizontal speed at cling entry, so the cling-exit jump can bypass the wall-jump speed floor.</summary>
	public float WallClingEntrySpeed;

	/// <summary>Resets per-spawn consumables (cling charges, breath stamina). Called by NetworkPlayer on respawn.</summary>
	public void ResetSpawnConsumables()
	{
		WallClingChargesRemaining = Sv.WallClingChargesPerSpawn;
		IsWallClinging = false;
		WallClingTimer = 0f;
		WallClingEntrySpeed = 0f;
		BreathHoldTimer = Sv.BreathHoldDuration;
		BreathRecoverTimer = 0f;
		BreathCooldownTimer = 0f;
		BreathHoldActiveNow = false;
	}

	/// <summary>Full mag + full reserve from weapon stats. Called on spawn and weapon switch.
	/// Server-authoritative; the client replicates it.</summary>
	public void InitializeAmmo(WeaponStats weapon)
	{
		if (weapon == null) { CurrentMag = 0; ReserveAmmo = 0; return; }
		CurrentMag = weapon.MagazineSize;
		ReserveAmmo = weapon.MaxReserveAmmo;
		UnlimitedAmmo = Sv.UnlimitedAmmoDefault;
		ReloadTimer = 0f;
		_reloadWasActive = false;
		_pendingReloadIntoMag = 0;
		LastReloadMoved = 0;
	}

	/// <summary>Full state snapshot. Value-type return, no allocation; safe every tick.</summary>
	public MovementSnapshot Snapshot() => new()
	{
		Velocity = Velocity,
		Stamina = Stamina,
		CrouchBlend = CrouchBlend,
		StaminaRegenTimer = StaminaRegenTimer,
		SprintExhausted = SprintExhausted,
		SprintNeedsRelease = SprintNeedsRelease,
		FireMode = FireMode,
		ShotIndex = ShotIndex,
		FireCooldown = FireCooldown,
		TimeSinceLastShot = TimeSinceLastShot,
		WeaponRaiseBlend = WeaponRaiseBlend,
		ReloadTimer = ReloadTimer,
		InspectTimer = InspectTimer,
		AdsBlend = AdsBlend,
		FirePressedLast = FirePressedLast,
		ReloadPressedLast = ReloadPressedLast,
		InspectPressedLast = InspectPressedLast,
		AimPunch = AimPunch,
		CurrentMag = CurrentMag,
		ReserveAmmo = ReserveAmmo,
		LastReloadMoved = LastReloadMoved,
		PendingReloadIntoMag = _pendingReloadIntoMag,
		UnlimitedAmmo = UnlimitedAmmo,
		ReloadWasActive = _reloadWasActive,
		BreathHoldTimer = BreathHoldTimer,
		BreathRecoverTimer = BreathRecoverTimer,
		BreathCooldownTimer = BreathCooldownTimer,
		BreathHoldActiveNow = BreathHoldActiveNow,
		IsAirborne = _isAirborne,
		CrouchCancelJumpUsed = _crouchCancelJumpUsed,
		PrevVelocityY = _prevVelocityY,
		CoyoteTimer = _coyoteTimer,
		JumpBufferTimer = _jumpBufferTimer,
		TimeSinceJump = _timeSinceJump,
		CrouchBufferTimer = _crouchBufferTimer,
		IsSliding = IsSliding,
		SlideTimer = SlideTimer,
		SlideStopAccuracyTimer = SlideStopAccuracyTimer,
		WallJumpAvailable = WallJumpAvailable,
		IsWallClinging = IsWallClinging,
		WallClingTimer = WallClingTimer,
		WallClingEntrySpeed = WallClingEntrySpeed,
		PreMoveHorizSpeed = PreMoveHorizSpeed,
		WallClingChargesRemaining = WallClingChargesRemaining,
		LastWishDir = LastWishDir,
		LastShotOrigin = LastShotOrigin,
		LastShotDirection = LastShotDirection,
		LastShotPatternEntry = LastShotPatternEntry,
		LastShotSpread = LastShotSpread,
		ActuallySprinting = ActuallySprinting,
		RecentlyFired = RecentlyFired,
		DidJumpThisFrame = DidJumpThisFrame,
		DidWallJumpThisFrame = DidWallJumpThisFrame,
		DidFireThisFrame = DidFireThisFrame,
		DidDryFireThisFrame = DidDryFireThisFrame,
		DidReloadThisFrame = DidReloadThisFrame,
	};

	/// <summary>Restores full state for reconciliation rollback; caller then replays the ticks after it.</summary>
	public void Restore(in MovementSnapshot s)
	{
		Velocity = s.Velocity;
		Stamina = s.Stamina;
		CrouchBlend = s.CrouchBlend;
		StaminaRegenTimer = s.StaminaRegenTimer;
		SprintExhausted = s.SprintExhausted;
		SprintNeedsRelease = s.SprintNeedsRelease;
		FireMode = s.FireMode;
		ShotIndex = s.ShotIndex;
		FireCooldown = s.FireCooldown;
		TimeSinceLastShot = s.TimeSinceLastShot;
		WeaponRaiseBlend = s.WeaponRaiseBlend;
		ReloadTimer = s.ReloadTimer;
		InspectTimer = s.InspectTimer;
		AdsBlend = s.AdsBlend;
		FirePressedLast = s.FirePressedLast;
		ReloadPressedLast = s.ReloadPressedLast;
		InspectPressedLast = s.InspectPressedLast;
		AimPunch = s.AimPunch;
		CurrentMag = s.CurrentMag;
		ReserveAmmo = s.ReserveAmmo;
		LastReloadMoved = s.LastReloadMoved;
		_pendingReloadIntoMag = s.PendingReloadIntoMag;
		UnlimitedAmmo = s.UnlimitedAmmo;
		_reloadWasActive = s.ReloadWasActive;
		BreathHoldTimer = s.BreathHoldTimer;
		BreathRecoverTimer = s.BreathRecoverTimer;
		BreathCooldownTimer = s.BreathCooldownTimer;
		BreathHoldActiveNow = s.BreathHoldActiveNow;
		_isAirborne = s.IsAirborne;
		_crouchCancelJumpUsed = s.CrouchCancelJumpUsed;
		_prevVelocityY = s.PrevVelocityY;
		_coyoteTimer = s.CoyoteTimer;
		_jumpBufferTimer = s.JumpBufferTimer;
		_timeSinceJump = s.TimeSinceJump;
		_crouchBufferTimer = s.CrouchBufferTimer;
		IsSliding = s.IsSliding;
		SlideTimer = s.SlideTimer;
		SlideStopAccuracyTimer = s.SlideStopAccuracyTimer;
		WallJumpAvailable = s.WallJumpAvailable;
		IsWallClinging = s.IsWallClinging;
		WallClingTimer = s.WallClingTimer;
		WallClingEntrySpeed = s.WallClingEntrySpeed;
		PreMoveHorizSpeed = s.PreMoveHorizSpeed;
		WallClingChargesRemaining = s.WallClingChargesRemaining;
		LastWishDir = s.LastWishDir;
		LastShotOrigin = s.LastShotOrigin;
		LastShotDirection = s.LastShotDirection;
		LastShotPatternEntry = s.LastShotPatternEntry;
		LastShotSpread = s.LastShotSpread;
		ActuallySprinting = s.ActuallySprinting;
		RecentlyFired = s.RecentlyFired;
		DidJumpThisFrame = s.DidJumpThisFrame;
		DidWallJumpThisFrame = s.DidWallJumpThisFrame;
		DidFireThisFrame = s.DidFireThisFrame;
		DidDryFireThisFrame = s.DidDryFireThisFrame;
		DidReloadThisFrame = s.DidReloadThisFrame;
	}

	/// <summary>Sway multiplier for everything except breathing. 1 = neutral, &lt;1 during hold, &gt;1 during recover.</summary>
	public float BreathSwayMul
	{
		get
		{
			if (!Sv.BreathHoldEnabled) return 1f;
			if (BreathRecoverTimer > 0f) return Sv.BreathHoldShakySwayMul;
			if (BreathHoldActiveNow) return Sv.BreathHoldSwayMul;
			return 1f;
		}
	}

	/// <summary>Multiplier for breathing oscillation only; less damped than the rest during a hold, amplified during recover.</summary>
	public float BreathBreathingMul
	{
		get
		{
			if (!Sv.BreathHoldEnabled) return 1f;
			if (BreathRecoverTimer > 0f) return Sv.BreathHoldShakyBreathingMul;
			if (BreathHoldActiveNow) return Sv.BreathHoldBreathingMul;
			return 1f;
		}
	}

	/// <summary>Sub-tick view yaw captured at the Fire rising edge. When SubtickFireViewValid, fire uses this
	/// instead of the tick-end view so a mid-flick shot aims at the press moment. Reset each Step.</summary>
	public float SubtickFireViewYaw;
	public float SubtickFireViewPitch;
	public bool SubtickFireViewValid;

	/// <summary>Last frame's body-local WishDir (mantle intent check).</summary>
	public Vector3 LastWishDir { get; private set; }
	public bool ActuallySprinting { get; private set; }
	public bool DidJumpThisFrame { get; private set; }
	public bool DidWallJumpThisFrame { get; private set; }
	public bool DidFireThisFrame { get; private set; }
	/// <summary>One-tick edge: clicked on an empty mag (dry-fire).</summary>
	public bool DidDryFireThisFrame { get; private set; }
	/// <summary>One-tick edge: a reload started. Drives the networked mag-drop.</summary>
	public bool DidReloadThisFrame { get; private set; }
	/// <summary>Last shot's world origin (eye position; server truth, replayable).</summary>
	public Vector3 LastShotOrigin { get; private set; }
	/// <summary>Last shot's world direction (unit), including aim punch, pattern and spread.</summary>
	public Vector3 LastShotDirection { get; private set; }
	public Vector2 LastShotPatternEntry { get; private set; }
	public float LastShotSpread { get; private set; }
	public bool RecentlyFired { get; private set; }
	/// <summary>Horizontal speed (X/Z). Inlined sqrt to skip a Vector3 ctor in hot paths.</summary>
	public float HorizontalSpeed => Mathf.Sqrt(Velocity.X * Velocity.X + Velocity.Z * Velocity.Z);

	private readonly Godot.RandomNumberGenerator _fireRng = new();

	/// <summary>Server-replayable fire step. Updates cooldown, ShotIndex, AimPunch and outputs.</summary>
	public void FireStep(FireInput input)
	{
		DidFireThisFrame = false;
		DidDryFireThisFrame = false;
		DidReloadThisFrame = false;
		FireCooldown = Mathf.Max(0f, FireCooldown - input.Dt);
		ReloadTimer = Mathf.Max(0f, ReloadTimer - input.Dt);
		InspectTimer = Mathf.Max(0f, InspectTimer - input.Dt);
		SlideStopAccuracyTimer = Mathf.Max(0f, SlideStopAccuracyTimer - input.Dt);

		bool reloadNowActive = ReloadTimer > 0f;
		if (_reloadWasActive && !reloadNowActive)
		{
			int moved = Mathf.Min(_pendingReloadIntoMag, ReserveAmmo);
			CurrentMag += moved;
			ReserveAmmo -= moved;
			LastReloadMoved = moved;
			_pendingReloadIntoMag = 0;
		}
		_reloadWasActive = reloadNowActive;

		if (UnlimitedAmmo && ReserveAmmo <= 0 && input.Weapon != null)
			ReserveAmmo = input.Weapon.MaxReserveAmmo;

		bool reloadEdge = input.ReloadPressed && !ReloadPressedLast;
		ReloadPressedLast = input.ReloadPressed;
		if (reloadEdge && !IsReloading && input.Weapon != null)
		{
			int magSize = input.Weapon.MagazineSize;
			int needed = magSize - CurrentMag;
			bool hasReserveOrUnlimited = UnlimitedAmmo || ReserveAmmo > 0;
			if (needed > 0 && hasReserveOrUnlimited)
			{
				ReloadTimer = input.Weapon.ReloadTime;
				InspectTimer = 0f;
				_pendingReloadIntoMag = needed;
				DidReloadThisFrame = true;
			}
		}

		bool inspectEdge = input.InspectPressed && !InspectPressedLast;
		InspectPressedLast = input.InspectPressed;
		if (inspectEdge && !IsReloading && !IsInspecting && input.Weapon != null)
			InspectTimer = input.Weapon.InspectTime;

		bool wantsFire = FireMode == 0 ? input.FirePressed : (input.FirePressed && !FirePressedLast);
		FirePressedLast = input.FirePressed;

		bool weaponReady = WeaponRaiseBlend >= Sv.SprintFireGateBlend;
		bool hasAmmo = CurrentMag > 0;

		if (input.CanFire && weaponReady && !IsReloading && hasAmmo && wantsFire && FireCooldown <= 0f && input.Weapon != null)
		{
			FireCooldown = 1f / Mathf.Max(0.1f, input.Weapon.FireRate);
			InspectTimer = 0f;
			CurrentMag = Mathf.Max(0, CurrentMag - 1);
			DoFire(input);
			TimeSinceLastShot = 0f;
		}
		else if (input.CanFire && weaponReady && !IsReloading && !hasAmmo && wantsFire && FireCooldown <= 0f && input.Weapon != null)
		{
			DidDryFireThisFrame = true;
			FireCooldown = 1f / Mathf.Max(0.1f, input.Weapon.FireRate);
		}

		TimeSinceLastShot += input.Dt;
		float resetDelay = input.Weapon?.PatternResetDelay ?? 0.35f;
		if (TimeSinceLastShot >= resetDelay) ShotIndex = 0;
		RecentlyFired = TimeSinceLastShot < 1.5f / Mathf.Max(0.1f, input.Weapon?.FireRate ?? 10f);

		float aimRec = RecentlyFired ? (input.Weapon?.AimPunchRecoveryFiring ?? 3f) : (input.Weapon?.AimPunchRecoveryReleased ?? 18f);
		AimPunch = AimPunch.Lerp(Vector3.Zero, Mathf.Min(1f, aimRec * input.Dt));
	}

	/// <summary>The actual shot: pattern + spread + bloom, then updates AimPunch and the LastShot* outputs.</summary>
	private void DoFire(FireInput input)
	{
		var w = input.Weapon;
		var pattern = w.RecoilPattern;
		Vector2 p = Vector2.Zero;
		if (pattern != null && pattern.Length > 0)
		{
			int idx = Mathf.Min(ShotIndex, pattern.Length - 1);
			p = pattern[idx] * w.PatternScale;
		}
		ShotIndex++;
		LastShotPatternEntry = p;

		_fireRng.Seed = ((ulong)input.TickIndex * 2654435761u) ^ ((ulong)ShotIndex * 40503u);

		float speed = input.Speed;
		float movementSpread;
		if (speed < 0.05f) movementSpread = 0f;
		else if (speed <= Sv.ShiftSpeed + 0.1f)
			movementSpread = Mathf.Lerp(0f, w.MovementSpreadShift, speed / Mathf.Max(0.01f, Sv.ShiftSpeed));
		else if (speed <= Sv.WalkSpeed + 0.1f)
			movementSpread = Mathf.Lerp(w.MovementSpreadShift, w.MovementSpreadWalk, (speed - Sv.ShiftSpeed) / Mathf.Max(0.01f, Sv.WalkSpeed - Sv.ShiftSpeed));
		else
			movementSpread = Mathf.Lerp(w.MovementSpreadWalk, w.MovementSpread, Mathf.Clamp((speed - Sv.WalkSpeed) / Mathf.Max(0.01f, Sv.SprintSpeed - Sv.WalkSpeed), 0f, 1f));
		movementSpread *= Mathf.Lerp(1f, w.AdsMovementSpreadMul, AdsBlend);
		float bloomT = Mathf.Pow(Mathf.Clamp(ShotIndex / Mathf.Max(1f, w.HipfireBloomShots), 0f, 1f), w.HipfireBloomCurve);
		float bloomScale = Mathf.Lerp(1f, w.AdsBloomMul, AdsBlend);
		float bloomSpread = w.HipfireBloomMax * bloomT * bloomScale;
		float spreadMag = w.HipfireBaseSpread + movementSpread + bloomSpread;
		float adsTarget = _isAirborne ? w.AdsSpreadAirMul : w.AdsSpreadMul;
		spreadMag *= Mathf.Lerp(1f, adsTarget, AdsBlend);
		if (_isAirborne) spreadMag *= w.AirborneSpreadMul;
		if (SlideStopAccuracyTimer > 0f && Sv.SlideStopAccuracyEnabled)
		{
			spreadMag *= Sv.SlideStopAccuracySpreadMul;
			SlideStopAccuracyTimer = 0f;
		}
		if (Sv.BreathHoldEnabled)
		{
			if (BreathRecoverTimer > 0f) spreadMag *= Sv.BreathHoldShakySpreadMul;
			else if (BreathHoldActiveNow) spreadMag *= Sv.BreathHoldSpreadMul;
		}
		LastShotSpread = spreadMag;

		float spreadPitch = _fireRng.RandfRange(-1f, 1f) * spreadMag;
		float spreadYaw = _fireRng.RandfRange(-1f, 1f) * spreadMag;
		AimPunch += new Vector3(-(p.Y + spreadPitch), -p.X + spreadYaw, 0f);
		AimPunch.X = Mathf.Clamp(AimPunch.X, -w.AimPunchMaxClimb, w.AimPunchMaxClimb * 0.2f);
		AimPunch.Y = Mathf.Clamp(AimPunch.Y, -w.AimPunchMaxClimb * 0.8f, w.AimPunchMaxClimb * 0.8f);

		float effYaw = input.ViewYaw + Mathf.DegToRad(AimPunch.Y);
		float effPitch = Mathf.Clamp(input.ViewPitch - Mathf.DegToRad(AimPunch.X), -1.4f, 1.4f);
		float cp = Mathf.Cos(effPitch);
		LastShotDirection = new Vector3(-Mathf.Sin(effYaw) * cp, Mathf.Sin(effPitch), -Mathf.Cos(effYaw) * cp);
		LastShotOrigin = input.ShooterPosition;

		DidFireThisFrame = true;
	}

	/// <summary>Server-replayable movement step. With events, splits the tick into segments at each event's
	/// TFraction and runs the inner step per segment; once-per-tick flags (DidJump/DidWallJump/LastWishDir) are
	/// written here, not in the inner step. Empty events = one segment over the full Dt.</summary>
	public void Step(MovementInput input)
	{
		DidJumpThisFrame = false;
		DidWallJumpThisFrame = false;
		SubtickFireViewValid = false;

		if (input.Events == null || input.Events.Length == 0)
		{
			LastWishDir = input.WishDir;
			RunSubStep(input);
			return;
		}

		InputBits state = input.InitialBits;
		Vector3 wishDir = WishDirFromBits(state);
		float yaw = input.InitialViewYaw;
		float pitch = input.InitialViewPitch;
		bool pendingJump = false;
		bool pendingCrouch = false;

		float tPrev = 0f;
		int n = input.Events.Length;
		for (int i = 0; i <= n; i++)
		{
			float tCur = (i == n) ? 1f : input.Events[i].TFraction;
			float dtPart = (tCur - tPrev) * input.Dt;
			if (dtPart > 0f)
			{
				MovementInput sub = input;
				sub.Dt = dtPart;
				sub.WishDir = wishDir;
				sub.ViewYaw = yaw;
				sub.ViewPitch = pitch;
				sub.SprintHeld = (state & InputBits.Sprint) != 0;
				sub.ShiftHeld = (state & InputBits.ShiftWalk) != 0;
				sub.CrouchHeld = (state & InputBits.Crouch) != 0;
				sub.AdsHeld = (state & InputBits.Ads) != 0;
				sub.BreathHoldHeld = (state & InputBits.BreathHold) != 0;
				sub.JumpPressed = pendingJump;
				sub.CrouchPressed = pendingCrouch;
				RunSubStep(sub);
				pendingJump = false;
				pendingCrouch = false;
			}
			if (i < n)
			{
				SubtickEvent ev = input.Events[i];
				InputBits prev = state;
				state = ev.StateAfter;
				InputBits rising = state & ~prev;
				if ((rising & InputBits.Jump) != 0) pendingJump = true;
				if ((rising & InputBits.Crouch) != 0) pendingCrouch = true;
				if ((rising & InputBits.Fire) != 0 && !SubtickFireViewValid)
				{
					SubtickFireViewYaw = ev.ViewYaw;
					SubtickFireViewPitch = ev.ViewPitch;
					SubtickFireViewValid = true;
				}
				wishDir = WishDirFromBits(state);
				yaw = ev.ViewYaw;
				pitch = ev.ViewPitch;
			}
			tPrev = tCur;
		}

		LastWishDir = wishDir;
	}

	/// <summary>One inner physics segment over Dt (a tick fraction, or the full tick). Doesn't reset the
	/// once-per-tick flags owned by Step.</summary>
	private void RunSubStep(MovementInput input)
	{
		float dt = input.Dt;
		Vector3 velocity = Velocity;

		if (input.OnFloor) WallJumpAvailable = true;

		_timeSinceJump = input.OnFloor ? 999f : _timeSinceJump + dt;
		if (input.OnFloor) _crouchCancelJumpUsed = false;

		UpdateWallCling(ref velocity, input, dt);

		if (!IsWallClinging)
			ApplyGravity(ref velocity, input.OnFloor, dt);
		TryJump(ref velocity, input);
		TryCrouchCancelJump(ref velocity, input);
		UpdateCrouchBlend(input.CrouchHeld, input.HeadroomBlocked, dt);
		UpdateStamina(input);
		UpdateWeaponRaiseBlend(dt);
		UpdateAdsBlend(input, velocity, dt);
		UpdateBreathHold(input, dt);
		UpdateSlide(ref velocity, input, dt);
		float targetSpeed = ComputeTargetSpeed(input);
		ApplyHorizontalMovement(ref velocity, input, targetSpeed, dt);

		Velocity = velocity;
	}

	/// <summary>WASD bitmask → unit local wish dir. +X = strafe right, +Z = back (MovementInput.WishDir
	/// convention). Zero if no movement bits.</summary>
	public static Vector3 WishDirFromBits(InputBits state)
	{
		int x = 0;
		int z = 0;
		if ((state & InputBits.Right) != 0) x++;
		if ((state & InputBits.Left) != 0) x--;
		if ((state & InputBits.Back) != 0) z++;
		if ((state & InputBits.Forward) != 0) z--;
		if (x == 0 && z == 0) return Vector3.Zero;
		return new Vector3(x, 0f, z).Normalized();
	}

	/// <summary>Applies gravity, with an apex-hang reduction for floatier jumps.</summary>
	private void ApplyGravity(ref Vector3 velocity, bool onFloor, float dt)
	{
		if (onFloor) return;
		float g = Sv.Gravity;
		if (Mathf.Abs(velocity.Y) < Sv.ApexHangThreshold) g *= Sv.ApexHangGravityMul;
		velocity.Y -= g * dt;
	}

	/// <summary>Regular jumps, wall jumps, and the coyote / jump-buffer / crouch-buffer windows.</summary>
	private void TryJump(ref Vector3 velocity, MovementInput input)
	{
		float horizSpeed = new Vector3(velocity.X, 0f, velocity.Z).Length();

		if (!_isAirborne) _coyoteTimer = 0f; else _coyoteTimer += input.Dt;
		if (input.JumpPressed) _jumpBufferTimer = Sv.JumpBufferTime;
		else _jumpBufferTimer = Mathf.Max(0f, _jumpBufferTimer - input.Dt);
		if (input.CrouchPressed) _crouchBufferTimer = Sv.CrouchJumpBufferTime;
		else _crouchBufferTimer = Mathf.Max(0f, _crouchBufferTimer - input.Dt);

		if (_jumpAwaitingCrouchBoost && input.CrouchPressed && velocity.Y > 0f
			&& _timeSinceJump < Sv.CrouchJumpBufferTime
			&& input.WishDir.LengthSquared() > 0.01f)
		{
			velocity.Y += Sv.CrouchJumpBonus;
			_jumpAwaitingCrouchBoost = false;
		}
		if (_jumpAwaitingCrouchBoost && (_timeSinceJump >= Sv.CrouchJumpBufferTime || velocity.Y <= 0f))
			_jumpAwaitingCrouchBoost = false;

		bool wantsJump = _jumpBufferTimer > 0f;
		bool grounded = !_isAirborne || _coyoteTimer <= Sv.CoyoteTime;

		if (wantsJump && grounded)
		{
			float speedT = horizSpeed > Sv.JumpSpeedBonusThreshold
				? Mathf.Clamp((horizSpeed - Sv.JumpSpeedBonusThreshold) / Mathf.Max(0.01f, Sv.SprintSpeed - Sv.JumpSpeedBonusThreshold), 0f, 1f)
				: 0f;
			bool hasMovementInput = input.WishDir.LengthSquared() > 0.01f;
			bool crouchForJump = input.CrouchHeld || _crouchBufferTimer > 0f;
			float crouchBonusT = hasMovementInput ? (crouchForJump ? 1f : CrouchBlend) : 0f;
			velocity.Y = Sv.JumpVelocity + speedT * Sv.JumpSpeedBonus + Sv.CrouchJumpBonus * crouchBonusT;

			if (input.TouchingWall && input.WishDir.LengthSquared() > 0.01f)
			{
				Vector3 worldDir = input.BodyBasis * input.WishDir.Normalized();
				float intoWall = worldDir.Dot(-input.WallNormal);
				if (intoWall > 0.5f)
					velocity.Y += Sv.WallAssistBonus;
			}

			bool wantsBoost = input.WishDir.LengthSquared() > 0.01f && horizSpeed < Sv.WalkSpeed * 0.7f;
			if (wantsBoost)
			{
				Vector3 worldDir = input.BodyBasis * input.WishDir.Normalized();
				velocity.X += worldDir.X * Sv.JumpForwardBoost;
				velocity.Z += worldDir.Z * Sv.JumpForwardBoost;
			}

			if (ActuallySprinting && input.WishDir.LengthSquared() > 0.01f)
			{
				Vector3 worldDir = input.BodyBasis * input.WishDir.Normalized();
				velocity.X += worldDir.X * Sv.JumpSprintForwardBoost;
				velocity.Z += worldDir.Z * Sv.JumpSprintForwardBoost;
			}

			DidJumpThisFrame = true;
			IsSliding = false;
			_jumpBufferTimer = 0f;
			_coyoteTimer = Sv.CoyoteTime + 1f;
			_timeSinceJump = 0f;
			_crouchCancelJumpUsed = false;
			_jumpAwaitingCrouchBoost = crouchBonusT < 0.99f && hasMovementInput;
		}
		else if (!input.OnFloor && input.TouchingWall && WallJumpAvailable)
		{
			float effHorizSpeed = Mathf.Max(horizSpeed, Mathf.Max(PreMoveHorizSpeed, IsWallClinging ? WallClingEntrySpeed : 0f));
			if (effHorizSpeed >= Sv.WallJumpMinSpeed)
			{
				float speedFactor = Mathf.Clamp(effHorizSpeed / Mathf.Max(0.01f, Sv.WallJumpSpeedRef), 0.6f, 1.1f);
				Vector3 wallH = new Vector3(input.WallNormal.X, 0f, input.WallNormal.Z);
				if (wallH.LengthSquared() > 0.0001f) wallH = wallH.Normalized();
				else wallH = Vector3.Forward;

				Vector3 lookDir = new Vector3(-Mathf.Sin(input.ViewYaw), 0f, -Mathf.Cos(input.ViewYaw));
				float lookIntoWall = -lookDir.Dot(wallH);
				if (lookIntoWall > 0f)
					lookDir = (lookDir + 2f * lookIntoWall * wallH).Normalized();

				float lw = Sv.WallJumpLookWeight;
				Vector3 jumpDir = lookDir * lw + wallH * (1f - lw);
				if (jumpDir.LengthSquared() > 0.0001f) jumpDir = jumpDir.Normalized();
				else jumpDir = wallH;

				float outSpeed = effHorizSpeed * Sv.WallJumpMomentumKeep + Sv.WallJumpHorizontal * speedFactor;
				velocity.X = jumpDir.X * outSpeed;
				velocity.Z = jumpDir.Z * outSpeed;

				velocity.Y = Mathf.Max(velocity.Y, 0f) + Sv.WallJumpVertical * speedFactor;

				WallJumpAvailable = false;
				DidWallJumpThisFrame = true;
				IsWallClinging = false;
				WallClingTimer = 0f;
			}
		}
	}

	/// <summary>Wall-cling state machine. Freezes against a wall after a sprint+jump approach; a re-jump does a
	/// wall jump with the saved entry speed (bypassing the speed floor). Limited per spawn by WallClingChargesRemaining.</summary>
	private void UpdateWallCling(ref Vector3 velocity, MovementInput input, float dt)
	{
		if (!Sv.WallClingEnabled) return;

		if (WallClingChargesRemaining < 0) WallClingChargesRemaining = Sv.WallClingChargesPerSpawn;

		if (IsWallClinging)
		{
			WallClingTimer -= dt;
			if (WallClingTimer <= 0f || !input.TouchingWall || !_isAirborne)
			{
				IsWallClinging = false;
				return;
			}
			velocity.X = 0f;
			velocity.Y = 0f;
			velocity.Z = 0f;
			return;
		}

		if (WallClingChargesRemaining <= 0) return;
		if (!_isAirborne) return;
		if (!input.TouchingWall) return;
		if (!ActuallySprinting) return;
		if (_timeSinceJump < Sv.WallClingPostJumpGrace) return;
		if (input.WishDir.LengthSquared() < 0.01f) return;
		if (DidWallJumpThisFrame) return;

		Vector3 worldDir = input.BodyBasis * input.WishDir.Normalized();
		float intoWall = worldDir.Dot(-input.WallNormal);
		if (intoWall < Sv.WallClingIntoWallDot) return;

		float horizSpeed = new Vector3(velocity.X, 0f, velocity.Z).Length();
		float entrySpeed = Mathf.Max(horizSpeed, PreMoveHorizSpeed);
		if (entrySpeed < Sv.WallClingMinSpeed) return;

		IsWallClinging = true;
		WallClingTimer = Sv.WallClingDuration;
		WallClingEntrySpeed = entrySpeed;
		WallClingChargesRemaining--;
		velocity.X = 0f;
		velocity.Y = 0f;
		velocity.Z = 0f;
	}

	/// <summary>Crouch-cancel jump: a crouch press in the apex window gives a small vertical boost.
	/// One-shot per airtime, reset on landing or a new jump.</summary>
	private void TryCrouchCancelJump(ref Vector3 velocity, MovementInput input)
	{
		if (!Sv.CrouchCancelJumpEnabled) return;
		if (_crouchCancelJumpUsed) return;
		if (input.OnFloor) return;
		if (!input.CrouchPressed) return;
		if (_timeSinceJump < Sv.CrouchCancelJumpWindowStart) return;
		if (_timeSinceJump > Sv.CrouchCancelJumpWindowEnd) return;
		if (velocity.Y < 0f) return;

		velocity.Y += Sv.CrouchCancelJumpBonus;
		_crouchCancelJumpUsed = true;
	}

	/// <summary>Moves the crouch blend toward 1 (crouched) or 0 (standing). A blocked ceiling holds the
	/// player crouched even after release, so they never stand into geometry above.</summary>
	private void UpdateCrouchBlend(bool crouchHeld, bool headroomBlocked, float dt)
	{
		bool stayCrouched = crouchHeld || headroomBlocked;
		CrouchBlend = Mathf.MoveToward(CrouchBlend, stayCrouched ? 1f : 0f, Sv.CrouchTransitionSpeed * dt);
	}

	/// <summary>Weapon raise blend: toward 0 while sprinting, else 1. Reload overrides sprint-lower so the
	/// weapon stays raised while reloading.</summary>
	private void UpdateWeaponRaiseBlend(float dt)
	{
		bool lower = ActuallySprinting && !IsReloading;
		float target = lower ? 0f : 1f;
		float time = lower ? Sv.SprintLowerTime : Sv.SprintRaiseTime;
		float rate = 1f / Mathf.Max(0.01f, time);
		WeaponRaiseBlend = Mathf.MoveToward(WeaponRaiseBlend, target, rate * dt);
	}

	/// <summary>Slide state machine. Start on crouch-press during a fast sprint, apply friction; end on slow
	/// speed, timeout, crouch release, or air, opening the slide-stop accuracy window.</summary>
	private void UpdateSlide(ref Vector3 velocity, MovementInput input, float dt)
	{
		if (!Sv.SlideEnabled) { IsSliding = false; return; }

		if (!IsSliding && input.CrouchPressed && !DidJumpThisFrame && ActuallySprinting && input.OnFloor)
		{
			float speed = new Vector3(velocity.X, 0f, velocity.Z).Length();
			if (speed >= Sv.SlideStartSpeedMin)
			{
				IsSliding = true;
				SlideTimer = 0f;
				Vector3 horiz = new Vector3(velocity.X, 0f, velocity.Z);
				if (horiz.LengthSquared() > 0.0001f)
				{
					Vector3 boost = horiz.Normalized() * Sv.SlideBoostSpeed;
					velocity.X = boost.X;
					velocity.Z = boost.Z;
				}
			}
		}

		if (IsSliding)
		{
			SlideTimer += dt;
			Vector3 horiz = new Vector3(velocity.X, 0f, velocity.Z);
			horiz = horiz.MoveToward(Vector3.Zero, Sv.SlideFriction * dt);
			velocity.X = horiz.X;
			velocity.Z = horiz.Z;
			float curSpeed = horiz.Length();
			if (curSpeed < Sv.SlideMinSpeed || SlideTimer >= Sv.SlideMaxTime
				|| !input.CrouchHeld || !input.OnFloor)
			{
				IsSliding = false;
				if (Sv.SlideStopAccuracyEnabled)
				{
					SlideStopAccuracyTimer = Sv.SlideStopAccuracyWindow;
					if (Sv.SlideStopHardBrake && input.OnFloor)
					{
						velocity.X = 0f;
						velocity.Z = 0f;
					}
				}
			}
		}
	}

	/// <summary>Breath-hold state machine: hold → recover → cooldown → idle/regen. Initial values from ResetSpawnConsumables.</summary>
	private void UpdateBreathHold(MovementInput input, float dt)
	{
		BreathHoldActiveNow = false;
		if (!Sv.BreathHoldEnabled) return;

		BreathCooldownTimer = Mathf.Max(0f, BreathCooldownTimer - dt);

		if (BreathRecoverTimer > 0f)
		{
			BreathRecoverTimer -= dt;
			if (BreathRecoverTimer <= 0f)
			{
				BreathRecoverTimer = 0f;
				BreathCooldownTimer = Sv.BreathHoldCooldownAfterRecover;
				BreathHoldTimer = Sv.BreathHoldDuration;
			}
			return;
		}

		bool inAds = AdsBlend > 0.5f;
		if (input.BreathHoldHeld && inAds && BreathHoldTimer > 0f && BreathCooldownTimer <= 0f)
		{
			BreathHoldActiveNow = true;
			BreathHoldTimer -= dt;
			if (BreathHoldTimer <= 0f)
			{
				BreathHoldTimer = 0f;
				BreathRecoverTimer = Sv.BreathHoldRecoverDuration;
			}
		}
		else
		{
			BreathHoldTimer = Mathf.Min(Sv.BreathHoldDuration, BreathHoldTimer + dt * Sv.BreathHoldDuration * 0.5f);
		}
	}

	/// <summary>Tracks airborne state from OnFloor and lerps ADS blend toward target at the per-weapon rate.</summary>
	private void UpdateAdsBlend(MovementInput input, Vector3 velocity, float dt)
	{
		if (DidJumpThisFrame) _isAirborne = true;
		else _isAirborne = !input.OnFloor;
		_prevVelocityY = velocity.Y;

		bool adsAllowed = input.AdsHeld && !ActuallySprinting && !IsSliding && !IsReloading && !IsInspecting && WeaponRaiseBlend >= 0.95f;
		float target = adsAllowed ? 1f : 0f;
		float blendTime = input.Weapon?.AdsBlendTime ?? 0.18f;
		float rate = 1f / Mathf.Max(0.01f, blendTime);
		AdsBlend = Mathf.MoveToward(AdsBlend, target, rate * dt);
	}

	/// <summary>Sprint/stamina state: drain, regen, exhaustion, auto-resume.</summary>
	private void UpdateStamina(MovementInput input)
	{
		bool hasInput = input.WishDir.LengthSquared() > 0.01f;
		bool sprintInput = input.SprintHeld && hasInput && !input.CrouchHeld && !input.AdsHeld;

		bool justExhausted = !SprintExhausted && Stamina <= 0f;
		if (Stamina <= 0f)
		{
			SprintExhausted = true;
			SprintNeedsRelease = true;
		}
		if (justExhausted) StaminaRegenTimer = Sv.StaminaExhaustTimeout;
		if (SprintExhausted && Stamina >= Sv.StaminaSprintThreshold)
		{
			SprintExhausted = false;
			SprintNeedsRelease = false;
		}
		if (!input.SprintHeld) SprintNeedsRelease = false;

		ActuallySprinting = sprintInput && !SprintExhausted && !SprintNeedsRelease && Stamina > 0f;

		float dt = input.Dt;
		if (ActuallySprinting)
		{
			Stamina = Mathf.Max(0f, Stamina - Sv.StaminaDrainRate * dt);
			StaminaRegenTimer = Sv.StaminaRegenDelay;
		}
		else
		{
			StaminaRegenTimer = Mathf.Max(0f, StaminaRegenTimer - dt);
			if (StaminaRegenTimer <= 0f)
				Stamina = Mathf.Min(Sv.MaxStamina, Stamina + Sv.StaminaRegenRate * dt);
		}
	}

	/// <summary>Target horizontal speed: sprint/shift/walk base times the crouch and ADS multipliers.</summary>
	private float ComputeTargetSpeed(MovementInput input)
	{
		float baseSpeed;
		if (ActuallySprinting)
		{
			baseSpeed = Sv.SprintSpeed;
		}
		else if (input.ShiftHeld) baseSpeed = Sv.ShiftSpeed;
		else baseSpeed = Sv.WalkSpeed;

		float speed = Mathf.Lerp(baseSpeed, Sv.CrouchSpeed, CrouchBlend);
		if (input.Weapon != null)
		{
			speed *= ActuallySprinting ? input.Weapon.SprintSpeedMul : input.Weapon.MoveSpeedMul;
			if (AdsBlend > 0f)
			{
				float adsMul = Mathf.Lerp(1f, input.Weapon.AdsSpeedMul, AdsBlend);
				speed *= adsMul;
			}
		}
		return speed;
	}

	/// <summary>Horizontal movement: ground friction (stopspeed floor) then wish-dir accel up to
	/// wishspeed; counter-strafe falls out of the addspeed formula. Air branch is strafe accel.</summary>
	private void ApplyHorizontalMovement(ref Vector3 velocity, MovementInput input, float targetSpeed, float dt)
	{
		if (IsSliding) return;

		Vector3 inputDir = input.WishDir;
		bool hasInput = inputDir.LengthSquared() > 0.01f;
		if (hasInput) inputDir = inputDir.Normalized();

		Vector3 worldDir = input.BodyBasis * inputDir;
		Vector3 horizVel = new Vector3(velocity.X, 0f, velocity.Z);

		if (input.OnFloor)
		{
			float speed = horizVel.Length();
			if (speed > 0.0001f)
			{
				float control = Mathf.Max(speed, Sv.StopSpeed);
				float drop = control * Sv.GroundFriction * dt;
				float newSpeed = Mathf.Max(0f, speed - drop);
				horizVel *= newSpeed / speed;
			}
			if (horizVel.LengthSquared() < 0.0001f) horizVel = Vector3.Zero;

			if (hasInput)
			{
				float wishSpeed = targetSpeed;
				float currentInWishDir = horizVel.Dot(worldDir);
				float addSpeed = wishSpeed - currentInWishDir;
				if (addSpeed > 0f)
				{
					float accelSpeed = Sv.GroundAcceleration * wishSpeed * dt;
					if (accelSpeed > addSpeed) accelSpeed = addSpeed;
					horizVel += worldDir * accelSpeed;
				}
			}
		}
		else if (hasInput)
		{
			float wishSpeed = Mathf.Min(targetSpeed, Sv.AirMaxWishSpeed);
			float currentInWishDir = horizVel.Dot(worldDir);
			float addSpeed = wishSpeed - currentInWishDir;
			if (addSpeed > 0f)
			{
				float accelSpeed = Sv.AirAcceleration * wishSpeed * dt;
				if (accelSpeed > addSpeed) accelSpeed = addSpeed;
				horizVel += worldDir * accelSpeed;
			}
		}

		velocity.X = horizVel.X;
		velocity.Z = horizVel.Z;
	}
}
