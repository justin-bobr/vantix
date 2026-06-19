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
using System.Text;
using Godot;

namespace Vantix.Net;

/// <summary>Top-level netcode boot, an autoload so it exists before any scene. Spawns NetServer and/or
/// NetClient per the parsed NetCli. Polls LiteNetLib every physics tick at ProcessPriority = -100 so
/// inputs/snapshots arrive before <see cref="NetworkPlayer._PhysicsProcess"/>.</summary>
public partial class NetMain : Node
{
	public static NetMain Instance { get; private set; }

	public NetCli Cli { get; private set; }
	public NetServer Server { get; private set; }
	public NetClient Client { get; private set; }
	public PuppetManager Puppets { get; private set; }

	/// <summary>The local player — spawned into the Players container after SpawnAck.</summary>
	public NetworkPlayer LocalPlayer { get; private set; }

	/// <summary>LocalPlayer accessor for other systems (Crosshair, DebugOverlay, reconcile).</summary>
	public NetworkPlayer FindLocalPlayer() => LocalPlayer;

	private bool _localPlayerInitialized;

	/// <summary>Parses the CLI and applies settings. Server/Listen/auto-connect Client start immediately;
	/// a Client without <c>--connect</c> waits for the menu to call ConnectToServer.</summary>
	public override void _Ready()
	{
		Instance = this;
		ProcessPriority = -100;

		Cli = NetCli.Parse();

		if (Cli.Mode != NetMode.Server)
		{
			Settings.Load();
			Settings.ApplyDisplay();
		}
		else
		{
			Settings.ApplyServerHeadlessDefaults();
			Engine.MaxFps = Cli.TickRate;
		}
		Dbg.Print($"[NetMain] {Cli}");
		System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
		GD.Print(
			$"[Runtime] GC mode: {(System.Runtime.GCSettings.IsServerGC ? "Server GC ON" : "Workstation GC (CoreCLR-host ignores runtimeconfig)")}  latency: {System.Runtime.GCSettings.LatencyMode}"
		);
		NetStats.Reset(Cli.Mode);

		switch (Cli.Mode)
		{
			case NetMode.Server:
				Server = new NetServer(Cli);
				Server.Start();
				break;

			case NetMode.Listen:
				Server = new NetServer(Cli);
				Server.Start();
				CreateAndStartClient();
				break;

			case NetMode.Client:
				if (Cli.AutoConnect)
					CreateAndStartClient();
				break;
		}
	}

	/// <summary>Connect button: applies host/port, starts the client, switches to the loading scene.</summary>
	public void ConnectToServer(string host, int port)
	{
		Cli.Mode = NetMode.Client;
		Cli.AutoConnect = true;
		Cli.Host = host;
		Cli.Port = port;
		NetStats.Reset(Cli.Mode);
		Dbg.Print($"[NetMain] ConnectToServer → {Cli.Host}:{Cli.Port}");
		CreateAndStartClient();
		GetTree().ChangeSceneToFile("res://loading.tscn");
	}

	/// <summary>Creates a NetClient, wires its events, and starts it.</summary>
	private void CreateAndStartClient()
	{
		Client = new NetClient(Cli);
		Client.OnSpawned += OnClientSpawned;
		Client.OnDisconnected += HandleDisconnect;
		Client.Start();
	}

	/// <summary>Called from NetClient once SpawnAck has arrived.</summary>
	private void OnClientSpawned()
	{
		_localPlayerInitialized = false;
	}

	private PackedScene _characterScene;

	private bool _teamSelectFlowInitialized;

	/// <summary>One-shot: on a Spectator SpawnAck (competitive, no spawn yet), spawns PreviewCameraController +
	/// TeamSelectionMenu. Both self-destruct on SpawnAuthorize. Skipped for deathmatch.</summary>
	private void TryInitializeTeamSelectFlow()
	{
		if (_teamSelectFlowInitialized || Client == null || !Client.Spawned)
			return;
		if (Client.SpawnAuthorized)
		{
			_teamSelectFlowInitialized = true;
			return;
		}
		var tree = GetTree();
		if (tree?.CurrentScene == null || tree.CurrentScene.Name != "World")
			return;
		_teamSelectFlowInitialized = true;
		tree.CurrentScene.AddChild(new PreviewCameraController { Name = "PreviewCameraController" });
		tree.CurrentScene.AddChild(new TeamSelectionMenu { Name = "TeamSelectionMenu" });
		Dbg.Print("[NetMain] Spectator team → PreviewCameraController + TeamSelectionMenu spawned");
	}

	/// <summary>Spawns LocalPlayer + HUD into the Players container once SpawnAck arrived, the spawn is
	/// authorized, and world.tscn is active.</summary>
	private void TryInitializeLocalPlayer()
	{
		if (_localPlayerInitialized || Client == null || !Client.Spawned)
			return;
		if (!Client.SpawnAuthorized)
			return;
		var tree = GetTree();
		if (tree?.CurrentScene == null)
			return;
		if (tree.CurrentScene.Name != "World")
			return;

		var playersContainer = tree.CurrentScene.GetNodeOrNull<Node3D>("Players");
		if (playersContainer == null)
		{
			GD.PushError("[NetMain] World/Players Node3D missing — cannot spawn LocalPlayer");
			return;
		}

		if (LocalPlayer != null && GodotObject.IsInstanceValid(LocalPlayer))
		{
			Dbg.Print("[NetMain] Cleaning up old LocalPlayer instance (reconnect)");
			LocalPlayer.QueueFree();
			LocalPlayer = null;
		}
		if (Puppets != null)
		{
			Puppets.Shutdown();
			Puppets = null;
		}

		_characterScene ??= GD.Load<PackedScene>("res://character/local_player.tscn");
		var local = _characterScene.Instantiate<NetworkPlayer>();
		local.CurrentGameMode = PresentationMode.Local;
		local.NetId = Client.OwnNetId;
		local.Name = $"local_{Client.OwnNetId}";
		local.Position = Client.PendingSpawnPos;
		var rot = local.Rotation;
		rot.Y = Client.PendingSpawnYaw;
		local.Rotation = rot;
		playersContainer.AddChild(local);
		LocalPlayer = local;
		local.ResetInterpToCurrentPos();
		ViewmodelMotionBlur.Reset();
		ViewmodelMotionBlur.Attach(local);
		Settings.Apply(tree);
		Dbg.Print(
			$"[NetMain] LocalPlayer spawned: netId={local.NetId} at {local.GlobalPosition} yaw={local.Rotation.Y:F2}"
		);

		Puppets = new PuppetManager();
		Puppets.Init(playersContainer, Client);
		Dbg.Print("[NetMain] PuppetManager initialized");

		var scoreboard = new Scoreboard();
		tree.CurrentScene.AddChild(scoreboard);
		Dbg.Print("[NetMain] Scoreboard attached (Tab to toggle)");

		var hitmarkerLayer = new CanvasLayer { Name = "hitmarker_layer", Layer = 110 };
		tree.CurrentScene.AddChild(hitmarkerLayer);
		hitmarkerLayer.AddChild(new HudHitmarker { Name = "HudHitmarker" });
		HudGate.Register(hitmarkerLayer);

		var killfeedLayer = new CanvasLayer { Name = "killfeed_layer", Layer = 110 };
		tree.CurrentScene.AddChild(killfeedLayer);
		killfeedLayer.AddChild(new HudKillfeed { Name = "HudKillfeed" });
		HudGate.Register(killfeedLayer);

		var lowhpLayer = new CanvasLayer { Name = "lowhp_layer", Layer = 105 };
		tree.CurrentScene.AddChild(lowhpLayer);
		lowhpLayer.AddChild(new HudLowHpFx { Name = "HudLowHpFx" });
		HudGate.Register(lowhpLayer);

		tree.CurrentScene.AddChild(new PostCanvasFx { Name = "PostCanvasFx" });

		tree.CurrentScene.AddChild(new ConsoleHud { Name = "ConsoleHud" });

		tree.CurrentScene.AddChild(new ServerAimRayDebug { Name = "ServerAimRayDebug" });
		tree.CurrentScene.AddChild(new ServerBodyCapsuleDebug { Name = "ServerBodyCapsuleDebug" });
		tree.CurrentScene.AddChild(new HudServerHitboxesDebug { Name = "HudServerHitboxesDebug" });

		tree.CurrentScene.AddChild(new HudMiniProfiler { Name = "HudMiniProfiler" });

		tree.CurrentScene.AddChild(new BulletTracerPool { Name = "BulletTracerPool" });

		_localPlayerInitialized = true;
	}

	/// <summary>Pumps server + client every physics tick and lazily spawns the local player when ready.</summary>
	public override void _PhysicsProcess(double delta)
	{
		using var _prof = MiniProfiler.Sample("NetMain._PhysicsProcess (both)");
		Server?.Poll();
		using (MiniProfiler.SampleClient("NetClient.Poll"))
			Client?.Poll();

		if (!_localPlayerInitialized)
		{
			TryInitializeLocalPlayer();
			TryInitializeTeamSelectFlow();
		}
		HudGate.Tick();
	}

	private const double SpikeThresholdSec = 0.030;
	private int _gen0Last,
		_gen1Last,
		_gen2Last;
	private long _heapLast;
	private bool _spikeTrackerInited;
	private long _drawCallsLast,
		_objCountLast,
		_nodeCountLast,
		_orphanLast;
	private long _physActiveLast,
		_physPairsLast,
		_physIslandsLast;
	private long _vramLast;
	private double _timeProcessLast,
		_timePhysProcessLast;

	public override void _Process(double delta)
	{
		if (Dbg.Enabled || Settings.ShowDebugBar)
			EnsureViewportMeasurement();
		if (!Dbg.Enabled)
			return;
		TrackFrameSpike(delta);
	}

	/// <summary>GPU frame time (ms): sum of measured render times across tracked viewports. 0 until measurement is enabled.</summary>
	public double MeasuredGpuMs()
	{
		double sum = 0;
		foreach (var vp in _measuredViewports)
			if (GodotObject.IsInstanceValid(vp))
				sum += RenderingServer.ViewportGetMeasuredRenderTimeGpu(vp.GetViewportRid());
		return sum;
	}

	/// <summary>Render-thread CPU time (ms) summed over tracked viewports. Companion to MeasuredGpuMs.</summary>
	public double MeasuredRenderCpuMs()
	{
		double sum = 0;
		foreach (var vp in _measuredViewports)
			if (GodotObject.IsInstanceValid(vp))
				sum += RenderingServer.ViewportGetMeasuredRenderTimeCpu(vp.GetViewportRid());
		return sum;
	}

	private readonly List<Viewport> _measuredViewports = new();
	private double _nextViewportScanAt;

	private void EnsureViewportMeasurement()
	{
		double now = Time.GetTicksMsec() / 1000.0;
		if (now < _nextViewportScanAt)
			return;
		_nextViewportScanAt = now + 5.0;
		_measuredViewports.Clear();
		CollectViewports(GetTree().Root, _measuredViewports);
		for (int i = _measuredViewports.Count - 1; i >= 0; i--)
		{
			if (
				_measuredViewports[i] is SubViewport sv
				&& (sv.RenderTargetUpdateMode == SubViewport.UpdateMode.Disabled || sv.Size.X < 2 || sv.Size.Y < 2)
			)
			{
				_measuredViewports.RemoveAt(i);
				continue;
			}
			RenderingServer.ViewportSetMeasureRenderTime(_measuredViewports[i].GetViewportRid(), true);
		}
	}

	private static void CollectViewports(Node n, List<Viewport> outList)
	{
		if (n is Viewport vp)
			outList.Add(vp);
		foreach (Node c in n.GetChildren())
			CollectViewports(c, outList);
	}

	private string BuildViewportTimesReport()
	{
		var sb = new StringBuilder(256);
		foreach (var vp in _measuredViewports)
		{
			if (!GodotObject.IsInstanceValid(vp))
				continue;
			Rid rid = vp.GetViewportRid();
			double cpu = RenderingServer.ViewportGetMeasuredRenderTimeCpu(rid);
			double gpu = RenderingServer.ViewportGetMeasuredRenderTimeGpu(rid);
			if (cpu < 0.25 && gpu < 0.25)
				continue;
			sb.Append("\n  vp '")
				.Append(vp.Name)
				.Append("': cpu=")
				.Append(cpu.ToString("F2"))
				.Append("ms gpu=")
				.Append(gpu.ToString("F2"))
				.Append("ms");
		}
		return sb.ToString();
	}

	/// <summary>Logs frame spikes above SpikeThresholdSec with GC stats and Godot perf deltas. Gated on
	/// Dbg.Enabled so production builds carry no overhead.</summary>
	private void TrackFrameSpike(double delta)
	{
		if (!_spikeTrackerInited)
		{
			_spikeTrackerInited = true;
			_gen0Last = System.GC.CollectionCount(0);
			_gen1Last = System.GC.CollectionCount(1);
			_gen2Last = System.GC.CollectionCount(2);
			_heapLast = System.GC.GetTotalMemory(forceFullCollection: false);
			return;
		}

		if (delta < SpikeThresholdSec)
			return;

		long drawCalls = (long)Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame);
		long objCount = (long)Performance.GetMonitor(Performance.Monitor.ObjectCount);
		long nodeCount = (long)Performance.GetMonitor(Performance.Monitor.ObjectNodeCount);
		long orphan = (long)Performance.GetMonitor(Performance.Monitor.ObjectOrphanNodeCount);
		long physActive = (long)Performance.GetMonitor(Performance.Monitor.Physics3DActiveObjects);
		long physPairs = (long)Performance.GetMonitor(Performance.Monitor.Physics3DCollisionPairs);
		long physIslands = (long)Performance.GetMonitor(Performance.Monitor.Physics3DIslandCount);
		long vram = (long)Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed);
		double timeProc = Performance.GetMonitor(Performance.Monitor.TimeProcess);
		double timePhys = Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess);

		int gen0 = System.GC.CollectionCount(0);
		int gen1 = System.GC.CollectionCount(1);
		int gen2 = System.GC.CollectionCount(2);
		long heap = System.GC.GetTotalMemory(forceFullCollection: false);
		int dGen0 = gen0 - _gen0Last;
		int dGen1 = gen1 - _gen1Last;
		int dGen2 = gen2 - _gen2Last;
		long dHeapKb = (heap - _heapLast) / 1024;
		long heapKb = heap / 1024;
		string gcTag =
			dGen2 > 0 ? " [GC-GEN2]"
			: dGen1 > 0 ? " [GC-GEN1]"
			: dGen0 > 0 ? " [GC-GEN0]"
			: "";

		long dDraw = drawCalls - _drawCallsLast;
		long dObj = objCount - _objCountLast;
		long dNode = nodeCount - _nodeCountLast;
		long dOrphan = orphan - _orphanLast;
		long dPhysActive = physActive - _physActiveLast;
		long dPhysPairs = physPairs - _physPairsLast;
		long dPhysIslands = physIslands - _physIslandsLast;
		long dVramKb = (vram - _vramLast) / 1024;
		double dProc = (timeProc - _timeProcessLast) * 1000;
		double dPhys = (timePhys - _timePhysProcessLast) * 1000;

		string roleTag = Cli?.Mode switch
		{
			NetMode.Server => "[SV]",
			NetMode.Client => $"[CL netId={LocalPlayer?.NetId.ToString() ?? "?"}]",
			NetMode.Listen => $"[HOST netId={LocalPlayer?.NetId.ToString() ?? "?"}]",
			_ => "[?]",
		};

		GD.Print(
			$"[SPIKE]{roleTag} dt={delta * 1000:F1}ms{gcTag} | gc Δ gen0={dGen0} gen1={dGen1} gen2={dGen2} heap={heapKb}KB (Δ {dHeapKb:+0;-0;0}KB)\n"
				+ $"  godot: process={timeProc * 1000:F2}ms phys={timePhys * 1000:F2}ms (Δ {dProc:+0.0;-0.0;0}/{dPhys:+0.0;-0.0;0}ms)\n"
				+ $"  render: draw={drawCalls} (Δ {dDraw:+0;-0;0}) vram={vram / (1024 * 1024)}MB (Δ {dVramKb:+0;-0;0}KB)\n"
				+ $"  scene: objects={objCount} (Δ {dObj:+0;-0;0}) nodes={nodeCount} (Δ {dNode:+0;-0;0}) orphans={orphan} (Δ {dOrphan:+0;-0;0})\n"
				+ $"  physics: active={physActive} (Δ {dPhysActive:+0;-0;0}) pairs={physPairs} (Δ {dPhysPairs:+0;-0;0}) islands={physIslands} (Δ {dPhysIslands:+0;-0;0})"
				+ BuildViewportTimesReport()
		);

		_gen0Last = gen0;
		_gen1Last = gen1;
		_gen2Last = gen2;
		_heapLast = heap;
		_drawCallsLast = drawCalls;
		_objCountLast = objCount;
		_nodeCountLast = nodeCount;
		_orphanLast = orphan;
		_physActiveLast = physActive;
		_physPairsLast = physPairs;
		_physIslandsLast = physIslands;
		_vramLast = vram;
		_timeProcessLast = timeProc;
		_timePhysProcessLast = timePhys;
	}

	private DisconnectScreen _disconnectScreen;

	/// <summary>User-initiated disconnect: stops the NetClient and routes through the same cleanup as a
	/// transport drop. Unsubscribes from OnDisconnected first so cleanup runs once.</summary>
	public void RequestDisconnect(string reason = "Disconnected by user")
	{
		Dbg.Print($"[NetMain] RequestDisconnect: {reason}");
		if (Client != null)
		{
			Client.OnDisconnected -= HandleDisconnect;
			Client.Stop();
		}
		HandleDisconnect(reason);
	}

	/// <summary>Post-disconnect idle state (set on disconnect, cleared on Reconnect/Quit). SceneLoader checks
	/// this to suppress its auto-connect logic.</summary>
	public static bool PostDisconnectIdle;

	private void HandleDisconnect(string reason)
	{
		Dbg.Print($"[NetMain] HandleDisconnect: {reason}");
		if (LocalPlayer != null && GodotObject.IsInstanceValid(LocalPlayer))
		{
			LocalPlayer.QueueFree();
			LocalPlayer = null;
		}
		Puppets?.Shutdown();
		Puppets = null;
		_localPlayerInitialized = false;
		_teamSelectFlowInitialized = false;
		HudGate.Reset();

		PostDisconnectIdle = true;
		Cli.AutoConnect = false;
		GetTree().ChangeSceneToFile("res://loading.tscn");

		if (_disconnectScreen != null && GodotObject.IsInstanceValid(_disconnectScreen))
		{
			var oldParent = _disconnectScreen.GetParent();
			if (oldParent != null && GodotObject.IsInstanceValid(oldParent))
				oldParent.QueueFree();
			_disconnectScreen = null;
		}
		var layer = new CanvasLayer { Layer = 1000, Name = "disconnect_overlay" };
		GetTree().Root.AddChild(layer);
		_disconnectScreen = new DisconnectScreen { Reason = reason };
		layer.AddChild(_disconnectScreen);
	}

	/// <summary>Reconnect button: tears down the old client, starts a fresh one, and re-enters loading.tscn to
	/// rerun the connect flow.</summary>
	public void RequestReconnect()
	{
		Dbg.Print("[NetMain] Reconnect requested");
		if (_disconnectScreen != null && GodotObject.IsInstanceValid(_disconnectScreen))
		{
			var parent = _disconnectScreen.GetParent();
			if (parent != null && GodotObject.IsInstanceValid(parent))
				parent.QueueFree();
			_disconnectScreen = null;
		}

		if (Client != null)
		{
			Client.Stop();
			Client = null;
		}
		PostDisconnectIdle = false;
		Cli.AutoConnect = true;
		CreateAndStartClient();

		GetTree().ChangeSceneToFile("res://loading.tscn");
	}

	/// <summary>Tears down networking resources on shutdown.</summary>
	public override void _ExitTree()
	{
		Puppets?.Shutdown();
		Server?.Stop();
		Client?.Stop();
		Instance = null;
	}
}
