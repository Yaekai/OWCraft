using System;
using System.Collections.Generic;
using UnityEngine;

namespace OWCraft.World
{
	/// <summary>
	/// A flat patch of a planet that Minecraft treats as its world. Minecraft's world is flat with a
	/// fixed "down"; a planet is round and its down points at the centre. So each planet is tiled
	/// (an equi-angular cube sphere) and every tile gets a fixed tangent frame and a fixed, private
	/// area of the Minecraft world. Placed blocks therefore stay exactly where they were built, across
	/// tile changes, reloads and time loops.
	///
	/// Coordinates: Unity is left-handed (+Z forward), Minecraft right-handed (+Z south). Within an
	/// anchor, Unity anchor-local (x, y, z) == Minecraft (x - X0, y - Y0, -(z - Z0)).
	/// </summary>
	public sealed class Anchor
	{
		public PlanetInfo Planet;
		public int Face, I, J;
		public Vector3 LocalOrigin; // in the planet body's local space
		public Quaternion LocalRotation; // anchor-local -> body-local; +Y = away from the planet centre
		public int McX0, McZ0;
		public Transform Root; // created lazily; parented to the planet; draws this tile's blocks

		public OWRigidbody Body => Planet.Body;
		public Transform BodyTransform => Planet.Body.transform;

		public Vector3 McToBodyLocal(double x, double y, double z)
		{
			var q = new Vector3((float)(x - McX0), (float)(y - AnchorGrid.Y0), (float)-(z - McZ0));
			return LocalOrigin + LocalRotation * q;
		}

		public Vector3 McToWorld(double x, double y, double z) => BodyTransform.TransformPoint(McToBodyLocal(x, y, z));

		public void BodyLocalToMc(Vector3 p, out double x, out double y, out double z)
		{
			Vector3 q = Quaternion.Inverse(LocalRotation) * (p - LocalOrigin);
			x = McX0 + (double)q.x;
			y = AnchorGrid.Y0 + (double)q.y;
			z = McZ0 - (double)q.z;
		}

		public void WorldToMc(Vector3 world, out double x, out double y, out double z) =>
			BodyLocalToMc(BodyTransform.InverseTransformPoint(world), out x, out y, out z);

		/// <summary>Anchor frame in world space (its up is "up" for Minecraft here).</summary>
		public Quaternion WorldRotation => BodyTransform.rotation * LocalRotation;

		public Transform EnsureRoot()
		{
			if (Root == null)
			{
				var go = new GameObject($"OWCraft Anchor {Planet.Name} {Face}/{I}/{J}");
				Root = go.transform;
				Root.SetParent(BodyTransform, false);
				Root.localPosition = LocalOrigin;
				Root.localRotation = LocalRotation;
			}
			return Root;
		}

		public override string ToString() => $"{Planet.Name} face {Face} tile {I},{J} (MC {McX0},{McZ0})";
	}

	public sealed class PlanetInfo
	{
		public OWRigidbody Body;
		public string Name;
		public int Slot; // which band of the Minecraft world (Z) belongs to this planet
		public float Radius; // the reference sphere the anchors sit on
		public int TilesPerFaceEdge;
		public readonly Dictionary<int, Anchor> Anchors = new Dictionary<int, Anchor>();

		public float TileSize => Radius * Mathf.PI * 0.5f / TilesPerFaceEdge;
	}

	public static class AnchorGrid
	{
		public const int Y0 = 192; // Minecraft Y of the reference sphere (world height is -64..320)
		public const int Spacing = 512; // Minecraft blocks between tile origins: never within render distance of each other
		public const int Base = 16384;
		public const int MaxTiles = 64;
		const float TargetTileMetres = 40f;

		static readonly Dictionary<OWRigidbody, PlanetInfo> Planets = new Dictionary<OWRigidbody, PlanetInfo>();
		static readonly Dictionary<int, PlanetInfo> BySlot = new Dictionary<int, PlanetInfo>();

		public static void Reset()
		{
			Planets.Clear();
			BySlot.Clear();
		}

		/// <summary>Registers every planet/moon in the scene so blocks on any of them can be drawn.</summary>
		public static void ScanScene()
		{
			foreach (var astro in UnityEngine.Object.FindObjectsOfType<AstroObject>())
			{
				var body = astro.GetOWRigidbody();
				if (body != null) Get(body);
			}
		}

		/// <summary>The planet info for a body the player can stand on, or null for the ship and the like.</summary>
		public static PlanetInfo Get(OWRigidbody body)
		{
			if (body == null) return null;
			if (Planets.TryGetValue(body, out var info)) return info;
			if (body == Locator.GetShipBody() || body == Locator.GetPlayerBody() || body == Locator.GetProbe()?.GetOWRigidbody()) return null;

			var astro = body.GetComponent<AstroObject>();
			string name;
			int slot;
			if (astro != null && astro.GetAstroObjectName() != AstroObject.Name.None && astro.GetAstroObjectName() != AstroObject.Name.CustomString)
			{
				name = astro.GetAstroObjectName().ToString();
				slot = (int)astro.GetAstroObjectName();
			}
			else
			{
				name = astro != null && !string.IsNullOrEmpty(astro.GetCustomName()) ? astro.GetCustomName() : body.gameObject.name;
				slot = 32 + (int)(StableHash(name) % 224u);
			}
			float radius = 0f;
			var gravity = astro != null ? astro.GetGravityVolume() : body.GetAttachedGravityVolume();
			if (gravity != null)
			{
				radius = HarmonyLib.Traverse.Create(gravity).Field("_upperSurfaceRadius").GetValue<float>();
			}
			if (radius < 5f) radius = 100f; // islands and props without gravity of their own

			while (BySlot.ContainsKey(slot)) slot = 32 + (slot - 32 + 1) % 224;
			info = new PlanetInfo
			{
				Body = body,
				Name = name,
				Slot = slot,
				Radius = radius,
				TilesPerFaceEdge = Mathf.Clamp(Mathf.RoundToInt(radius * Mathf.PI * 0.5f / TargetTileMetres), 1, MaxTiles),
			};
			Planets[body] = info;
			BySlot[slot] = info;
			OWCraft.Log($"planet {name}: slot {slot}, radius {radius:F0} m, {info.TilesPerFaceEdge} tiles per face edge ({info.TileSize:F0} m)");
			return info;
		}

		static uint StableHash(string s)
		{
			uint h = 2166136261;
			foreach (char c in s)
			{
				h = (h ^ c) * 16777619;
			}
			return h;
		}

		/// <summary>The tile under a body-local position.</summary>
		public static Anchor TileAt(PlanetInfo planet, Vector3 bodyLocal)
		{
			Vector3 d = bodyLocal.sqrMagnitude > 1e-6f ? bodyLocal.normalized : Vector3.up;
			int axis = 0;
			for (int k = 1; k < 3; k++)
			{
				if (Mathf.Abs(d[k]) > Mathf.Abs(d[axis])) axis = k;
			}
			int face = axis * 2 + (d[axis] < 0 ? 1 : 0);
			float major = Mathf.Abs(d[axis]);
			int ua = (axis + 1) % 3, va = (axis + 2) % 3;
			int n = planet.TilesPerFaceEdge;
			int i = TileIndex(Mathf.Atan(d[ua] / major), n);
			int j = TileIndex(Mathf.Atan(d[va] / major), n);
			return GetAnchor(planet, face, i, j);
		}

		static int TileIndex(float angle, int n) =>
			Mathf.Clamp(Mathf.FloorToInt((angle + Mathf.PI * 0.25f) / (Mathf.PI * 0.5f) * n), 0, n - 1);

		static float TileCentre(int index, int n) => Mathf.Tan(-Mathf.PI * 0.25f + (index + 0.5f) * (Mathf.PI * 0.5f) / n);

		public static Anchor GetAnchor(PlanetInfo planet, int face, int i, int j)
		{
			int key = (face * MaxTiles + i) * MaxTiles + j;
			if (planet.Anchors.TryGetValue(key, out var anchor)) return anchor;

			int axis = face / 2;
			float sign = face % 2 == 0 ? 1f : -1f;
			int ua = (axis + 1) % 3, va = (axis + 2) % 3;
			Vector3 p = Vector3.zero;
			p[axis] = sign;
			p[ua] = TileCentre(i, planet.TilesPerFaceEdge);
			p[va] = TileCentre(j, planet.TilesPerFaceEdge);
			Vector3 up = p.normalized;
			Vector3 axisV = Vector3.zero;
			axisV[va] = 1f;
			Vector3 forward = (axisV - Vector3.Dot(axisV, up) * up).normalized;
			anchor = new Anchor
			{
				Planet = planet,
				Face = face,
				I = i,
				J = j,
				LocalOrigin = up * planet.Radius,
				LocalRotation = Quaternion.LookRotation(forward, up),
				McX0 = Base + (face * MaxTiles + i) * Spacing,
				McZ0 = Base + (planet.Slot * MaxTiles + j) * Spacing,
			};
			planet.Anchors[key] = anchor;
			return anchor;
		}

		/// <summary>The anchor whose Minecraft area contains this block column (null outside every planet's area).</summary>
		public static Anchor AnchorForMc(int x, int z)
		{
			int col = FloorDiv(x - Base + Spacing / 2, Spacing);
			int row = FloorDiv(z - Base + Spacing / 2, Spacing);
			if (col < 0 || row < 0) return null;
			int face = col / MaxTiles, i = col % MaxTiles;
			int slot = row / MaxTiles, j = row % MaxTiles;
			if (face >= 6 || !BySlot.TryGetValue(slot, out var planet) || planet.Body == null) return null;
			if (i >= planet.TilesPerFaceEdge || j >= planet.TilesPerFaceEdge) return null;
			return GetAnchor(planet, face, i, j);
		}

		public static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);
	}
}
