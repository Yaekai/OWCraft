using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using OWCraft.Link;
using UnityEngine;
using UnityEngine.Rendering;

namespace OWCraft.World
{
	/// <summary>
	/// Draws Minecraft's chunk geometry as ordinary Unity meshes, parented to the planet the blocks
	/// belong to: they turn with it, get Outer Wilds' sun and shadows, and are visible from orbit.
	/// Minecraft's sections arrive pre-meshed (16^3 blocks, triangles, atlas UVs) on the render ring.
	/// </summary>
	public sealed class BlockRenderer : IDisposable
	{
		const long BytesPerFrame = 16L << 20;

		readonly Dictionary<(int, int, int), Section> _sections = new Dictionary<(int, int, int), Section>();
		Texture2D _atlas;
		bool _atlasDirty;
		Material _solid, _translucent;
		readonly SceneRenderer _scene;
		readonly LightRenderer _lights = new LightRenderer();

		public Material SolidMaterial => _solid;
		public Material TranslucentMaterial => _translucent;

		public int SectionCount => _sections.Count;
		public int LightCount => _lights.Count;
		public int SkippedSections { get; private set; }

		public BlockRenderer()
		{
			_scene = new SceneRenderer(this);
			PickShader();
		}

		// ---- materials --------------------------------------------------------------------------

		static readonly string[] SolidShaders =
		{
			"Particles/Standard Surface", // lit, vertex colour, alpha cutout
			"Outer Wilds/Particles/Cutoff", // the game's own: lit, vertex colour, alpha cutout
			"Standard",
			"Legacy Shaders/Transparent/Cutout/Diffuse",
			"Unlit/Transparent Cutout",
		};

		static Shader FindShader(params string[] names)
		{
			foreach (var name in names)
			{
				var s = Shader.Find(name);
				if (s != null && s.isSupported) return s;
				s = Resources.FindObjectsOfTypeAll<Shader>().FirstOrDefault(x => x.name == name);
				if (s != null && s.isSupported) return s;
			}
			return null;
		}

		/// <summary>
		/// Picks the best block shader that's loaded right now. At the title screen the game hasn't loaded
		/// its particle shaders yet, so this runs again after the solar system loads.
		/// </summary>
		public void PickShader()
		{
			if (OWCraft.Verbose)
			{
				var names = Resources.FindObjectsOfTypeAll<Shader>().Select(s => s.name).Distinct().OrderBy(n => n);
				OWCraft.Log("shaders loaded: " + string.Join(", ", names));
			}
			var shader = FindShader(SolidShaders);
			if (shader == null)
			{
				OWCraft.Log("no usable block shader found; blocks will be invisible");
				return;
			}
			if (_solid != null && _solid.shader == shader) return;
			OWCraft.Log("block shader: " + shader.name);
			if (shader.name != SolidShaders[0] && shader.name != SolidShaders[1])
			{
				// Most fallbacks ignore vertex colour (grass and leaf tint, ambient occlusion): say what else exists.
				var candidates = Resources.FindObjectsOfTypeAll<Shader>().Select(s => s.name)
					.Where(n => n.IndexOf("article", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("ertex", StringComparison.OrdinalIgnoreCase) >= 0)
					.Distinct().OrderBy(n => n);
				OWCraft.Log("(no vertex colours with this shader; loaded particle/vertex shaders: " + string.Join(", ", candidates) + ")");
				// What the game's own vertex-colour particle shaders take, to pick one that can replace Standard.
				foreach (var name in new[] { "Outer Wilds/Particles/Cutoff", "Outer Wilds/Particles/Alpha" })
				{
					var sh = Shader.Find(name);
					if (sh == null) continue;
					var props = new System.Collections.Generic.List<string>();
					for (int i = 0; i < sh.GetPropertyCount(); i++) props.Add(sh.GetPropertyName(i) + ":" + sh.GetPropertyType(i));
					OWCraft.Log($"shader {name}: queue {sh.renderQueue}, properties {string.Join(", ", props)}");
				}
			}

			if (_solid == null) _solid = new Material(shader) { name = "OWCraft blocks", hideFlags = HideFlags.DontUnloadUnusedAsset };
			else _solid.shader = shader;
			SetIfPresent(_solid, "_Mode", 1f);
			SetIfPresent(_solid, "_Cutoff", 0.5f);
			SetIfPresent(_solid, "_Glossiness", 0f);
			SetIfPresent(_solid, "_Metallic", 0f);
			SetIfPresent(_solid, "_ColorMode", 0f); // particles: multiply by vertex colour
			SetIfPresent(_solid, "_CameraFadeDist", 0f); // Outer Wilds particles fade out near the camera
			SetIfPresent(_solid, "_UseRingworldLighting", 0f);
			_solid.EnableKeyword("_ALPHATEST_ON");
			_solid.SetOverrideTag("RenderType", "TransparentCutout");
			_solid.renderQueue = (int)RenderQueue.AlphaTest;

			if (_translucent == null) _translucent = new Material(shader) { name = "OWCraft translucent blocks", hideFlags = HideFlags.DontUnloadUnusedAsset };
			else _translucent.shader = shader;
			SetIfPresent(_translucent, "_Mode", 2f);
			SetIfPresent(_translucent, "_Glossiness", 0.6f);
			SetIfPresent(_translucent, "_SrcBlend", (float)BlendMode.SrcAlpha);
			SetIfPresent(_translucent, "_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
			SetIfPresent(_translucent, "_ZWrite", 0f);
			_translucent.EnableKeyword("_ALPHABLEND_ON");
			_translucent.SetOverrideTag("RenderType", "Transparent");
			_translucent.renderQueue = (int)RenderQueue.Transparent;
			if (_atlas != null)
			{
				_solid.mainTexture = _atlas;
				_translucent.mainTexture = _atlas;
			}
		}

		static void SetIfPresent(Material m, string prop, float v)
		{
			if (m.HasProperty(prop)) m.SetFloat(prop, v);
		}

		// ---- render ring --------------------------------------------------------------------------

		public void Update(SharedLink link)
		{
			link.DrainRender(Handle, BytesPerFrame);
			_lights.Update();
			if (_atlasDirty && _atlas != null)
			{
				_atlas.Apply(false);
				_atlasDirty = false;
			}
		}

		void Handle(uint type, IntPtr p, int bytes)
		{
			try
			{
				switch (type)
				{
					case Proto.RenAtlas:
						OnAtlas(p, bytes);
						break;
					case Proto.RenAtlasRegion:
						OnAtlasRegion(p, bytes);
						break;
					case Proto.RenSection:
						OnSection(p, bytes);
						break;
					case Proto.RenClearAll:
						Clear();
						break;
					case Proto.RenTexture:
						_scene.OnTexture(p, bytes);
						break;
					case Proto.RenScene:
						_scene.OnScene(p, bytes);
						break;
					case Proto.RenLights:
						_lights.OnLights(p, bytes);
						break;
					// The player model: later.
				}
			}
			catch (Exception e)
			{
				OWCraft.LogOnce("render-" + type, $"render message {type}: {e}");
			}
		}

		unsafe void OnAtlas(IntPtr p, int bytes)
		{
			int w = *(int*)p, h = *(int*)(p + 4);
			if (w <= 0 || h <= 0 || bytes < 8 + (long)w * h * 4) return;
			if (_atlas == null || _atlas.width != w || _atlas.height != h)
			{
				if (_atlas != null) UnityEngine.Object.Destroy(_atlas);
				_atlas = new Texture2D(w, h, TextureFormat.RGBA32, false, false)
				{
					name = "OWCraft atlas",
					hideFlags = HideFlags.DontUnloadUnusedAsset,
					filterMode = FilterMode.Point,
					wrapMode = TextureWrapMode.Clamp,
					anisoLevel = 0,
				};
				if (_solid != null) _solid.mainTexture = _atlas;
				if (_translucent != null) _translucent.mainTexture = _atlas;
			}
			// Minecraft sends the top row first. Uploaded as is it lands upside down in Unity's terms, but
			// Minecraft's V and Unity's V then address the same rows, so UVs pass through unchanged.
			_atlas.LoadRawTextureData(p + 8, w * h * 4);
			_atlasDirty = true;
			OWCraft.Log($"atlas {w}x{h}");
		}

		unsafe void OnAtlasRegion(IntPtr p, int bytes)
		{
			if (_atlas == null) return;
			uint* hdr = (uint*)p;
			int x = (int)hdr[0], y = (int)hdr[1], w = (int)hdr[2], h = (int)hdr[3];
			if (w <= 0 || h <= 0 || x + w > _atlas.width || y + h > _atlas.height || bytes < 16 + (long)w * h * 4) return;
			var px = new Color32[w * h];
			byte* src = (byte*)(p + 16);
			for (int row = 0; row < h; row++)
			{
				for (int col = 0; col < w; col++)
				{
					byte* s = src + (row * w + col) * 4;
					px[row * w + col] = new Color32(s[0], s[1], s[2], s[3]);
				}
			}
			// Same storage order as the full atlas (top row first == Unity row 0), so no flip here either.
			_atlas.SetPixels32(x, y, w, h, px);
			_atlasDirty = true;
		}

		static readonly Vector3[] Normals =
		{
			Vector3.up, Vector3.down, Vector3.up, // no normal (particles): lit like a top face
			Vector3.forward, Vector3.back, // MC north (-Z) is Unity +Z; south is Unity -Z
			Vector3.left, Vector3.right,
		};

		/// <summary>RenVertex → Unity: Z flipped, atlas V unchanged, light baked into the colour.</summary>
		internal static unsafe void DecodeVertices(byte* v, int count, Vector3[] pos, Vector2[] uv, Color32[] col, Vector3[] nrm)
		{
			for (int i = 0; i < count; i++, v += Proto.RenVertexBytes)
			{
				float* f = (float*)v;
				pos[i] = new Vector3(f[0], f[1], -f[2]);
				uv[i] = new Vector2(f[3], f[4]); // atlas rows stored in Minecraft's order, so V maps 1:1
				byte* c = v + 20;
				uint light = *(uint*)(v + 24);
				uint flags = *(uint*)(v + 28);
				// Sky light dims caves and overhangs; block light (torches) brightens them again.
				float sky = (light >> 8) & 0xF, blk = light & 0xF;
				float l = Mathf.Lerp(0.25f, 1f, Mathf.Max(sky, blk) / 15f);
				col[i] = new Color32((byte)(c[0] * l), (byte)(c[1] * l), (byte)(c[2] * l), c[3]);
				int n = (int)((flags >> 4) & 7);
				nrm[i] = n < Normals.Length ? Normals[n] : Vector3.up;
			}
		}

		unsafe void OnSection(IntPtr p, int bytes)
		{
			int* hdr = (int*)p;
			int sx = hdr[0], sy = hdr[1], sz = hdr[2];
			int count = hdr[3];
			var key = (sx, sy, sz);
			if (count <= 0)
			{
				Remove(key);
				return;
			}
			if (bytes < 16 + (long)count * Proto.RenVertexBytes) return;
			var anchor = AnchorGrid.AnchorForMc(sx * 16 + 8, sz * 16 + 8);
			if (anchor == null)
			{
				SkippedSections++;
				Remove(key);
				return;
			}

			var pos = new Vector3[count];
			var uv = new Vector2[count];
			var col = new Color32[count];
			var nrm = new Vector3[count];
			DecodeVertices((byte*)(p + 16), count, pos, uv, col, nrm);
			// Triangle lists: keep whole triangles together (the flags are per face, so per triangle).
			var solidIdx = new List<int>(count);
			var transIdx = new List<int>();
			byte* fl = (byte*)(p + 16 + 28);
			for (int t = 0; t + 2 < count; t += 3)
			{
				uint flags = *(uint*)(fl + t * Proto.RenVertexBytes);
				var list = (flags & 2) != 0 ? transIdx : solidIdx;
				// Flipping Z mirrors the mesh, so reverse each triangle to keep its front face outward.
				list.Add(t);
				list.Add(t + 2);
				list.Add(t + 1);
			}

			if (!_sections.TryGetValue(key, out var section))
			{
				section = new Section { Key = key };
				_sections[key] = section;
			}
			var mesh = section.Mesh;
			if (mesh == null)
			{
				// Kept as an asset, not owned by the scene: an Outer Wilds time loop reloads the scene,
				// but Minecraft won't send the sections again.
				mesh = new Mesh { name = $"OWCraft section {sx},{sy},{sz}", hideFlags = HideFlags.DontUnloadUnusedAsset };
				mesh.MarkDynamic();
				section.Mesh = mesh;
			}
			mesh.Clear();
			mesh.indexFormat = count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
			mesh.vertices = pos;
			mesh.uv = uv;
			mesh.colors32 = col;
			mesh.normals = nrm;
			mesh.subMeshCount = transIdx.Count > 0 ? 2 : 1;
			mesh.SetTriangles(solidIdx, 0, false);
			if (transIdx.Count > 0) mesh.SetTriangles(transIdx, 1, false);
			mesh.RecalculateBounds();
			section.Translucent = transIdx.Count > 0;
			Place(section, anchor);
		}

		sealed class Section
		{
			public (int x, int y, int z) Key;
			public Mesh Mesh;
			public bool Translucent;
			public GameObject Go;
		}

		void Place(Section section, Anchor anchor)
		{
			var go = section.Go;
			if (go == null)
			{
				go = new GameObject(section.Mesh.name);
				go.AddComponent<MeshFilter>().sharedMesh = section.Mesh;
				var r = go.AddComponent<MeshRenderer>();
				r.shadowCastingMode = ShadowCastingMode.On;
				r.receiveShadows = true;
				section.Go = go;
			}
			var root = anchor.EnsureRoot();
			if (go.transform.parent != root) go.transform.SetParent(root, false);
			var (sx, sy, sz) = section.Key;
			go.transform.localPosition = new Vector3(sx * 16 - anchor.McX0, sy * 16 - AnchorGrid.Y0, -(sz * 16 - anchor.McZ0));
			go.transform.localRotation = Quaternion.identity;
			go.transform.localScale = Vector3.one;
			var renderer = go.GetComponent<MeshRenderer>();
			if (section.Translucent) renderer.sharedMaterials = new[] { _solid, _translucent };
			else renderer.sharedMaterial = _solid;
		}

		/// <summary>After a scene load: put every known section back on its (new) planet.</summary>
		public void Rebuild()
		{
			int placed = 0;
			foreach (var section in _sections.Values)
			{
				section.Go = null; // went with the old scene
				var (sx, _, sz) = section.Key;
				var anchor = AnchorGrid.AnchorForMc(sx * 16 + 8, sz * 16 + 8);
				if (anchor == null || section.Mesh == null) continue;
				Place(section, anchor);
				placed++;
			}
			_lights.Rebuild();
			OWCraft.Log($"rebuilt {placed} of {_sections.Count} sections, {_lights.Count} lights");
		}

		void Remove((int, int, int) key)
		{
			if (_sections.TryGetValue(key, out var section))
			{
				_sections.Remove(key);
				DestroySection(section);
			}
		}

		static void DestroySection(Section section)
		{
			if (section.Go != null) UnityEngine.Object.Destroy(section.Go);
			if (section.Mesh != null) UnityEngine.Object.Destroy(section.Mesh);
		}

		/// <summary>Drops every section (Minecraft changed world, or the scene reloaded).</summary>
		public void Clear()
		{
			_scene.Clear();
			_lights.Clear();
			foreach (var section in _sections.Values) DestroySection(section);
			_sections.Clear();
		}

		public void Dispose()
		{
			Clear();
			_scene.Dispose();
			_lights.Dispose();
			if (_atlas != null) UnityEngine.Object.Destroy(_atlas);
			if (_solid != null) UnityEngine.Object.Destroy(_solid);
			if (_translucent != null) UnityEngine.Object.Destroy(_translucent);
		}
	}
}
