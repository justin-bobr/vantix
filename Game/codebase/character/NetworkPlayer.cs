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

[Tool, GlobalClass]
public partial class NetworkPlayer : CharacterBody3D
{
	private bool _tpsWasReloading, _tpsWasInspecting;
	private static readonly StringName _pTpsActionRequest = "parameters/Action/request";

	private GeometryInstance3D.ShadowCastingSetting? _tpsShadowModeApplied;

	[ExportGroup("Grip")]
	protected float HipFov => ConVars.Cl.Fov;
	protected const float AdsFovDesignBase = 100f;
	protected float AimFov => HipFov * ((_currentWeapon?.AimFov ?? 78f) / AdsFovDesignBase);
	protected float TpsAimFov => _currentWeapon?.TpsAimFov ?? 50f;
	protected Vector3 AdsOffsetPosition => _currentWeapon?.AdsOffsetPosition ?? new Vector3(-0.02f, 0.06f, 0.0205f);
	protected Vector3 AdsOffsetRotation => _currentWeapon?.AdsOffsetRotation ?? new Vector3(0f, -8.4f, 0f);
	protected float AimBlendSpeed => _currentWeapon?.AimBlendSpeed ?? 12f;
	protected Vector3 CrouchOffsetPosition => _currentWeapon?.CrouchOffsetPosition ?? new Vector3(0.015f, 0.02f, -0.015f);
	protected Vector3 CrouchOffsetRotation => _currentWeapon?.CrouchOffsetRotation ?? new Vector3(0f, 4.3f, 0f);
	protected Vector3 CantedOffsetPosition => _currentWeapon?.CantedOffsetPosition ?? new Vector3(-0.05f, -0.015f, -0.01f);
	protected Vector3 CantedOffsetRotation => _currentWeapon?.CantedOffsetRotation ?? new Vector3(0f, 35.0f, 0f);
	protected bool AdsTestMode => _currentWeapon?.AdsTestMode ?? false;
	protected bool CrouchTestMode => _currentWeapon?.CrouchTestMode ?? false;
	protected bool CantedTestMode => _currentWeapon?.CantedTestMode ?? false;
	protected float AdsCalibrationDistance => _currentWeapon?.AdsCalibrationDistance ?? 1.0f;
	protected float AdsCalibrationSize => _currentWeapon?.AdsCalibrationSize ?? 0.004f;
	protected Color AdsCalibrationColor => _currentWeapon?.AdsCalibrationColor ?? new Color(1f, 0f, 0f, 1f);
	protected Vector3 RecoilImpulseHipfire => _currentWeapon?.RecoilImpulseHipfire ?? new Vector3(-1.2f, 0.4f, 0f);
	protected Vector3 RecoilImpulseAimed => _currentWeapon?.RecoilImpulseAimed ?? new Vector3(-0.6f, 0.2f, 0f);
	protected float RecoilStiffness => _currentWeapon?.RecoilStiffness ?? 200f;
	protected float RecoilDamping => _currentWeapon?.RecoilDamping ?? 0.6f;
	protected float RecoilMass => _currentWeapon?.RecoilMass ?? 1f;
	protected float RecoilMaxDegrees => _currentWeapon?.RecoilMaxDegrees ?? 10f;
	protected float AimRecoilMultiplier => _currentWeapon?.AimRecoilMultiplier ?? 0.5f;
	protected float WeaponRecoilRotScale => _currentWeapon?.WeaponRecoilRotScale ?? 0.35f;
	protected float WeaponRecoilKickback => _currentWeapon?.WeaponRecoilKickback ?? 0.012f;

	protected AnimationPlayer _player;
	protected bool _isAiming;
	protected bool _isCrouched;
	protected float _lookYaw;
	protected float _lookPitch;
	protected float _aimBlend;
	protected bool _cantedAim;
	protected Vector3 _recoilCurrent;
	protected Camera3D _cam;
	protected Camera3D _viewmodelCam;
	protected Camera3D _tpsCam;
	protected Node3D _tpsPivot;
	protected float _tpsZoomDist = -1f;
	protected PhysicsRayQueryParameters3D _tpsRayQuery;
	protected readonly PhysicsRayQueryResult3D _tpsRayResult = new();
	protected Godot.Collections.Array<Rid> _tpsSelfExclude;
	protected Node3D _viewmodelCamAnchor;
	protected CanvasLayer _viewmodelLayer;
	protected AnimationPlayer _tpsPlayer;
	protected AnimationTree _tpsTree;
	protected AnimationNodeAnimation _tpsActionAnim;
	protected AnimationNodeAnimation _tpsAimPoseNode;
	protected TpsAimModifier _tpsAimModifier;

	protected BotController _botController;
	protected WeaponAnimation _currentWeapon;
	protected WeaponAnimation _fpsWeapon;
	protected WeaponAnimation _tpsWeapon;
	protected float _magFill = 1f;
	protected string _fireModeName = "Semi";
	protected FootstepAudio _footstepAudio;
	protected AnimationTree _tree;
	protected AnimationNodeAnimation _actionAnim;
	protected AnimationNodeAnimation _actionRefNode;
	protected AnimationNodeAnimation _actionRef2Node;
	protected AnimationNodeAnimation _tpsActionRefNode;
	protected AnimationNodeAnimation _tpsActionRef2Node;
	protected AnimationNodeAnimation _gripPose;
	protected AnimationNodeAnimation _gripPoseAim;
	protected GripType _grip = GripType.Standard;
	protected Vector2 _simVel;
	protected float _runAmt, _sprintAmt;
	protected float _smoothedHorizSpeed;
	protected Node3D _leftHandFabrik;
	protected Node3D _rightHandFabrik;
	protected WeaponBoneModifier _weaponBoneModifier;
	protected Node3D _bodyNode;
	protected Vector3 _bodyRest;
	protected bool _bodyRestCaptured;

	protected AnimationNodeAnimation _locoStopAnim;
	protected string _animEnumHint;
	protected string _tpsAnimEnumHint;

	protected bool TpsView => CurrentGameMode != PresentationMode.Server && ViewMode == ViewMode.Tps && _tpsCam != null;

	[ExportGroup("Mode")]
	[Export]
	public PresentationMode CurrentGameMode = PresentationMode.Local;

	public bool IsLocalPlayer => CurrentGameMode == PresentationMode.Local;
	public bool IsServerAgent => CurrentGameMode == PresentationMode.Server;

	[ExportGroup("FPS")]
	[ExportSubgroup("Camera")]
	[Export]
	public NodePath HeadCameraPath;
	[Export]
	public NodePath ViewmodelCameraPath;
	[Export]
	public NodePath ViewmodelLayerPath;
	[Export]
	public NodePath ViewmodelCameraAnchorPath;
	[Export]
	public NodePath ViewmodelMeshRootPath;
	[Export(PropertyHint.Layers3DRender)]
	public uint ViewmodelRenderLayer = 2;
	[Export]
	public Vector3 ViewmodelOffsetPosition = Vector3.Zero;
	[Export]
	public Vector3 ViewmodelOffsetRotation = Vector3.Zero;
	[Export]
	public bool MouseLookEnabled = true;

	[ExportSubgroup("Head")]
	[Export]
	public Node3D HeadPitch;

	[ExportSubgroup("Body")]
	[Export(PropertyHint.Range, "0,1,0.01")]
	public float CrouchCameraDrop = 0.32f;

	[ExportGroup("TPS")]
	[ExportSubgroup("Camera")]
	[Export]
	public NodePath TpsCameraPath;
	[Export]
	public NodePath TpsPivotPath;
	[Export(PropertyHint.Range, "-45,45,0.5")]
	public float TpsBasePitchDeg = -8f;
	[Export]
	public Vector3 TpsCameraOffset = new(0.4f, 0.0f, 1.5f);
	[Export(PropertyHint.Range, "0,0.5,0.01")]
	public float TpsCamWallMargin = 0.2f;
	[Export(PropertyHint.Layers3DPhysics)]
	public uint TpsCamCollisionMask = 1;
	[Export(PropertyHint.Range, "1,40,0.5")]
	public float TpsCamSmoothRate = 14f;
	[Export(PropertyHint.Range, "0.3,3,0.1")]
	public float TpsZoomMin = 0.8f;
	[Export(PropertyHint.Range, "1,8,0.1")]
	public float TpsZoomMax = 4.0f;
	[Export(PropertyHint.Range, "0.05,1,0.05")]
	public float TpsZoomStep = 0.3f;

	[ExportSubgroup("Body")]
	[Export(PropertyHint.Range, "-180,180,1")]
	public float TpsBodyYawOffsetDeg = 0f;
	[Export(PropertyHint.Range, "1,30,0.5")]
	public float TpsBodyTurnRate = 10f;
	[Export(PropertyHint.Range, "-2,2,0.05")]
	public float TpsRecoilPitchScale = 0.5f;

	[ExportSubgroup("Visual")]
	[Export]
	public Node3D TpsVisual;
	[Export(PropertyHint.Layers3DRender)]
	public uint TpsRenderLayer = 1u << 5;

	[ExportSubgroup("Aim Posing")]
	[Export]
	public string TpsAimBoneName = "spine_03";
	[Export(PropertyHint.Range, "0,1,0.05")]
	public float TpsAimPitchScale = 0.6f;

	[ExportGroup("Animations")]
	[ExportSubgroup("FPS")]
	[Export]
	public NodePath CharacterAnimationPath;
	[Export]
	public NodePath FpsTreePath = new("AnimationTree");
	[Export]
	public bool UseAnimationTree = true;
	[Export(PropertyHint.Range, "1,40,0.5")]
	public float LocomotionSmoothing = 22f;
	[Export(PropertyHint.Range, "1,15,0.5")]
	public float SpeedBlendRate = 5f;
	[Export]
	public bool RebuildAnimationTree { get => false; set { if (value) EditorRebuildTree(); } }

	[ExportSubgroup("FPS/Locomotion")]
	[Export]
	public string IdleStanding = "locomotion/A_TFA_FP_AR_Idle_Loop_Standing";
	[Export]
	public string IdleCrouched = "locomotion/A_TFA_FP_AR_Idle_Loop_Crouched";
	[Export]
	public string IdleAimed = "locomotion/A_TFA_FP_AR_Idle_Loop_Aimed";
	[Export]
	public string WalkForward = "locomotion/A_TFA_FP_AR_Walk_F_Loop_Standing";
	[Export]
	public string WalkBackward = "locomotion/A_TFA_FP_AR_Walk_B_Loop_Standing";
	[Export]
	public string WalkStrafeLeft = "locomotion/A_TFA_FP_AR_Walk_Strafe_L_Loop_Standing";
	[Export]
	public string WalkStrafeRight = "locomotion/A_TFA_FP_AR_Walk_Strafe_R_Loop_Standing";
	[Export]
	public string WalkForwardAimed = "locomotion/A_TFA_FP_AR_Walk_F_Loop_Aimed";
	[Export]
	public string WalkBackwardAimed = "locomotion/A_TFA_FP_AR_Walk_B_Loop_Aimed";
	[Export]
	public string WalkStrafeLeftAimed = "locomotion/A_TFA_FP_AR_Walk_Strafe_L_Loop_Aimed";
	[Export]
	public string WalkStrafeRightAimed = "locomotion/A_TFA_FP_AR_Walk_Strafe_R_Loop_Aimed";
	[Export]
	public string RunForward = "locomotion/A_TFA_FP_AR_Run_F_Loop";
	[Export]
	public string SprintForward = "locomotion/A_TFA_FP_AR_Sprint_F_Loop";
	[Export]
	public string JumpStart = "locomotion/A_TFA_FP_AR_Jump_Start";
	[Export]
	public string JumpFallingLoop = "locomotion/A_TFA_FP_AR_Jump_Falling_Loop";
	[Export]
	public string JumpEnd = "locomotion/A_TFA_FP_AR_Jump_End";
	[Export]
	public string JumpFull = "locomotion/A_TFA_FP_AR_Jump_Full";

	[ExportSubgroup("FPS/Combat")]
	[Export]
	public string FireSemi = "combat/A_TFA_FP_AR_Fire_Semi";
	[Export]
	public string FireAuto = "combat/A_TFA_FP_AR_Fire_Auto";
	[Export]
	public string FireAimed = "combat/A_TFA_FP_AR_Fire_Aimed";
	[Export]
	public string FireEmpty = "combat/A_TFA_FP_AR_Fire_Empty";
	[Export]
	public string Reload = "combat/A_TFA_FP_AR_Reload";
	[Export]
	public string ReloadEmpty = "combat/A_TFA_FP_AR_Reload_Empty";
	[Export]
	public string ReloadAimed = "combat/A_TFA_FP_AR_Reload_Aimed";
	[Export]
	public string ReloadEmptyAimed = "combat/A_TFA_FP_AR_Reload_Empty_Aimed";
	[Export]
	public string ReloadQuick = "combat/A_TFA_FP_AR_Reload_Quick";
	[Export]
	public string ReloadQuickAimed = "combat/A_TFA_FP_AR_Reload_Quick_Aimed";
	[Export]
	public string MagCheck = "combat/A_TFA_FP_AR_MagCheck";
	[Export]
	public string MagCheckAimed = "combat/A_TFA_FP_AR_MagCheck_Aimed";
	[Export]
	public string FireModeSwitch = "combat/A_TFA_FP_AR_FireModeSwitch";
	[Export]
	public string MeleeBashForward = "combat/A_TFA_FP_AR_Melee_Bash_F";
	[Export]
	public string MeleeSwingLeft = "combat/A_TFA_FP_AR_Melee_Swing_L";
	[Export]
	public string MeleeSwingRight = "combat/A_TFA_FP_AR_Melee_Swing_R";
	[Export]
	public string ClearJamMagSwipe = "combat/A_TFA_FP_AR_ClearJam_MagSwipe";
	[Export]
	public string ClearJamRack = "combat/A_TFA_FP_AR_ClearJam_Rack";
	[Export]
	public string GrenadeThrowQuick = "combat/A_TFA_FP_AR_Grenade_Throw_Quick";

	[ExportSubgroup("FPS/Interactions")]
	[Export]
	public string Inspect = "interactions/A_TFA_FP_AR_Inspect";
	[Export]
	public string InspectEmpty = "interactions/A_TFA_FP_AR_Inspect_Empty";
	[Export]
	public string HealSyringe = "interactions/A_TFA_FP_AR_Heal_Syringe";
	[Export]
	public string GripChange = "interactions/A_TFA_FP_AR_GripChange";
	[Export]
	public string InteractGrab = "interactions/A_TFA_FP_AR_Interact_Grab";
	[Export]
	public string InteractPush = "interactions/A_TFA_FP_AR_Interact_Push";
	[Export]
	public string InteractPunch = "interactions/A_TFA_FP_AR_Interact_Punch";

	[ExportSubgroup("FPS/Transitions")]
	[Export]
	public string Equip = "transitions/A_TFA_FP_AR_Equip";
	[Export]
	public string EquipQuick = "transitions/A_TFA_FP_AR_Equip_Quick";
	[Export]
	public string Holster = "transitions/A_TFA_FP_AR_Holster";
	[Export]
	public string TransitionCrouchStart = "transitions/A_TFA_FP_AR_Transition_Crouch_Start";
	[Export]
	public string TransitionCrouchEnd = "transitions/A_TFA_FP_AR_Transition_Crouch_End";
	[Export]
	public string TransitionCrouchStartAimed = "transitions/A_TFA_FP_AR_Transition_Crouch_Start_Aimed";
	[Export]
	public string TransitionCrouchEndAimed = "transitions/A_TFA_FP_AR_Transition_Crouch_End_Aimed";
	[Export]
	public string WalkEnd = "transitions/A_TFA_FP_AR_Transition_Walk_End";
	[Export]
	public string RunEnd = "transitions/A_TFA_FP_AR_Transition_Run_End";
	[Export]
	public string TriggerDisciplineReady = "transitions/A_TFA_FP_AR_TriggerDiscipline_Ready";

	[ExportSubgroup("FPS/Poses")]
	[Export]
	public string IdlePoseStanding = "poses/A_TFA_FP_AR_Idle_Pose_Standing";
	[Export]
	public string AimPose = "poses/A_TFA_FP_AR_Aim_Pose";
	[Export]
	public string AimPoseGripAngled = "poses/A_TFA_FP_AR_Aim_Pose_Grip_Angled";
	[Export]
	public string ActionRefAim = "poses/A_TFA_FP_AR_Aim_Pose";
	[Export]
	public string ActionRefIdle = "poses/A_TFA_FP_AR_Idle_Pose_Standing";
	[Export]
	public string AimPoseGripVertical = "poses/A_TFA_FP_AR_Aim_Pose_Grip_Vertical";
	[Export]
	public string IdlePoseGripAngled = "poses/A_TFA_FP_AR_Idle_Pose_Grip_Angled";
	[Export]
	public string IdlePoseGripVertical = "poses/A_TFA_FP_AR_Idle_Pose_Grip_Vertical";
	[Export]
	public string TriggerDisciplineSafePose = "poses/A_TFA_FP_AR_TriggerDiscipline_Safe_Pose";

	[ExportSubgroup("TPS")]
	[Export]
	public NodePath TpsAnimationPath;
	[Export]
	public AnimationTree TpsAnimTree;

	[ExportSubgroup("TPS/Locomotion")]
	[Export]
	public string TpsIdleLoop = "locomotion/A_TFA_TP_AR_Idle_Loop";

	[ExportSubgroup("TPS/Combat")]
	[Export]
	public string TpsFire = "combat/A_TFA_TP_AR_Fire";
	[Export]
	public string TpsFireEmpty = "combat/A_TFA_TP_AR_Fire_Empty";
	[Export]
	public string TpsReload = "combat/A_TFA_TP_AR_Reload";
	[Export]
	public string TpsReloadEmpty = "combat/A_TFA_TP_AR_Reload_Empty";
	[Export]
	public string TpsReloadAimed = "combat/A_TFA_TP_AR_Reload_Aimed";
	[Export]
	public string TpsReloadEmptyAimed = "combat/A_TFA_TP_AR_Reload_Empty_Aimed";
	[Export]
	public string TpsReloadQuick = "combat/A_TFA_TP_AR_Reload_Quick";
	[Export]
	public string TpsReloadQuickAimed = "combat/A_TFA_TP_AR_Reload_Quick_Aimed";
	[Export]
	public string TpsMagCheck = "combat/A_TFA_TP_AR_MagCheck";
	[Export]
	public string TpsMagCheckAimed = "combat/A_TFA_TP_AR_MagCheck_Aimed";
	[Export]
	public string TpsFireModeSwitch = "combat/A_TFA_TP_AR_FireModeSwitch";
	[Export]
	public string TpsMeleeBashForward = "combat/A_TFA_TP_AR_Melee_Bash_F";
	[Export]
	public string TpsMeleeSwingLeft = "combat/A_TFA_TP_AR_Melee_Swing_L";
	[Export]
	public string TpsMeleeSwingRight = "combat/A_TFA_TP_AR_Melee_Swing_R";
	[Export]
	public string TpsClearJamMagSwipe = "combat/A_TFA_TP_AR_ClearJam_MagSwipe";
	[Export]
	public string TpsClearJamRack = "combat/A_TFA_TP_AR_ClearJam_Rack";
	[Export]
	public string TpsGrenadeThrowQuick = "combat/A_TFA_TP_AR_Grenade_Throw_Quick";

	[ExportSubgroup("TPS/Interactions")]
	[Export]
	public string TpsInspect = "interactions/A_TFA_TP_AR_Inspect";
	[Export]
	public string TpsInspectEmpty = "interactions/A_TFA_TP_AR_Inspect_Empty";
	[Export]
	public string TpsHealSyringe = "interactions/A_TFA_TP_AR_Heal_Syringe";
	[Export]
	public string TpsInteractGrab = "interactions/A_TFA_TP_AR_Interact_Grab";
	[Export]
	public string TpsInteractPush = "interactions/A_TFA_TP_AR_Interact_Push";
	[Export]
	public string TpsInteractPunch = "interactions/A_TFA_TP_AR_Interact_Punch";

	[ExportSubgroup("TPS/Transitions")]
	[Export]
	public string TpsEquip = "transitions/A_TFA_TP_AR_Equip";
	[Export]
	public string TpsEquipQuick = "transitions/A_TFA_TP_AR_Equip_Quick";
	[Export]
	public string TpsHolster = "transitions/A_TFA_TP_AR_Holster";
	[Export]
	public string TpsTransitionAimStart = "transitions/A_TFA_TP_AR_Transition_Aim_Start";
	[Export]
	public string TpsTransitionAimEnd = "transitions/A_TFA_TP_AR_Transition_Aim_End";

	[ExportSubgroup("TPS/Poses")]
	[Export]
	public string TpsAimPose = "poses/A_TFA_TP_AR_Aim_Pose";
	[Export]
	public string TpsAimPoseCanted = "poses/A_TFA_TP_AR_Aim_Pose_Canted";
	[Export]
	public string TpsAimPoseGripAngled = "poses/A_TFA_TP_AR_Aim_Pose_Grip_Angled";
	[Export]
	public string TpsAimPoseGripVertical = "poses/A_TFA_TP_AR_Aim_Pose_Grip_Vertical";
	[Export]
	public string TpsIdlePose = "poses/A_TFA_TP_AR_Idle_Pose";
	[Export]
	public string TpsIdlePoseGripAngled = "poses/A_TFA_TP_AR_Idle_Pose_Grip_Angled";
	[Export]
	public string TpsIdlePoseGripVertical = "poses/A_TFA_TP_AR_Idle_Pose_Grip_Vertical";

	[Export(PropertyHint.Range, "1,60,0.5")]
	public float GripPoseBlendSpeed = 15f;
	[Export(PropertyHint.Range, "0.05,0.5,0.005")]
	public float GripAimBlendTime = 0.18f;
	[Export(PropertyHint.Range, "0.05,0.5,0.005")]
	public float GripChangeNotifyTime = 0.133f;

	[ExportGroup("Sway")]
	[Export(PropertyHint.Range, "0,1,0.001")]
	public float SwayLookFactor = 0.04f;
	[Export(PropertyHint.Range, "0,20,0.1")]
	public float SwayMaxDegrees = 6f;
	[Export(PropertyHint.Range, "1,40,0.5")]
	public float SwaySpringSpeed = 12f;
	[Export(PropertyHint.Range, "0,1,0.05")]
	public float AimSwayMultiplier = 0.3f;

	[ExportGroup("View Sway")]
	[Export]
	public bool ViewSwayEnabled = true;
	[Export(PropertyHint.Range, "1,40,0.5")]
	public float LeanReferenceSpeed = 5.0f;
	[Export(PropertyHint.Range, "0,1,0.05")]
	public float ViewSwayAdsMul = 0.15f;
	[Export(PropertyHint.Range, "0,1,0.05")]
	public float ViewSwayAdsLookMul = 0.5f;
	[Export(PropertyHint.Range, "0,1,0.05")]
	public float ViewSwayWorldMul = 0.35f;

	[ExportSubgroup("Direction Lean")]
	[Export]
	public bool DirectionLeanEnabled = true;
	[Export(PropertyHint.Range, "10,400,1")]
	public float DirectionLeanStiffness = 90f;
	[Export(PropertyHint.Range, "1,60,0.5")]
	public float DirectionLeanDamping = 14f;
	[Export(PropertyHint.Range, "0,0.1,0.0005")]
	public float StrafeLeanPos = 0.011f;
	[Export(PropertyHint.Range, "0,10,0.1")]
	public float StrafeLeanRoll = 1.4f;
	[Export(PropertyHint.Range, "0,5,0.05")]
	public float ForwardLeanPitch = 0.35f;
	[Export(PropertyHint.Range, "0,0.05,0.0005")]
	public float ForwardLeanPosDown = 0.004f;
	[Export(PropertyHint.Range, "0,0.05,0.0005")]
	public float ForwardLeanPosForward = 0.009f;

	[ExportSubgroup("Velocity Tilt")]
	[Export]
	public bool VelocityTiltEnabled = true;
	[Export(PropertyHint.Range, "0,1,0.005")]
	public float InertiaTiltStrength = 0.05f;
	[Export(PropertyHint.Range, "0,10,0.1")]
	public float InertiaTiltMax = 1.2f;
	[Export(PropertyHint.Range, "0,20,0.1")]
	public float InertiaTiltRecovery = 7.0f;

	[ExportSubgroup("Body Yaw Lag")]
	[Export]
	public bool BodyYawLagEnabled = true;
	[Export(PropertyHint.Range, "0,0.1,0.0005")]
	public float BodyYawLagStrength = 0.01f;
	[Export(PropertyHint.Range, "0,30,0.5")]
	public float BodyYawLagMax = 2.5f;
	[Export(PropertyHint.Range, "1,40,0.5")]
	public float BodyYawLagSmoothing = 10.0f;

	[ExportSubgroup("Mouse Inertia")]
	[Export]
	public bool MouseInertiaEnabled = true;
	[Export(PropertyHint.Range, "0,0.1,0.0005")]
	public float MouseInertiaYaw = 0.01f;
	[Export(PropertyHint.Range, "0,0.1,0.0005")]
	public float MouseInertiaPitch = 0.012f;
	[Export(PropertyHint.Range, "0,5,0.05")]
	public float MouseInertiaMaxYaw = 0.7f;
	[Export(PropertyHint.Range, "0,5,0.05")]
	public float MouseInertiaMaxPitch = 0.7f;
	[Export(PropertyHint.Range, "0,20,0.1")]
	public float MouseInertiaRecovery = 7.0f;
	[Export(PropertyHint.Range, "1,40,0.5")]
	public float MouseInertiaSmoothingIn = 10.0f;
	[Export(PropertyHint.Range, "1,40,0.5")]
	public float MouseInertiaSmoothingOut = 7.0f;
	[Export(PropertyHint.Range, "0,2,0.05")]
	public float MouseInertiaRollMul = 0.18f;

	[ExportGroup("IK")]
	[ExportSubgroup("Left Hand (Foregrip)")]
	[Export]
	public bool IkEnabled = false;
	[Export]
	public NodePath LeftHandFabrikPath;

	[ExportSubgroup("Right Hand (Pistol Grip)")]
	[Export]
	public NodePath RightHandFabrikPath;

	[ExportGroup("Weapon")]
	[Export]
	public NodePath CurrentWeaponPath;
	[Export]
	public NodePath TpsWeaponPath;
	/// <summary>WeaponBoneModifier applying the ADS/crouch/canted/recoil offset to ik_hand_gun. Resolved by
	/// node path, not a static singleton (which broke the editor ADS preview).</summary>
	[Export]
	public NodePath WeaponBoneModifierPath;

	[ExportSubgroup("Firing")]
	[Export(PropertyHint.Range, "2,40,0.5")]
	public float GrenadeThrowSpeed = 14f;

	[ExportGroup("Footsteps")]
	[Export]
	public NodePath FootstepAudioPath;

	[ExportGroup("State")]
	[Export]
	public ViewMode ViewMode = ViewMode.Fps;
	[ExportSubgroup("Movement")]
	[Export]
	public bool IsRunning { get => _runAmt > 0.5f; set => _runAmt = value ? 1f : 0f; }
	[Export]
	public bool IsSprinting { get => _sprintAmt > 0.5f; set { _sprintAmt = value ? 1f : 0f; if (value) _runAmt = 1f; } }
	[Export]
	public bool IsCrouching { get => _isCrouched; set => _isCrouched = value; }
	[Export]
	public Vector2 SimulatedVelocity { get => _simVel; set => _simVel = value; }
	[ExportSubgroup("Actions")]
	[Export]
	public bool IsAiming { get => _isAiming; set => _isAiming = value; }
	[Export]
	public bool IsCantedAiming { get => _cantedAim; set => _cantedAim = value; }
	[Export]
	public GripType CurrentGrip { get => _grip; set => _grip = value; }

	/// <summary>Weapon trigger mode (semi-auto / full-auto).</summary>
	public enum FireMode { Semi, Auto }

	/// <summary>Foregrip style affecting the hand pose.</summary>
	public enum GripType { Standard, Angled, Vertical }

	public BotController BotController => _botController ??= new();

	public Camera3D ActiveCamera =>
		(ViewMode == ViewMode.Tps && _tpsCam != null) ? _tpsCam : _cam;

	private void FireTpsAction(string anim)
	{
		if (string.IsNullOrEmpty(anim) || !_tpsPlayer.HasAnimation(anim))
			return;
		_tpsActionAnim.Animation = anim;
		_tpsTree.Set(_pTpsActionRequest, (int)AnimationNodeOneShot.OneShotRequest.Fire);
	}

	protected void ResolveWeaponPlayers()
	{
		bool server = CurrentGameMode == PresentationMode.Server;
		_fpsWeapon = server ? null : GetNodeOrNull<WeaponAnimation>(CurrentWeaponPath);
		_tpsWeapon = server ? null : GetNodeOrNull<WeaponAnimation>(TpsWeaponPath);
		if (_fpsWeapon != null) { _fpsWeapon.Mode = WeaponMode.FPS; _fpsWeapon.OwnerBody = this; }
		if (_tpsWeapon != null) { _tpsWeapon.Mode = WeaponMode.TPS; _tpsWeapon.OwnerBody = this; }
		_leftHandFabrik = GetNodeOrNull<Node3D>(LeftHandFabrikPath);
		_rightHandFabrik = GetNodeOrNull<Node3D>(RightHandFabrikPath);
		_weaponBoneModifier = GetNodeOrNull<WeaponBoneModifier>(WeaponBoneModifierPath);
		_bodyNode = HeadPitch;
		if (_bodyNode != null && !_bodyRestCaptured)
		{ _bodyRest = _bodyNode.Position; _bodyRestCaptured = true; }
		_cam = GetNodeOrNull<Camera3D>(HeadCameraPath);
		_viewmodelCam = GetNodeOrNull<Camera3D>(ViewmodelCameraPath);
		_viewmodelCamAnchor = GetNodeOrNull<Node3D>(ViewmodelCameraAnchorPath);
		if (_fpsWeapon != null) { _fpsWeapon.RemapFromCamera = _viewmodelCam; _fpsWeapon.RemapToCamera = _cam; }
		_viewmodelLayer = GetNodeOrNull<CanvasLayer>(ViewmodelLayerPath);
		_tpsCam = GetNodeOrNull<Camera3D>(TpsCameraPath);
		_tpsPivot = GetNodeOrNull<Node3D>(TpsPivotPath);
		_tpsPlayer = GetNodeOrNull<AnimationPlayer>(TpsAnimationPath);
		_footstepAudio = GetNodeOrNull<FootstepAudio>(FootstepAudioPath);
		if (_footstepAudio != null)
			_footstepAudio.IsLocalPlayer = CurrentGameMode == PresentationMode.Local;
		UpdateActiveWeapon();
	}

	protected void UpdateActiveWeapon()
	{
		WeaponAnimation target = CurrentGameMode == PresentationMode.Server ? null
			: IsPuppet ? _tpsWeapon
			: TpsView && _tpsWeapon != null ? _tpsWeapon
			: _fpsWeapon;
		if (target == _currentWeapon)
			return;
		_currentWeapon?.DeactivateWeapon();
		_currentWeapon = target;
		if (_currentWeapon == null)
			return;
		_currentWeapon.ActivateWeapon();
		_currentWeapon.Aiming = _isAiming;
		_currentWeapon.OwnerBody = this;
		_currentWeapon.SetFireMode(_fireModeName);
		_currentWeapon.SetMagazineFill(_magFill);
	}

	protected void UpdateTpsCamera(float dt)
	{
		if (_tpsPivot == null || _tpsCam == null)
			return;
		_tpsPivot.Rotation = new Vector3(
			Mathf.DegToRad(TpsBasePitchDeg) + (MouseLookEnabled ? _lookPitch : 0f),
			0f,
			0f);

		if (_tpsZoomDist < 0f)
			_tpsZoomDist = TpsCameraOffset.Z;
		Vector3 offset = new(TpsCameraOffset.X, TpsCameraOffset.Y, _tpsZoomDist);
		Vector3 pivot = _tpsPivot.GlobalPosition;
		Vector3 desiredWorld = _tpsPivot.GlobalTransform * offset;
		Vector3 targetLocal = offset;
		var space = GetWorld3D()?.DirectSpaceState;
		if (space != null)
		{
			_tpsRayQuery ??= new PhysicsRayQueryParameters3D { CollideWithAreas = false, CollideWithBodies = true };
			_tpsSelfExclude ??= new Godot.Collections.Array<Rid> { GetRid() };
			_tpsRayQuery.From = pivot;
			_tpsRayQuery.To = desiredWorld;
			_tpsRayQuery.CollisionMask = TpsCamCollisionMask;
			_tpsRayQuery.Exclude = _tpsSelfExclude;
			if (space.IntersectRayInto(_tpsRayQuery, _tpsRayResult))
			{
				Vector3 dir = desiredWorld - pivot;
				float desiredDist = dir.Length();
				if (desiredDist > 0.001f)
				{
					float hitDist = (_tpsRayResult.GetPosition() - pivot).Length();
					float safeDist = Mathf.Max(0.2f, hitDist - TpsCamWallMargin);
					targetLocal = _tpsPivot.GlobalTransform.AffineInverse() * (pivot + dir / desiredDist * safeDist);
				}
			}
		}
		float t = TpsCamSmoothRate > 0f ? 1f - Mathf.Exp(-TpsCamSmoothRate * dt) : 1f;
		_tpsCam.Position = _tpsCam.Position.Lerp(targetLocal, t);
		_tpsCam.Fov = Mathf.Lerp(_tpsCam.Fov, Mathf.Lerp(HipFov, TpsAimFov, _aimBlend), Mathf.Clamp(dt * AimBlendSpeed, 0f, 1f));
	}

	protected void UpdateTpsBody(float dt)
	{
		bool remote = CurrentGameMode == PresentationMode.Remote;
		UpdateTpsAimPose();
		if (_tpsAimModifier != null)
			_tpsAimModifier.Pitch = (remote ? _lookPitch : 0f) + Mathf.DegToRad(_recoilCurrent.X) * TpsRecoilPitchScale;
		if (!remote || TpsVisual == null)
			return;
		float targetYaw = _lookYaw + Mathf.DegToRad(TpsBodyYawOffsetDeg);
		Vector3 r = TpsVisual.Rotation;
		r.Y = Mathf.LerpAngle(r.Y, targetYaw, Mathf.Clamp(TpsBodyTurnRate * dt, 0f, 1f));
		TpsVisual.Rotation = r;
	}

	protected void UpdateTpsAimPose()
	{
		if (_tpsAimPoseNode == null || _tpsPlayer == null)
			return;
		string pose = _cantedAim ? TpsAimPoseCanted
			: _grip == GripType.Vertical ? TpsAimPoseGripVertical
			: _grip == GripType.Angled ? TpsAimPoseGripAngled
			: TpsAimPose;
		if (!string.IsNullOrEmpty(pose) && _tpsAimPoseNode.Animation != pose && _tpsPlayer.HasAnimation(pose))
			_tpsAimPoseNode.Animation = pose;
	}

	protected void UpdateTpsMontages()
	{
		if (_tpsTree == null || _tpsActionAnim == null || _tpsPlayer == null)
			return;
		bool reloading = IsPuppet ? PuppetIsReloading : (Movement?.IsReloading ?? false);
		bool inspecting = IsPuppet ? PuppetIsInspecting : (Movement?.IsInspecting ?? false);
		bool aimed = (Movement?.AdsBlend ?? 0f) > 0.5f;
		if (reloading && !_tpsWasReloading)
			FireTpsAction(aimed && !string.IsNullOrEmpty(TpsReloadAimed) ? TpsReloadAimed : TpsReload);
		_tpsWasReloading = reloading;
		if (inspecting && !_tpsWasInspecting)
			FireTpsAction(TpsInspect);
		_tpsWasInspecting = inspecting;
	}

	protected void ApplyModeVisibility()
	{
		bool server = CurrentGameMode == PresentationMode.Server;
		bool local = CurrentGameMode == PresentationMode.Local;
		bool tps = TpsView;
		bool fps = local && !tps;
		if (local)
		{
			if (GodotObject.IsInstanceValid(_cam)) _cam.Current = !tps;
			if (GodotObject.IsInstanceValid(_tpsCam)) _tpsCam.Current = tps;
			if (GodotObject.IsInstanceValid(_viewmodelCam)) _viewmodelCam.Current = true;
		}
		if (GodotObject.IsInstanceValid(_viewmodelLayer)) _viewmodelLayer.Visible = fps;
		bool tpsToCamera = !server && (!local || tps);
		bool tpsShadowOnly = fps;
		if (GodotObject.IsInstanceValid(TpsVisual))
		{
			TpsVisual.Visible = tpsToCamera || tpsShadowOnly;
			var shadowMode = tpsShadowOnly && !tpsToCamera
				? GeometryInstance3D.ShadowCastingSetting.ShadowsOnly
				: GeometryInstance3D.ShadowCastingSetting.On;
			if (_tpsShadowModeApplied != shadowMode)
			{
				_tpsShadowModeApplied = shadowMode;
				SetCastShadowRecursive(TpsVisual, shadowMode);
			}
		}
		if (local)
		{
			bool tpsBodyActive = tps || tpsShadowOnly;
			if (TpsAnimTree != null && TpsAnimTree.Active != tpsBodyActive) TpsAnimTree.Active = tpsBodyActive;
			if (AimModifier != null && AimModifier.Active != tpsBodyActive) AimModifier.Active = tpsBodyActive;
		}
		UpdateActiveWeapon();
	}

	protected void ApplyViewmodelLayer()
	{
		Node root = GetNodeOrNull<Node>(ViewmodelMeshRootPath);
		if (root != null) SetRenderLayersRecursive(root, ViewmodelRenderLayer);
	}

	protected void ApplyTpsLayer()
	{
		if (GodotObject.IsInstanceValid(TpsVisual)) SetRenderLayersRecursive(TpsVisual, TpsRenderLayer);
	}

	protected static void SetRenderLayersRecursive(Node n, uint layer)
	{
		if (n is VisualInstance3D vi) vi.Layers = layer;
		foreach (Node c in n.GetChildren())
			SetRenderLayersRecursive(c, layer);
	}

	protected static void SetCastShadowRecursive(Node n, GeometryInstance3D.ShadowCastingSetting mode)
	{
		if (n is GeometryInstance3D gi) gi.CastShadow = mode;
		foreach (Node c in n.GetChildren())
			SetCastShadowRecursive(c, mode);
	}

	protected (string key, Vector2 pos)[] StandPoints() => new[] {
		(IdleStanding, new Vector2(0, 0)), (WalkForward, new Vector2(0, 100)), (WalkBackward, new Vector2(0, -100)),
		(WalkStrafeLeft, new Vector2(-100, 0)), (WalkStrafeRight, new Vector2(100, 0)),
	};

	protected (string key, Vector2 pos)[] AimPoints() => new[] {
		(IdleAimed, new Vector2(0, 0)), (WalkForwardAimed, new Vector2(0, 100)), (WalkBackwardAimed, new Vector2(0, -100)),
		(WalkStrafeLeftAimed, new Vector2(-100, 0)), (WalkStrafeRightAimed, new Vector2(100, 0)),
	};

	protected (string key, Vector2 pos)[] CrouchPoints() => new[] {
		(IdleCrouched, new Vector2(0, 0)), (WalkForward, new Vector2(0, 100)), (WalkBackward, new Vector2(0, -100)),
		(WalkStrafeLeft, new Vector2(-100, 0)), (WalkStrafeRight, new Vector2(100, 0)),
	};

	protected static void PopulateBlendSpace(AnimationNodeBlendSpace2D bs, AnimationPlayer player, (string key, Vector2 pos)[] points)
	{
		for (int i = bs.GetBlendPointCount() - 1; i >= 0; i--)
			bs.RemoveBlendPoint(i);
		foreach (var (key, pos) in points)
			if (!string.IsNullOrEmpty(key) && player.HasAnimation(key))
				bs.AddBlendPoint(new AnimationNodeAnimation { Animation = key }, pos);
	}

	protected void AssignTreeAnimations(AnimationNodeBlendTree blend, AnimationPlayer player)
	{
		if (blend.GetNode("StandWalk") is AnimationNodeBlendSpace2D s)
			PopulateBlendSpace(s, player, StandPoints());
		if (blend.GetNode("AimLoco") is AnimationNodeBlendSpace2D a)
			PopulateBlendSpace(a, player, AimPoints());
		if (blend.GetNode("CrouchLoco") is AnimationNodeBlendSpace2D c)
			PopulateBlendSpace(c, player, CrouchPoints());
		if (blend.GetNode("RunLoop") is AnimationNodeAnimation r)
			r.Animation = RunForward;
		if (blend.GetNode("SprintLoop") is AnimationNodeAnimation sp)
			sp.Animation = SprintForward;
	}

	protected void UpdateGripLayer()
	{
		if (_player == null)
			return;
		string nonAim = _grip switch
		{
			GripType.Angled => IdlePoseGripAngled,
			GripType.Vertical => IdlePoseGripVertical,
			_ => null,
		};
		string aim = _grip switch
		{
			GripType.Angled => AimPoseGripAngled,
			GripType.Vertical => AimPoseGripVertical,
			_ => null,
		};
		if (_gripPose != null && nonAim != null && _player.HasAnimation(nonAim))
			_gripPose.Animation = nonAim;
		if (_gripPoseAim != null && aim != null && _player.HasAnimation(aim))
			_gripPoseAim.Animation = aim;
	}

	protected void BuildAnimationTree()
	{
		if (!UseAnimationTree || _player == null)
		{
			var saved = GetNodeOrNull<AnimationTree>(FpsTreePath);
			if (saved != null)
				saved.Active = false;
			return;
		}

		_tree = GetNodeOrNull<AnimationTree>(FpsTreePath);
		var bt = _tree?.TreeRoot as AnimationNodeBlendTree;
		if (_tree == null || bt == null || !bt.HasNode("StandWalk"))
		{
			GD.PushWarning("[NetworkPlayer] No AnimationTree with StandWalk found in scene.");
			return;
		}
		AssignTreeAnimations(bt, _player);
		_tree.AnimPlayer = _tree.GetPathTo(_player);
		_actionAnim = bt.GetNode("ActionAnim") as AnimationNodeAnimation;
		_actionRefNode = bt.HasNode("ActionRef") ? bt.GetNode("ActionRef") as AnimationNodeAnimation : null;
		_actionRef2Node = bt.HasNode("ActionRef2") ? bt.GetNode("ActionRef2") as AnimationNodeAnimation : null;
		if (_actionRefNode != null) _actionRefNode.Animation = ActionRefIdle;
		if (_actionRef2Node != null) _actionRef2Node.Animation = ActionRefIdle;
		_gripPose = bt.GetNode("GripPose") as AnimationNodeAnimation;
		_gripPoseAim = bt.GetNode("GripPoseAim") as AnimationNodeAnimation;
		_locoStopAnim = bt.GetNode("LocoStopAnim") as AnimationNodeAnimation;
		UpdateGripLayer();
		_tree.Active = true;
		_tree.CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Manual;
		_tree.Set("parameters/ActionSub/sub_amount", 1f);
		_tree.Set("parameters/ActionAdd/add_amount", 1f);
	}

	protected void BuildTpsTree()
	{
		if (CurrentGameMode == PresentationMode.Server || _tpsPlayer == null)
			return;
		_tpsTree = TpsAnimTree;
		var bt = _tpsTree?.TreeRoot as AnimationNodeBlendTree;
		if (_tpsTree == null || bt == null || !bt.HasNode("Action"))
		{
			GD.PushWarning("[NetworkPlayer] No TpsAnimationTree with an Action node found in scene.");
			return;
		}
		_tpsTree.AnimPlayer = _tpsTree.GetPathTo(_tpsPlayer);
		Node playerRoot = _tpsPlayer.GetNodeOrNull(_tpsPlayer.RootNode);
		if (playerRoot != null)
			_tpsTree.RootNode = _tpsTree.GetPathTo(playerRoot);
		if (bt.GetNode("Idle") is AnimationNodeAnimation idle && !string.IsNullOrEmpty(TpsIdleLoop))
			idle.Animation = TpsIdleLoop;
		_tpsAimPoseNode = bt.GetNode("AimPose") as AnimationNodeAnimation;
		if (_tpsAimPoseNode != null && !string.IsNullOrEmpty(TpsAimPose))
			_tpsAimPoseNode.Animation = TpsAimPose;
		if (bt.GetNode("AimRef") is AnimationNodeAnimation aimRef && !string.IsNullOrEmpty(TpsIdleLoop))
			aimRef.Animation = TpsIdleLoop;
		_tpsActionAnim = bt.GetNode("ActionAnim") as AnimationNodeAnimation;
		_tpsActionRefNode = bt.HasNode("ActionRef") ? bt.GetNode("ActionRef") as AnimationNodeAnimation : null;
		_tpsActionRef2Node = bt.HasNode("ActionRef2") ? bt.GetNode("ActionRef2") as AnimationNodeAnimation : null;
		if (_tpsActionRefNode != null) _tpsActionRefNode.Animation = TpsIdlePose;
		if (_tpsActionRef2Node != null) _tpsActionRef2Node.Animation = TpsIdlePose;
		_tpsTree.Active = true;
		_tpsTree.Set("parameters/AimSub/sub_amount", 1f);
		_tpsTree.Set("parameters/ActionSub/sub_amount", 1f);
		_tpsTree.Set("parameters/ActionAdd/add_amount", 1f);
	}

	protected void SetupTpsAimModifier()
	{
		if (CurrentGameMode == PresentationMode.Server || TpsVisual == null)
			return;
		var skel = TpsVisual.GetNodeOrNull<Skeleton3D>("Armature/Skeleton3D");
		if (skel == null)
			return;
		_tpsAimModifier = new TpsAimModifier
		{
			Name = "TpsAimModifier",
			Additive = true,
			BodyNode = TpsVisual,
			AimBoneName = TpsAimBoneName,
			PitchScale = TpsAimPitchScale,
		};
		skel.AddChild(_tpsAimModifier);
	}

	protected void EditorRebuildTree()
	{
		if (!Engine.IsEditorHint())
			return;
		_animEnumHint = null;
		_tpsAnimEnumHint = null;
		NotifyPropertyListChanged();
		var player = GetNodeOrNull<AnimationPlayer>(CharacterAnimationPath);
		if (player == null)
		{ GD.PushWarning("[NetworkPlayer] CharacterAnimationPath unresolved"); return; }

		var tree = GetNodeOrNull<AnimationTree>(FpsTreePath);
		var blend = tree?.TreeRoot as AnimationNodeBlendTree;
		if (tree == null || blend == null || !blend.HasNode("StandWalk"))
		{
			GD.PushWarning("[NetworkPlayer] No AnimationTree with StandWalk in scene — add one in the editor.");
			return;
		}
		AssignTreeAnimations(blend, player);
		tree.AnimPlayer = tree.GetPathTo(player);
		GD.Print("[NetworkPlayer] Animations assigned — Ctrl+S to save.");
	}

	protected string GetAnimationEnumHint()
	{
		if (string.IsNullOrEmpty(_animEnumHint))
			_animEnumHint = BuildAnimationEnumHint(CharacterAnimationPath);
		return _animEnumHint;
	}

	protected string GetTpsAnimationEnumHint()
	{
		if (string.IsNullOrEmpty(_tpsAnimEnumHint))
			_tpsAnimEnumHint = BuildAnimationEnumHint(TpsAnimationPath);
		return _tpsAnimEnumHint;
	}

	protected string BuildAnimationEnumHint(NodePath playerPath)
	{
		var player = GetNodeOrNull<AnimationPlayer>(playerPath);
		if (player == null)
			return "";
		var list = new List<string>();
		foreach (StringName lib in player.GetAnimationLibraryList())
		{
			var library = player.GetAnimationLibrary(lib);
			if (library == null)
				continue;
			foreach (StringName a in library.GetAnimationList())
				list.Add(lib.ToString().Length > 0 ? $"{lib}/{a}" : a.ToString());
		}
		list.Sort();
		return string.Join(",", list);
	}

	protected void ApplyViewMode() => ApplyModeVisibility();

	protected virtual void ApplyEditorPreview(float dt = 0f) { }

	/// <summary>Builds the sim, weapon and animation rig, then applies per-mode visibility.</summary>
	public override void _Ready()
	{
		SetProcess(true);
		ProcessPriority = 100;
		if (Engine.IsEditorHint())
		{
			SetPhysicsProcess(false);
			return;
		}
		IsPuppet = CurrentGameMode == PresentationMode.Remote;
		SetupSim();
		if (CurrentGameMode != PresentationMode.Server)
		{
			_player = GetNodeOrNull<AnimationPlayer>(CharacterAnimationPath);
			ResolveWeaponPlayers();
			BuildAnimationTree();
			BuildTpsTree();
			PreWarmAnimationOneShots(_tree);
			ApplyViewmodelLayer();
			ApplyTpsLayer();
		}
		ApplyModeVisibility();
		if (CurrentGameMode != PresentationMode.Server)
			AddToGroup("puddle_feet");
		if (CurrentGameMode == PresentationMode.Local)
			Input.MouseMode = Input.MouseModeEnum.Captured;
	}

	/// <summary>Editor: shows the animation-name string fields as enum dropdowns.</summary>
	public override void _ValidateProperty(Godot.Collections.Dictionary property)
	{
		var type = (Variant.Type)property["type"].AsInt64();
		var usage = (PropertyUsageFlags)property["usage"].AsInt64();
		if (type != Variant.Type.String || !usage.HasFlag(PropertyUsageFlags.ScriptVariable))
			return;
		string name = (string)property["name"];
		if (name.EndsWith("BoneName"))
			return;
		string hint = name.StartsWith("Tps") ? GetTpsAnimationEnumHint() : GetAnimationEnumHint();
		if (!string.IsNullOrEmpty(hint))
		{
			property["hint"] = (int)PropertyHint.Enum;
			property["hint_string"] = hint;
		}
	}

	/// <summary>Adds a recoil kick in degrees, clamped to RecoilMaxDegrees.</summary>
	public void AddRecoilKick(Vector3 degreesXYZ)
	{
		_recoilCurrent += degreesXYZ;
		float mag = _recoilCurrent.Length();
		if (mag > RecoilMaxDegrees)
			_recoilCurrent = _recoilCurrent * (RecoilMaxDegrees / mag);
	}
}
