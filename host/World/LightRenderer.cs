using System;
using System.Collections.Generic;
using UnityEngine;

namespace OWCraft.World
{
	/// <summary>
	/// Light-emitting blocks (torches, lanterns, lava, glowstone...) as real point lights on the planet.
	/// Minecraft sends them per 16x16x16 section (RenLights): block position, light level 1-15 and a
	/// colour + kind (steady, flame, lava). A section's list replaces the previous one.
	/// </summary>
	public sealed class LightRenderer : IDisposable
	{
		const int KindFlame = 1, KindLava = 2;

		struct BlockLight
		{
			public int X, Y, Z; // Minecraft block
			public int Level;
			public Color Color;
			public int Kind;
		}

		sealed class SectionLights
		{
			public BlockLight[] Lights;
			public readonly List<Light> Objects = new List<Light>();
		}

		readonly Dictionary<(int, int, int), SectionLights> _sections = new Dictionary<(int, int, int), SectionLights>();
		readonly List<(Light light, float baseIntensity, float seed)> _flickering = new List<(Light, float, float)>();

		public int Count { get; private set; }

		public unsafe void OnLights(IntPtr p, int bytes)
		{
			if (bytes < 16) return;
			int* hdr = (int*)p;
			int sx = hdr[0], sy = hdr[1], sz = hdr[2], count = hdr[3];
			var key = (sx, sy, sz);
			Remove(key);
			if (count <= 0 || bytes < 16 + (long)count * 8) return;
			var lights = new BlockLight[count];
			byte* e = (byte*)p + 16;
			for (int i = 0; i < count; i++, e += 8)
			{
				uint c = *(uint*)(e + 4);
				lights[i] = new BlockLight
				{
					X = sx * 16 + e[0],
					Y = sy * 16 + e[1],
					Z = sz * 16 + e[2],
					Level = e[3],
					Color = new Color((c & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, ((c >> 16) & 0xFF) / 255f),
					Kind = (int)((c >> 24) & 0x0F),
				};
			}
			var section = new SectionLights { Lights = lights };
			_sections[key] = section;
			Place(section);
		}

		void Place(SectionLights section)
		{
			foreach (var l in section.Lights)
			{
				var anchor = AnchorGrid.AnchorForMc(l.X, l.Z);
				if (anchor == null) continue;
				var go = new GameObject("OWCraft light");
				go.transform.SetParent(anchor.EnsureRoot(), false);
				go.transform.localPosition = new Vector3(l.X + 0.5f - anchor.McX0, l.Y + 0.5f - AnchorGrid.Y0, -(l.Z + 0.5f - anchor.McZ0));
				var light = go.AddComponent<Light>();
				light.type = LightType.Point;
				light.color = l.Color;
				// Minecraft light fades out over `level` blocks; a Unity point light over its range.
				light.range = Mathf.Max(2f, l.Level * 0.9f);
				light.intensity = 0.4f + l.Level / 15f * 1.4f;
				light.shadows = LightShadows.None;
				light.renderMode = LightRenderMode.Auto;
				section.Objects.Add(light);
				Count++;
				if (l.Kind == KindFlame || l.Kind == KindLava) _flickering.Add((light, light.intensity, UnityEngine.Random.value * 100f));
			}
		}

		/// <summary>Flames flicker a little.</summary>
		public void Update()
		{
			if (_flickering.Count == 0) return;
			float t = Time.time;
			for (int i = _flickering.Count - 1; i >= 0; i--)
			{
				var (light, baseIntensity, seed) = _flickering[i];
				if (light == null)
				{
					_flickering.RemoveAt(i);
					continue;
				}
				light.intensity = baseIntensity * (0.9f + 0.1f * Mathf.PerlinNoise(seed, t * 3f));
			}
		}

		void Remove((int, int, int) key)
		{
			if (!_sections.TryGetValue(key, out var section)) return;
			_sections.Remove(key);
			Destroy(section);
		}

		void Destroy(SectionLights section)
		{
			foreach (var light in section.Objects)
			{
				if (light != null) UnityEngine.Object.Destroy(light.gameObject);
				Count--;
			}
			section.Objects.Clear();
		}

		/// <summary>After a scene load: the old light objects went with the scene; put them back.</summary>
		public void Rebuild()
		{
			_flickering.Clear();
			Count = 0;
			foreach (var section in _sections.Values)
			{
				section.Objects.Clear();
				Place(section);
			}
		}

		public void Clear()
		{
			foreach (var section in _sections.Values) Destroy(section);
			_sections.Clear();
			_flickering.Clear();
			Count = 0;
		}

		public void Dispose() => Clear();
	}
}
