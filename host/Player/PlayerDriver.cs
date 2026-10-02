using System;
using System.Diagnostics;
using HarmonyLib;
using OWCraft.Link;
using OWCraft.World;
using UnityEngine;

namespace OWCraft.Player
{
	/// <summary>
	/// Minecraft mode: Minecraft simulates the player (walking, jumping, breaking and placing) against the
	/// planet's collision, and the Outer Wilds player becomes a puppet that follows Minecraft's feet. The
	/// Outer Wilds body is pinned to the planet (kinematic, parented) so its own physics stays out of it.
	/// </summary>
	public sealed class PlayerDriver
	{
		/// <summary>Moving this far (fraction of a tile) from the anchor into another tile switches tile.</summary>
		const float ReanchorFraction = 0.6f;

		public bool Active { get; private set; }
		public Anchor Anchor { get; private set; }
		public uint TeleportSeq { get; private set; }
		public uint CollisionEpoch { get; private set; }
		public float Yaw, Pitch; // Minecraft degrees, authoritative (we integrate the mouse)
		public double X, Y, Z; // Minecraft feet: where the host says the player is

		InputMode _previousInputMode;
		float _feetOffset; // player transform origin above the feet, metres
		bool _waitingForTeleport;
		Vector3 _holdLocal; // body-local player position while Minecraft is still loading the teleport
		Quaternion _holdRotation;
		// The view, kept upright on the round planet (not on the flat tile, which tilts up to a few
		// degrees away from it): Minecraft's yaw/pitch are derived from it every frame.
		Quaternion _heading; // player rotation in the planet body's local space
		float _lookPitch; // degrees, + looks down (Minecraft's convention)

		public static bool ScreenOpen; // read by the pause patch
		public static OWRigidbody PinnedTo; // read by the velocity patch
		public static Vector3 PinnedRelativeVelocity; // Minecraft's own motion, world space
		public static float MaxVelocityCorrection; // how wrong the game's own player velocity was (diagnostics)

		readonly System.Collections.Generic.List<Collider> _solidColliders = new System.Collections.Generic.List<Collider>();
		RigidbodyInterpolation _previousInterpolation;
		Collider _groundCollider;
		static long _qpcFrequency;
		public static double TickAgeMs; // diagnostics, worst since the last stats line: should stay within 0..~50
		public static int LateTickFrames; // diagnostics: frames that had no newer Minecraft tick to move towards
		float _eye = 1.62f; // Minecraft's eye height at the shown moment

		// Minecraft's ticks, as a timeline: a tick reaches us up to one Minecraft frame after its stamp
		// (it's published at the end of the frame it ran in), so interpolating "previous -> latest" ran
		// out of tick on 10-20% of frames: the view held, then jumped. Showing the timeline a little
		// later than the latest stamp keeps a newer tick in hand.
		struct TickSample
		{
			public long Qpc;
			public double X, Y, Z;
			public float Eye;
		}

		/// <summary>How much later than "one tick behind" the timeline is shown (a Minecraft frame and a bit).</summary>
		const double ExtraDelayMs = 20.0;
		readonly TickSample[] _ticks = new TickSample[8];
		int _tickCount;
		long _lastTickQpc;

		[System.Runtime.InteropServices.DllImport("kernel32.dll")]
		static extern bool QueryPerformanceCounter(out long count);

		[System.Runtime.InteropServices.DllImport("kernel32.dll")]
		static extern bool QueryPerformanceFrequency(out long frequency);

		/// <summary>Why Minecraft mode can't start right now, or null.</summary>
		public string CanEnter(SharedLink link)
		{
			var controller = Locator.GetPlayerController();
			if (controller == null || Locator.GetPlayerBody() == null) return "no player";
			if (!link.GuestAlive) return "Minecraft isn't connected (start it with the OWCraft link)";
			if (PlayerState.IsDead()) return "you're dead";
			if (!controller.IsGrounded()) return "stand on a planet first";
			if (AnchorGrid.Get(controller.GetGroundBody()) == null) return "you can't build on this (stand on a planet or moon)";
			return null;
		}

		public void Enter(CollisionStreamer streamer)
		{
			var controller = Locator.GetPlayerController();
			var body = Locator.GetPlayerBody();
			var planet = AnchorGrid.Get(controller.GetGroundBody());
			Transform player = body.transform;

			Vector3 up = (player.position - planet.Body.transform.position).normalized;
			_feetOffset = Mathf.Clamp(Vector3.Dot(player.position - controller.GetGroundContactPoint(), up), 0f, 3f);
			Vector3 feet = player.position - up * _feetOffset;
			Anchor = AnchorGrid.TileAt(planet, planet.Body.transform.InverseTransformPoint(feet));
			Anchor.WorldToMc(feet, out X, out Y, out Z);
			Y += 0.01; // never start inside the ground
			_heading = Quaternion.Inverse(planet.Body.transform.rotation) * player.rotation;
			var lookCam = Locator.GetPlayerCamera();
			_lookPitch = lookCam != null ? -Mathf.Asin(Mathf.Clamp(Vector3.Dot(lookCam.transform.forward, player.up), -1f, 1f)) * Mathf.Rad2Deg : 0f;
			if (lookCam != null) McLookFrom(lookCam.transform.forward);

			// Pinned the way the game's own seats do it (PlayerAttachPoint): kinematic and parented to the
			// planet. Not OWRigidbody.Suspend, which also turns off the player's detectors, so the game
			// thinks you're in a vacuum and you suffocate.
			body.MakeKinematic();
			body.transform.parent = planet.Body.transform;
			PinnedTo = planet.Body;
			var ground = Traverse.Create(controller).Field("_groundCollider").GetValue<Collider>();
			controller.LockMovement(true);
			controller.SetColliderActivation(false);
			_groundCollider = ground;
			StandOn(controller, planet.Body);
			// Every other solid collider too: the planet is a moving rigidbody, so a pinned player body
			// brushing its ground counts as an impact, and hard enough ones kill. Triggers (oxygen, hazard
			// and fluid detectors) stay on.
			_solidColliders.Clear();
			foreach (var c in body.GetComponentsInChildren<Collider>())
			{
				if (c.isTrigger || !c.enabled) continue;
				c.enabled = false;
				_solidColliders.Add(c);
			}
			// We place the body every frame; physics interpolation would drag it back to the last
			// physics step and make the motion stutter.
			var rb = body.GetComponent<Rigidbody>();
			if (rb != null)
			{
				_previousInterpolation = rb.interpolation;
				rb.interpolation = RigidbodyInterpolation.None;
			}
			_previousInputMode = OWInput.GetInputMode();
			OWInput.ChangeInputMode(InputMode.None);
			var cam = Locator.GetPlayerCameraController();
			if (cam != null) Traverse.Create(cam).Field("_degreesX").SetValue(0f);

			Active = true;
			Teleport(streamer);
			float naturalEye = lookCam != null ? Vector3.Dot(lookCam.transform.position - feet, up) : 0f;
			OWCraft.Log($"Minecraft mode on: {Anchor}, feet ({X:F1}, {Y:F1}, {Z:F1}), feet offset {_feetOffset:F2} m, the game's own eye height {naturalEye:F2} m");
		}

		public void Exit()
		{
			if (!Active) return;
			Active = false;
			ScreenOpen = false;
			PinnedTo = null;
			PinnedRelativeVelocity = Vector3.zero;
			var controller = Locator.GetPlayerController();
			var body = Locator.GetPlayerBody();
			foreach (var c in _solidColliders)
			{
				if (c != null) c.enabled = true;
			}
			_solidColliders.Clear();
			if (body != null)
			{
				var rb = body.GetComponent<Rigidbody>();
				if (rb != null) rb.interpolation = _previousInterpolation;
				body.transform.parent = null;
				body.MakeNonKinematic();
				if (Anchor?.Body != null) body.SetVelocity(Anchor.Body.GetPointVelocity(body.transform.position));
			}
			if (controller != null)
			{
				controller.SetColliderActivation(true);
				controller.UnlockMovement();
			}
			if (OWInput.IsInputMode(InputMode.None)) OWInput.ChangeInputMode(_previousInputMode == InputMode.None ? InputMode.Character : _previousInputMode);
			OWCraft.Log("Minecraft mode off");
		}

		/// <summary>
		/// Locked movement means "not grounded" to the game, and the animator plays the fall. The
		/// controller doesn't update grounding while locked, so we can say it's standing on the planet.
		/// Needs the ground collider (HasGroundControl reads its friction); without one, leave it.
		/// </summary>
		void StandOn(PlayerCharacterController controller, OWRigidbody planet)
		{
			if (controller == null) return;
			var t = Traverse.Create(controller);
			bool ok = _groundCollider != null;
			t.Field("_isGrounded").SetValue(ok);
			if (!ok) return;
			t.Field("_groundBody").SetValue(planet);
			t.Field("_groundCollider").SetValue(_groundCollider);
		}

		/// <summary>Tells Minecraft to put its player at X/Y/Z (new tile, or start) on a fresh collision store.</summary>
		public void Resync(CollisionStreamer streamer) => Teleport(streamer);

		void Teleport(CollisionStreamer streamer)
		{
			TeleportSeq++;
			CollisionEpoch++;
			streamer.Reset(CollisionEpoch);
			_waitingForTeleport = true;
			_tickCount = 0; // don't glide between the old and new place
			var body = Locator.GetPlayerBody();
			_holdLocal = body.transform.localPosition;
			_holdRotation = body.transform.localRotation;
		}

		/// <summary>Minecraft's yaw/pitch for a world-space view direction, in the current tile's frame.</summary>
		void McLookFrom(Vector3 forward)
		{
			Vector3 f = Quaternion.Inverse(Anchor.WorldRotation) * forward; // anchor-local
			// Straight up or down the view says nothing about yaw; the body still does (it's what you walk by).
			var body = Locator.GetPlayerBody();
			Vector3 h = f.x * f.x + f.z * f.z < 0.0025f && body != null ? Quaternion.Inverse(Anchor.WorldRotation) * body.transform.forward : f;
			Yaw = YawFromLocal(h);
			Pitch = Mathf.Clamp(-Mathf.Asin(Mathf.Clamp(f.y, -1f, 1f)) * Mathf.Rad2Deg, -90f, 90f);
		}

		// Minecraft's forward for yaw is (-sin, 0, cos) in its space, i.e. (-sin, 0, -cos) in anchor-local
		// Unity space (Z flipped): Unity heading = Minecraft yaw + 180.
		static float YawFromLocal(Vector3 f) => Mathf.Repeat(Mathf.Atan2(-f.x, -f.z) * Mathf.Rad2Deg, 360f);

		/// <summary>Mouse look, with Minecraft's sensitivity curve (so it feels like Minecraft).</summary>
		public void Look(Vector2 mouseDelta, float sensitivity)
		{
			float s = sensitivity * 0.6f + 0.2f;
			float f = s * s * s * 8f * 0.15f;
			_heading = _heading * Quaternion.Euler(0f, mouseDelta.x * f, 0f);
			_lookPitch = Mathf.Clamp(_lookPitch + mouseDelta.y * f, -90f, 90f);
		}

		/// <summary>Once per frame while active: follow Minecraft. False if Minecraft mode had to end.</summary>
		public bool Update(in SharedLink.GuestState mc, bool haveState, CollisionStreamer streamer)
		{
			var body = Locator.GetPlayerBody();
			if (body == null || Anchor?.Body == null || PlayerState.IsDead()) return false;
			ScreenOpen = haveState && mc.Has(Proto.McScreenOpen);
			Transform player = body.transform;

			if (_waitingForTeleport)
			{
				// Minecraft holds its player until it has collision under it; so do we.
				player.localPosition = _holdLocal;
				player.localRotation = _holdRotation;
				if (haveState && mc.TeleportAck == TeleportSeq) _waitingForTeleport = false;
				else return true;
			}
			if (!haveState) return true;

			// Follow Minecraft's 20 Hz ticks on our own frame clock, like Minecraft's renderer does.
			double fx = mc.X, fy = mc.Y, fz = mc.Z;
			_eye = mc.EyeHeight > 0f ? mc.EyeHeight : 1.62f;
			PinnedRelativeVelocity = Vector3.zero;
			if (mc.TickQpc != 0 && mc.TickMs > 0)
			{
				// Minecraft stamps ticks with QueryPerformanceCounter. Not Stopwatch: Mono's counts 100 ns
				// units from its own start, so the age came out as nonsense and the motion moved in 20 Hz steps.
				QueryPerformanceCounter(out long now);
				if (_qpcFrequency == 0) QueryPerformanceFrequency(out _qpcFrequency);
				double qpcPerMs = _qpcFrequency / 1000.0;
				if (mc.TickQpc != _lastTickQpc)
				{
					_lastTickQpc = mc.TickQpc;
					var cur = new TickSample { Qpc = mc.TickQpc, X = mc.CurX, Y = mc.CurY, Z = mc.CurZ, Eye = mc.TickEye > 0f ? mc.TickEye : _eye };
					if (_tickCount > 0)
					{
						var last = _ticks[_tickCount - 1];
						double jx = cur.X - last.X, jy = cur.Y - last.Y, jz = cur.Z - last.Z;
						// A teleport, a respawn, or ticks we missed: start the timeline over.
						if (jx * jx + jy * jy + jz * jz > 8.0 * 8.0 || cur.Qpc <= last.Qpc || (cur.Qpc - last.Qpc) / qpcPerMs > mc.TickMs * 4) _tickCount = 0;
					}
					if (_tickCount == 0)
					{
						_ticks[0] = new TickSample { Qpc = mc.TickQpc - (long)(mc.TickMs * qpcPerMs), X = mc.PrevX, Y = mc.PrevY, Z = mc.PrevZ, Eye = mc.TickEyeO > 0f ? mc.TickEyeO : cur.Eye };
						_tickCount = 1;
					}
					if (_tickCount == _ticks.Length)
					{
						Array.Copy(_ticks, 1, _ticks, 0, _ticks.Length - 1);
						_tickCount--;
					}
					_ticks[_tickCount++] = cur;
				}
				double ageMs = (now - _ticks[_tickCount - 1].Qpc) / qpcPerMs;
				TickAgeMs = Math.Max(TickAgeMs, ageMs);
				// The moment shown: one tick behind the latest stamp, like before, plus the arrival margin.
				long show = now - (long)((mc.TickMs + ExtraDelayMs) * qpcPerMs);
				int i = _tickCount - 2;
				while (i > 0 && _ticks[i].Qpc > show) i--;
				TickSample a = _ticks[Math.Max(i, 0)], b = _ticks[Math.Min(i + 1, _tickCount - 1)];
				double span = b.Qpc - a.Qpc;
				double t = span > 0 ? (show - a.Qpc) / span : 1.0;
				if (t > 1.0) LateTickFrames++; // still no newer tick to move towards
				t = Math.Max(0.0, Math.Min(1.0, t));
				fx = a.X + (b.X - a.X) * t;
				fy = a.Y + (b.Y - a.Y) * t;
				fz = a.Z + (b.Z - a.Z) * t;
				_eye = Mathf.Lerp(a.Eye, b.Eye, (float)t);
				if (span > 0)
				{
					// Minecraft's walking speed, for the Hearthian's walk/run animation.
					float perSecond = (float)(_qpcFrequency / span);
					var mcVel = new Vector3((float)(b.X - a.X), (float)(b.Y - a.Y), (float)-(b.Z - a.Z)) * perSecond;
					PinnedRelativeVelocity = mcVel.sqrMagnitude < 30f * 30f ? Anchor.WorldRotation * mcVel : Vector3.zero;
				}
			}
			StandOn(Locator.GetPlayerController(), Anchor.Body);
			if (Math.Abs(fx - Anchor.McX0) > AnchorGrid.Spacing / 2 || Math.Abs(fz - Anchor.McZ0) > AnchorGrid.Spacing / 2)
			{
				// Minecraft put its player somewhere else (respawn, /tp): that's not on this planet. Back to
				// where we were.
				OWCraft.Log($"Minecraft moved the player off this tile ({fx:F0}, {fy:F0}, {fz:F0}); bringing it back");
				Resync(streamer);
				return true;
			}
			X = fx;
			Y = fy;
			Z = fz;

			Vector3 feet = Anchor.McToWorld(fx, fy, fz);
			// Upright on the planet: the heading follows the local "up" as we walk around the curve.
			Transform planetT = Anchor.BodyTransform;
			Vector3 radialUp = planetT.InverseTransformPoint(feet).normalized;
			_heading = Quaternion.FromToRotation(_heading * Vector3.up, radialUp) * _heading;
			Quaternion rot = planetT.rotation * _heading;
			player.SetPositionAndRotation(feet + rot * Vector3.up * _feetOffset, rot);
			var camController = Locator.GetPlayerCameraController();
			if (camController != null) camController.SetDegreesY(Mathf.Clamp(-_lookPitch, -80f, 80f));
			var cam = Locator.GetPlayerCamera();
			if (cam != null)
			{
				// The game stops at 80 degrees; Minecraft lets you look straight down (to pillar up).
				// Our Update runs after the camera's FixedUpdate, so this is what gets drawn.
				cam.transform.localRotation = Quaternion.AngleAxis(_lookPitch, Vector3.right);
				// The camera exactly at Minecraft's eye, so what you aim at is what Minecraft hits.
				float eye = _eye;
				player.position += Anchor.McToWorld(fx, fy + eye, fz) - cam.transform.position;
				McLookFrom(cam.transform.forward);
			}

			MaybeReanchor(feet, streamer);
			return true;
		}

		/// <summary>
		/// Minecraft's world is flat; the planet isn't. Walking far enough moves us to the next tile, whose
		/// own flat patch fits the planet there: same world position, new Minecraft coordinates.
		/// </summary>
		void MaybeReanchor(Vector3 feetWorld, CollisionStreamer streamer)
		{
			double dx = X - Anchor.McX0, dz = Z - Anchor.McZ0;
			float limit = Anchor.Planet.TileSize * ReanchorFraction;
			if (dx * dx + dz * dz < limit * limit) return;
			var planet = Anchor.Planet;
			var next = AnchorGrid.TileAt(planet, planet.Body.transform.InverseTransformPoint(feetWorld));
			if (next == Anchor) return;

			var old = Anchor;
			Anchor = next;
			Anchor.WorldToMc(feetWorld, out X, out Y, out Z);
			Y += 0.05;
			var cam = Locator.GetPlayerCamera();
			if (cam != null) McLookFrom(cam.transform.forward);
			Teleport(streamer);
			OWCraft.Log($"tile change {old} -> {Anchor}");
		}

		/// <summary>Diagnostics: how far Minecraft's feet are above the game's own ground (negative: sunk in).</summary>
		public string GroundGap()
		{
			if (!Active || Anchor?.Body == null) return "n/a";
			Vector3 up = Anchor.WorldRotation * Vector3.up;
			Vector3 from = Anchor.McToWorld(X, Y, Z) + up * 1.5f;
			if (!Physics.Raycast(from, -up, out var hit, 6f, OWLayerMask.physicalMask, QueryTriggerInteraction.Ignore)) return "no ground below";
			return $"{hit.distance - 1.5f:F2} m ({hit.collider.name})";
		}

		/// <summary>The host state Minecraft follows this frame.</summary>
		public void Fill(ref SharedLink.HostState s)
		{
			s.X = X;
			s.Y = Y;
			s.Z = Z;
			s.Yaw = Yaw;
			s.Pitch = Pitch;
			s.TeleportSeq = TeleportSeq;
			s.CollisionEpoch = CollisionEpoch;
		}
	}

	/// <summary>
	/// While pinned, the player moves with the planet. The game reads the player's velocity from its
	/// own (now kinematic) rigidbody, which reports zero in a universe where the planet orbits and spins:
	/// you'd seem to race past the ground. That's the constant falling wind, and HighSpeedImpactSensor
	/// raycasts along it and kills you when it meets terrain. Planets move by KinematicRigidbody, so
	/// their Unity rigidbody reports zero too; OWRigidbody.GetPointVelocity has the real value.
	/// </summary>
	[HarmonyPatch(typeof(PlayerBody), nameof(PlayerBody.ManagedFixedUpdate), new[] { typeof(float), typeof(Vector3) })]
	static class PinnedVelocityPatch
	{
		static void Postfix(PlayerBody __instance, float invFixedDeltaTime, ref Vector3 ____currentVelocity, ref Vector3 ____lastVelocity, ref Vector3 ____currentAccel)
		{
			var planet = PlayerDriver.PinnedTo;
			if (planet == null) return;
			Vector3 v = planet.GetPointVelocity(__instance.transform.position) + PlayerDriver.PinnedRelativeVelocity;
			PlayerDriver.MaxVelocityCorrection = Mathf.Max(PlayerDriver.MaxVelocityCorrection, (____currentVelocity - v).magnitude);
			____currentVelocity = v;
			____currentAccel = (v - ____lastVelocity) * invFixedDeltaTime;
		}
	}

	/// <summary>While a Minecraft screen is open, Escape closes it instead of pausing Outer Wilds.</summary>
	[HarmonyPatch(typeof(PauseCommandListener), "Update")]
	static class PausePatch
	{
		static bool Prefix() => !PlayerDriver.ScreenOpen;
	}
}
