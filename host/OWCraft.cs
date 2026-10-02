using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using OWML.Common;
using OWML.ModHelper;
using OWCraft.Link;
using OWCraft.Player;
using OWCraft.UI;
using OWCraft.World;
using UnityEngine;
using UnityEngine.InputSystem;

namespace OWCraft
{
	/// <summary>
	/// Real Minecraft, played on Outer Wilds planets. Minecraft runs alongside the game with SkyCraft's
	/// Fabric mod (MIT, chasmlol/SkyCraft) and talks to this mod over shared memory (SkyCraft protocol
	/// v11): it gets the planet's collision and our controls, and sends back its chunk meshes, which
	/// are drawn on the planet, plus its hand/HUD/screens as an overlay.
	/// </summary>
	public class OWCraft : ModBehaviour
	{
		static OWCraft _instance;
		static readonly HashSet<string> Once = new HashSet<string>();
		public static bool Verbose;

		SharedLink _link;
		CollisionStreamer _collision;
		BlockRenderer _blocks;
		OverlayRenderer _overlay;
		readonly InputForwarder _input = new InputForwarder();
		readonly PlayerDriver _driver = new PlayerDriver();
		readonly CommandRunner _commands = new CommandRunner();
		float _clearArmedUntil = -1f;
		const int ClearRadius = 48;
		// Fixed: OWML's settings turned the backslash into a slash, which named a different mapping.
		readonly string _mappingName = Proto.DefaultMappingName;
		bool _inSolarSystem;
		int _guestPid;
		bool _sized; // this Minecraft already got our size and walking speed
		float _walkSpeed = 6f; // m/s; Minecraft walks at 4.3 (attribute 0.1), Outer Wilds' player runs at 6
		// Minecraft runs in the background and Windows schedules its GPU work behind the foreground game:
		// with Outer Wilds uncapped, Minecraft waited in the driver for most of each frame (8-25 fps).
		// A cap leaves the GPU idle gaps it needs. 0 = no cap.
		int _fpsCap = 60;
		bool _capped;
		int _savedTargetFrameRate, _savedVSyncCount;
		// Minecraft's eye is at 1.62 m, the game's at 1.85 m: entering would drop the view. Scaled 1.11
		// Steve is 2.0 m tall with the eye at 1.80 m and still fits through a 2-block doorway.
		const string ScaleCommand = "/attribute @s minecraft:scale base set 1.11";
		bool _wasGuestAlive;
		float _guestLostAt = -1f;
		float _nextStatsAt;
		float _nextCameraFixAt;
		long _mcFramesAtStats;
		int _overlayAtStats;
		float _statsStartedAt;

		public static void Log(string message)
		{
			if (_instance != null) _instance.ModHelper.Console.WriteLine(message, MessageType.Info);
			else Debug.Log("[OWCraft] " + message);
		}

		/// <summary>Logs a message once per key (for things that could otherwise repeat every frame).</summary>
		public static void LogOnce(string key, string message)
		{
			lock (Once)
			{
				if (!Once.Add(key)) return;
			}
			// Worker threads can't touch Unity; Debug.Log is thread-safe and lands in the same log.
			Debug.Log("[OWCraft] " + message);
		}

		public override void Configure(IModConfig config)
		{
			Verbose = config.GetSettingsValue<bool>("verboseLogging");
			_fpsCap = config.GetSettingsValue<int>("minecraftModeFpsCap");
			_walkSpeed = config.GetSettingsValue<float>("minecraftWalkSpeed");
			_sized = false; // apply a new speed
			_input.ToggleKey = ParseKey(config.GetSettingsValue<string>("toggleKey"), Key.F6);
			_input.OpenMenuKey = ParseKey(config.GetSettingsValue<string>("minecraftMenuKey"), Key.O);
		}

		static Key ParseKey(string s, Key fallback) =>
			!string.IsNullOrEmpty(s) && Enum.TryParse(s, true, out Key k) ? k : fallback;

		void Start()
		{
			_instance = this;
			new Harmony("Yaekai.OWCraft").PatchAll(Assembly.GetExecutingAssembly());
			try
			{
				_link = new SharedLink(_mappingName);
			}
			catch (Exception e)
			{
				ModHelper.Console.WriteLine("couldn't create the Minecraft link: " + e.Message, MessageType.Error);
				return;
			}
			_collision = new CollisionStreamer(_link);
			_blocks = new BlockRenderer();
			_overlay = new OverlayRenderer();
			Log($"link {_mappingName} ready; start Minecraft with -Dskycraft.link={_mappingName}");

			LoadManager.OnCompleteSceneLoad += OnSceneLoaded;
			OnSceneLoaded(OWScene.None, LoadManager.GetCurrentScene());
		}

		void OnSceneLoaded(OWScene from, OWScene to)
		{
			if (_driver.Active) _driver.Exit();
			_inSolarSystem = to == OWScene.SolarSystem;
			AnchorGrid.Reset();
			if (!_inSolarSystem) return;
			AnchorGrid.ScanScene();
			_blocks.PickShader();
			_blocks.Rebuild();
		}

		// Stutter diagnostics, logged with the 5 s stats line.
		readonly System.Diagnostics.Stopwatch _clock = new System.Diagnostics.Stopwatch();
		int _slowFrames, _frames, _gcAtStats;
		float _worstFrame;
		double _ourMs, _ourWorstMs;
		// Worst single-frame cost per part, to tell which one stutters.
		readonly System.Diagnostics.Stopwatch _partClock = new System.Diagnostics.Stopwatch();
		readonly double[] _partWorst = new double[4];
		static readonly string[] PartNames = { "player", "collision", "blocks", "overlay" };

		void EndPart(int part)
		{
			_partWorst[part] = Math.Max(_partWorst[part], _partClock.Elapsed.TotalMilliseconds);
			_partClock.Restart();
		}

		void Update()
		{
			_clock.Restart();
			try
			{
				Tick();
			}
			finally
			{
				double ms = _clock.Elapsed.TotalMilliseconds;
				_ourMs += ms;
				_ourWorstMs = Math.Max(_ourWorstMs, ms);
				_frames++;
				if (Time.unscaledDeltaTime > 0.05f) _slowFrames++;
				_worstFrame = Mathf.Max(_worstFrame, Time.unscaledDeltaTime);
			}
		}

		/// <summary>How fast Minecraft really runs next to us: its frames, and overlay frames we got.</summary>
		string TakeGuestRates(in SharedLink.GuestState mc, bool haveState)
		{
			float now = Time.unscaledTime, span = Mathf.Max(0.001f, now - _statsStartedAt);
			string s = haveState && _mcFramesAtStats > 0
				? $"Minecraft {(mc.FrameCounter - _mcFramesAtStats) / span:F0} fps, overlay {(_overlay.FramesTaken - _overlayAtStats) / span:F0} fps, ours {_frames / span:F0} fps"
				: "Minecraft fps: n/a";
			if (haveState) _mcFramesAtStats = mc.FrameCounter;
			_overlayAtStats = _overlay.FramesTaken;
			_statsStartedAt = now;
			return s;
		}

		string TakeFrameStats()
		{
			int gc = GC.CollectionCount(0);
			string s = $"frames: {_frames}, {_slowFrames} over 50 ms, worst {_worstFrame * 1000f:F0} ms, " +
				$"mod {(_frames > 0 ? _ourMs / _frames : 0):F1} ms avg / {_ourWorstMs:F0} ms worst ({PartNames[0]} {_partWorst[0]:F0}, {PartNames[1]} {_partWorst[1]:F0}, " +
				$"{PartNames[2]} {_partWorst[2]:F0}, {PartNames[3]} {_partWorst[3]:F0} ms), {gc - _gcAtStats} garbage collections";
			Array.Clear(_partWorst, 0, _partWorst.Length);
			_gcAtStats = gc;
			_frames = _slowFrames = 0;
			_worstFrame = 0f;
			_ourMs = _ourWorstMs = 0;
			return s;
		}

		void Tick()
		{
			if (_link == null) return;
			_link.Heartbeat();
			bool haveState = _link.ReadGuestState(out var mc);
			TrackGuest();

			bool paused = OWTime.IsPaused() || OWInput.IsInputMode(InputMode.Menu);
			var kb = Keyboard.current;
			if (_inSolarSystem && !paused && kb != null && kb[_input.ToggleKey].wasPressedThisFrame) Toggle();

			if (_driver.Active)
			{
				if (paused)
				{
					_input.ReleaseAll(_link);
				}
				else
				{
					bool screen = haveState && mc.Has(Proto.McScreenOpen);
					Vector2 delta = _input.Update(_link, screen, Screen.width, Screen.height);
					if (!screen) _driver.Look(delta, haveState && mc.Sensitivity > 0 ? mc.Sensitivity : 0.5f);
					// Minecraft in third person (F5, or a saved option) hides the hand: back to first person.
					if (haveState && mc.CameraMode != 0 && Time.unscaledTime >= _nextCameraFixAt)
					{
						_nextCameraFixAt = Time.unscaledTime + 0.5f;
						Log($"Minecraft camera mode {mc.CameraMode}: switching it back to first person");
						_link.PushInput(Proto.InKey, InputForwarder.HidF5, 1);
						_link.PushInput(Proto.InKey, InputForwarder.HidF5, 0);
					}
					if (kb != null && kb[Key.F7].wasPressedThisFrame && haveState) ClearBlocks();
					if (haveState && !screen && !_commands.Busy && !_sized && mc.Has(Proto.McInWorld))
					{
						_sized = true;
						// Minecraft's movement_speed attribute: 0.1 walks 4.317 blocks/s.
						float attribute = _walkSpeed > 0f ? _walkSpeed / 43.17f : 0.1f;
						Log($"Minecraft eye height {mc.EyeHeight:F2} m: scaling the player to the game's size, walking speed {_walkSpeed:F1} m/s");
						_commands.Enqueue(ScaleCommand);
						_commands.Enqueue("/attribute @s minecraft:movement_speed base set " + attribute.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture));
					}
					_commands.Update(_link, screen);
					_overlay.ShowCursor = screen;
					_overlay.Cursor = _input.Cursor;
				}
				// The game re-applies its own graphics settings now and then (scene loads, the options menu).
				if (_capped && (Application.targetFrameRate != _fpsCap || QualitySettings.vSyncCount != 0))
				{
					QualitySettings.vSyncCount = 0;
					Application.targetFrameRate = _fpsCap;
				}
				_partClock.Restart();
				bool keep = _driver.Update(mc, haveState, _collision);
				EndPart(0);
				if (!keep || (_guestLostAt >= 0 && Time.unscaledTime - _guestLostAt > 5f))
				{
					Notify("Minecraft mode off");
					ExitMode();
				}
			}
			// Minecraft waits for this before it renders its next frame: send it before our slower work.
			var s = new SharedLink.HostState
			{
				Flags = _inSolarSystem ? Proto.HostInGame : Proto.HostLoading,
				WorldId = 1,
				ViewportW = Screen.width,
				ViewportH = Screen.height,
				GameHour = 12f,
			};
			// Outside Minecraft mode, Minecraft keeps its world loaded but takes no input, and stays where
			// it is (echo its own position, so a teleport from us is a no-op).
			if (!_driver.Active || paused) s.Flags |= Proto.HostMenuOpen;
			// Not below the world: Minecraft respawns its player where we say, and a fallen player's own
			// position would kill it again, over and over.
			if (!_driver.Active && haveState && mc.Has(Proto.McInWorld) && mc.Y > -64)
			{
				_driver.X = mc.X;
				_driver.Y = mc.Y;
				_driver.Z = mc.Z;
				_driver.Yaw = mc.Yaw;
				_driver.Pitch = mc.Pitch;
			}
			_driver.Fill(ref s);
			_link.WriteHostState(s);

			if (_driver.Active)
			{
				_partClock.Restart();
				_collision.Update(_driver.Anchor, _driver.X, _driver.Y, _driver.Z);
				EndPart(1);
				if (Time.unscaledTime >= _nextStatsAt)
				{
					_nextStatsAt = Time.unscaledTime + 5f;
					Log($"Minecraft feet ({_driver.X:F1}, {_driver.Y:F1}, {_driver.Z:F1}), {_driver.GroundGap()} above the game's ground; velocity corrected by up to {PlayerDriver.MaxVelocityCorrection:F0} m/s; {_blocks.LightCount} lights; worst tick age {PlayerDriver.TickAgeMs:F0} ms ({PlayerDriver.LateTickFrames} frames waiting on a late tick); " + _collision.TakeStats());
					PlayerDriver.MaxVelocityCorrection = 0f;
					PlayerDriver.TickAgeMs = 0;
					PlayerDriver.LateTickFrames = 0;
					string rates = TakeGuestRates(mc, haveState); // before TakeFrameStats resets the frame count
					Log(TakeFrameStats() + "; " + rates);
				}
			}

			_partClock.Restart();
			if (_inSolarSystem) _blocks.Update(_link);
			EndPart(2);
			PumpEvents();
			_overlay.Visible = _driver.Active;

		}

		/// <summary>Things that happened in Minecraft (explosions...), from its event ring.</summary>
		void PumpEvents()
		{
			for (int i = 0; i < 64 && _link.TryPopEvent(out var ev); i++)
			{
				if (ev.Type != Proto.EvExplosion) continue;
				Log($"Minecraft explosion at ({ev.A:F1}, {ev.B:F1}, {ev.C:F1}), radius {ev.D:F1}");
				if (_inSolarSystem) ExplosionEffects.Spawn(ev.A, ev.B, ev.C, ev.D);
			}
		}

		void TrackGuest()
		{
			bool alive = _link.GuestAlive;
			if (alive)
			{
				_guestLostAt = -1f;
				int pid = _link.GuestPid;
				if (pid != _guestPid)
				{
					// A new Minecraft: its collision store is empty and it'll resend every chunk.
					if (_guestPid != 0) _blocks?.Clear();
					_guestPid = pid;
					_sized = false;
					Log($"Minecraft connected (pid {pid})");
					if (_driver.Active) _driver.Resync(_collision);
				}
			}
			else if (_wasGuestAlive)
			{
				_guestLostAt = Time.unscaledTime;
				_sized = false; // a restarted Minecraft may be a different world or player
				Log("Minecraft stopped responding");
			}
			_wasGuestAlive = alive;
		}

		void Toggle()
		{
			if (_driver.Active)
			{
				ExitMode();
				return;
			}
			string why = _driver.CanEnter(_link);
			if (why != null)
			{
				Notify("Can't start Minecraft mode: " + why);
				return;
			}
			_driver.Enter(_collision);
			CapFrameRate();
			// Minecraft opens its pause menu when its window loses focus before the link is up; that
			// screen (with its blurred backdrop) would cover the whole view. Escape closes it.
			if (_link.ReadGuestState(out var mc) && mc.Has(Proto.McScreenOpen))
			{
				_link.PushInput(Proto.InKey, InputForwarder.HidEscape, 1);
				_link.PushInput(Proto.InKey, InputForwarder.HidEscape, 0);
			}
			Notify($"Minecraft mode ({_input.ToggleKey} to leave)");
		}

		/// <summary>F7, twice: removes every Minecraft block around you (with /fill, so cheats must be on).</summary>
		void ClearBlocks()
		{
			if (_commands.Busy) return;
			if (Time.unscaledTime > _clearArmedUntil)
			{
				_clearArmedUntil = Time.unscaledTime + 3f;
				Notify($"Press F7 again to remove all Minecraft blocks within {ClearRadius} blocks");
				return;
			}
			_clearArmedUntil = -1f;
			int cx = (int)Math.Floor(_driver.X), cy = (int)Math.Floor(_driver.Y), cz = (int)Math.Floor(_driver.Z);
			// 32x32x32 = 32768 blocks per /fill, Minecraft's default limit.
			for (int x = cx - ClearRadius; x < cx + ClearRadius; x += 32)
			for (int z = cz - ClearRadius; z < cz + ClearRadius; z += 32)
			for (int y = cy - 16; y < cy + 80; y += 32)
			{
				_commands.Enqueue($"/fill {x} {y} {z} {x + 31} {y + 31} {z + 31} air");
			}
			_commands.Enqueue($"/kill @e[type=item,distance=..{ClearRadius * 2}]");
			Notify("Clearing Minecraft blocks around you...");
		}

		void CapFrameRate()
		{
			if (_fpsCap <= 0 || _capped) return;
			_savedTargetFrameRate = Application.targetFrameRate;
			_savedVSyncCount = QualitySettings.vSyncCount;
			QualitySettings.vSyncCount = 0; // targetFrameRate is ignored with vsync on
			Application.targetFrameRate = _fpsCap;
			_capped = true;
			Log($"frame rate capped at {_fpsCap} fps while in Minecraft mode (was target {_savedTargetFrameRate}, vsync {_savedVSyncCount})");
		}

		void UncapFrameRate()
		{
			if (!_capped) return;
			QualitySettings.vSyncCount = _savedVSyncCount;
			Application.targetFrameRate = _savedTargetFrameRate;
			_capped = false;
		}

		void ExitMode()
		{
			UncapFrameRate();
			_commands.Cancel();
			_input.ReleaseAll(_link);
			_driver.Exit();
			_overlay.Visible = false;
		}

		static void Notify(string text)
		{
			Log(text);
			var nm = NotificationManager.SharedInstance;
			if (nm != null) nm.PostNotification(new NotificationData(NotificationTarget.Player, text, 3f, false), false);
		}

		void OnGUI()
		{
			if (_overlay == null) return;
			// Take Minecraft's newest 2D frame as late as possible, right before it is drawn.
			if (_overlay.Visible && _link != null && Event.current.type == EventType.Repaint)
			{
				_partClock.Restart();
				_overlay.Update(_link);
				EndPart(3);
			}
			_overlay.Draw();
		}

		void OnDestroy()
		{
			LoadManager.OnCompleteSceneLoad -= OnSceneLoaded;
			try
			{
				if (_driver.Active) _driver.Exit();
			}
			catch (Exception e)
			{
				Debug.Log("[OWCraft] leaving Minecraft mode on quit: " + e.Message); // the player may already be gone
			}
			_collision?.Dispose();
			_blocks?.Dispose();
			_overlay?.Dispose();
			_link?.Dispose();
		}
	}
}
