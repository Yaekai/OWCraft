using System;
using System.Collections.Generic;
using OWCraft.Link;
using UnityEngine;
using UnityEngine.Rendering;

namespace OWCraft.World
{
	/// <summary>
	/// Everything in Minecraft that isn't a chunk block: mobs and animals, block entities (chests, beds,
	/// signs) and particles. Minecraft sends them as one posed mesh per frame (RenScene), in batches
	/// that each use the block atlas or one of its entity textures (RenTexture). We keep the latest
	/// one as a single mesh on the planet, like the sections.
	/// </summary>
	public sealed class SceneRenderer : IDisposable
	{
		readonly BlockRenderer _blocks;
		readonly Dictionary<uint, Texture2D> _textures = new Dictionary<uint, Texture2D>();
		readonly Dictionary<(uint, bool), Material> _materials = new Dictionary<(uint, bool), Material>();
		readonly Layer _sceneLayer = new Layer("OWCraft entities"), _avatarLayer = new Layer("OWCraft player body");
		bool _avatarLoaded;
		Shader _materialsShader; // the block shader the cached clones were made with

		public SceneRenderer(BlockRenderer blocks)
		{
			_blocks = blocks;
		}

		/// <summary>RenTexture: an entity texture (skin, armour, chest...), top row first like the atlas.</summary>
		public unsafe void OnTexture(IntPtr p, int bytes)
		{
			uint* hdr = (uint*)p;
			uint id = hdr[0];
			int w = (int)hdr[1], h = (int)hdr[2];
			if (w <= 0 || h <= 0 || bytes < 16 + (long)w * h * 4) return;
			if (!_textures.TryGetValue(id, out var tex) || tex == null || tex.width != w || tex.height != h)
			{
				if (tex != null) UnityEngine.Object.Destroy(tex);
				tex = new Texture2D(w, h, TextureFormat.RGBA32, false, false)
				{
					name = "OWCraft entity texture " + id,
					filterMode = FilterMode.Point,
					wrapMode = TextureWrapMode.Clamp,
					hideFlags = HideFlags.DontUnloadUnusedAsset,
				};
				_textures[id] = tex;
				ResetMaterials();
			}
			tex.LoadRawTextureData(p + 16, w * h * 4);
			tex.Apply(false);
		}

		/// <summary>RenScene: this frame's entities and particles, relative to an origin block.</summary>
		public unsafe void OnScene(IntPtr p, int bytes)
		{
			if (bytes < 32) return;
			double* origin = (double*)p;
			double ox = origin[0], oy = origin[1], oz = origin[2];
			var anchor = AnchorGrid.AnchorForMc((int)Math.Floor(ox), (int)Math.Floor(oz));
			if (anchor == null || !_sceneLayer.Load(this, p + 24, bytes - 24))
			{
				_sceneLayer.Hide();
				return;
			}
			_sceneLayer.Place(anchor, ox, oy, oz);
		}

		/// <summary>
		/// RenAvatar: the player's own body in Minecraft's third person (F5), relative to its feet. Empty
		/// in first person. Placed every frame at the feet we show (PlaceAvatar).
		/// </summary>
		public void OnAvatar(IntPtr p, int bytes)
		{
			_avatarLoaded = _avatarLayer.Load(this, p, bytes);
			if (!_avatarLoaded) _avatarLayer.Hide();
		}

		/// <summary>Every frame in Minecraft mode: the body at the player's feet, or hidden.</summary>
		public void PlaceAvatar(Anchor anchor, double x, double y, double z, bool show)
		{
			if (show && _avatarLoaded && anchor != null) _avatarLayer.Place(anchor, x, y, z);
			else _avatarLayer.Hide();
		}

		/// <summary>One posed mesh from Minecraft (the scene, or the player's body), in batches by texture.</summary>
		sealed class Layer
		{
			readonly string _name;
			Mesh _mesh;
			GameObject _go;
			MeshRenderer _renderer;
			Vector3[] _pos = new Vector3[0], _nrm = new Vector3[0];
			Vector2[] _uv = new Vector2[0];
			Color32[] _col = new Color32[0];
			readonly List<Vector3> _posList = new List<Vector3>(), _nrmList = new List<Vector3>();
			readonly List<Vector2> _uvList = new List<Vector2>();
			readonly List<Color32> _colList = new List<Color32>();
			readonly List<List<int>> _indexLists = new List<List<int>>();
			readonly List<Material> _materialList = new List<Material>();

			public Layer(string name)
			{
				_name = name;
			}

			/// <summary>Batch count, vertex count, batches, vertices. False if there's nothing to show.</summary>
			public unsafe bool Load(SceneRenderer owner, IntPtr p, int bytes)
			{
				if (bytes < 8) return false;
				uint* counts = (uint*)p;
				int batchCount = (int)counts[0], count = (int)counts[1];
				long vertexStart = 8 + (long)batchCount * 16;
				if (batchCount == 0 || count == 0) return false;
				if (bytes < vertexStart + (long)count * Proto.RenVertexBytes) return false;

				// Every frame: reuse the buffers, or the garbage collector stalls the game every few seconds.
				if (_pos.Length < count)
				{
					int cap = Mathf.NextPowerOfTwo(count);
					_pos = new Vector3[cap];
					_uv = new Vector2[cap];
					_col = new Color32[cap];
					_nrm = new Vector3[cap];
				}
				BlockRenderer.DecodeVertices((byte*)p + vertexStart, count, _pos, _uv, _col, _nrm);
				Fill(_posList, _pos, count);
				Fill(_uvList, _uv, count);
				Fill(_colList, _col, count);
				Fill(_nrmList, _nrm, count);

				if (_mesh == null)
				{
					_mesh = new Mesh { name = _name, hideFlags = HideFlags.DontUnloadUnusedAsset };
					_mesh.MarkDynamic();
				}
				_mesh.Clear();
				_mesh.indexFormat = count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
				_mesh.SetVertices(_posList);
				_mesh.SetUVs(0, _uvList);
				_mesh.SetColors(_colList);
				_mesh.SetNormals(_nrmList);

				uint* b = (uint*)(p + 8);
				_materialList.Clear();
				int used = 0;
				for (int i = 0; i < batchCount; i++, b += 4)
				{
					uint texture = b[0];
					int first = (int)b[1], n = (int)b[2];
					bool translucent = (b[3] & 1) != 0;
					if (first < 0 || n < 3 || first + n > count) continue;
					var mat = owner.MaterialFor(texture, translucent);
					if (mat == null) continue;
					if (used == _indexLists.Count) _indexLists.Add(new List<int>());
					var idx = _indexLists[used++];
					idx.Clear();
					for (int t = 0; t + 2 < n; t += 3)
					{
						// Mirrored by the Z flip, so reversed to keep front faces outward.
						idx.Add(first + t);
						idx.Add(first + t + 2);
						idx.Add(first + t + 1);
					}
					_materialList.Add(mat);
				}
				_mesh.subMeshCount = Math.Max(1, used);
				for (int i = 0; i < used; i++) _mesh.SetTriangles(_indexLists[i], i, false);
				_mesh.RecalculateBounds();

				if (_go == null)
				{
					_go = new GameObject(_name);
					_go.AddComponent<MeshFilter>().sharedMesh = _mesh;
					_renderer = _go.AddComponent<MeshRenderer>();
					_renderer.shadowCastingMode = ShadowCastingMode.On;
					_renderer.receiveShadows = true;
				}
				if (!SameMaterials(_renderer.sharedMaterials, _materialList)) _renderer.sharedMaterials = _materialList.ToArray();
				return true;
			}

			/// <summary>The mesh's origin at this Minecraft position, on the tile's root.</summary>
			public void Place(Anchor anchor, double x, double y, double z)
			{
				if (_go == null) return;
				var root = anchor.EnsureRoot();
				if (_go.transform.parent != root) _go.transform.SetParent(root, false);
				_go.transform.localPosition = new Vector3((float)(x - anchor.McX0), (float)(y - AnchorGrid.Y0), (float)-(z - anchor.McZ0));
				_go.transform.localRotation = Quaternion.identity;
				if (!_go.activeSelf) _go.SetActive(true);
			}

			public void Hide()
			{
				if (_go != null && _go.activeSelf) _go.SetActive(false);
			}

			public void Dispose()
			{
				if (_go != null) UnityEngine.Object.Destroy(_go);
				if (_mesh != null) UnityEngine.Object.Destroy(_mesh);
			}
		}

		static void Fill<T>(List<T> list, T[] src, int count)
		{
			list.Clear();
			for (int i = 0; i < count; i++) list.Add(src[i]);
		}

		static bool SameMaterials(Material[] current, List<Material> wanted)
		{
			if (current.Length != wanted.Count) return false;
			for (int i = 0; i < current.Length; i++)
			{
				if (current[i] != wanted[i]) return false;
			}
			return true;
		}

		Material MaterialFor(uint texture, bool translucent)
		{
			var template = translucent ? _blocks.TranslucentMaterial : _blocks.SolidMaterial;
			if (template == null) return null;
			if (_materialsShader != template.shader)
			{
				// PickShader swapped the block shader: the clones still have the old one.
				ResetMaterials();
				_materialsShader = template.shader;
			}
			if (texture == 0) return template; // the block/item atlas
			if (!_textures.TryGetValue(texture, out var tex) || tex == null) return null; // not arrived yet
			if (_materials.TryGetValue((texture, translucent), out var mat) && mat != null) return mat;
			mat = new Material(template) { name = $"OWCraft entity {texture}{(translucent ? " translucent" : "")}", hideFlags = HideFlags.DontUnloadUnusedAsset };
			mat.mainTexture = tex;
			_materials[(texture, translucent)] = mat;
			return mat;
		}

		void ResetMaterials()
		{
			foreach (var m in _materials.Values)
			{
				if (m != null) UnityEngine.Object.Destroy(m);
			}
			_materials.Clear();
		}

		void Hide()
		{
			_sceneLayer.Hide();
			_avatarLayer.Hide();
			_avatarLoaded = false;
		}

		/// <summary>Minecraft restarted: its texture ids start over.</summary>
		public void Clear()
		{
			Hide();
			ResetMaterials();
			foreach (var t in _textures.Values)
			{
				if (t != null) UnityEngine.Object.Destroy(t);
			}
			_textures.Clear();
		}

		public void Dispose()
		{
			Clear();
			_sceneLayer.Dispose();
			_avatarLayer.Dispose();
		}
	}
}
