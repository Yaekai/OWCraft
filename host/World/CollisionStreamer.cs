using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using OWCraft.Link;
using UnityEngine;

namespace OWCraft.World
{
	/// <summary>
	/// Streams the planet's collision around the player to Minecraft, region by region (8x8x8 blocks):
	/// exact triangles for the player's smooth collider, then 1/8-block voxels for everything else.
	/// Same scheme as SkyCraft's SKSE Collision.cpp (MIT); the voxelizer is a port of it.
	///
	/// Main thread: picks regions, asks Unity's physics what's there and turns it into triangles in
	/// Minecraft space. Worker thread: voxelizes and writes the collision ring.
	/// </summary>
	public sealed class CollisionStreamer : IDisposable
	{
		const int RegionSize = Proto.RegionSize;
		const int Grid = RegionSize * 8; // voxels per region edge
		const int Radius = 5, Below = 3, Above = 2;
		const float RefreshNearSeconds = 1f; // sand, crumbling ground, moving parts
		const int MaxHarvestsPerFrame = 3;
		const double FrameBudgetMs = 2.5;
		const float SteepMin = 0.1f, SteepMax = 0.643f;
		const float SampleStep = 0.5f; // blocks between height samples on colliders we can't read

		struct Tri
		{
			public Vector3 A, B, C;
		}

		sealed class Job
		{
			public int Rx, Ry, Rz;
			public uint Epoch;
			public bool Clear;
			public List<Tri> Tris;
		}

		readonly SharedLink _link;
		readonly BlockingCollection<Job> _queue = new BlockingCollection<Job>();
		readonly Thread _worker;
		readonly List<Vector3Int> _offsets = new List<Vector3Int>();
		readonly Dictionary<long, float> _harvested = new Dictionary<long, float>();
		readonly Dictionary<Mesh, MeshData> _meshes = new Dictionary<Mesh, MeshData>();
		readonly Collider[] _hits = new Collider[512];
		readonly HashSet<Collider> _ignored = new HashSet<Collider>();
		readonly Stopwatch _clock = new Stopwatch();
		uint _epoch;
		volatile bool _disposed;

		public int RegionsSent { get; private set; }
		public int UnreadableMeshes { get; private set; }

		// Diagnostics, logged every few seconds while Minecraft mode is on (main thread only).
		int _statHarvests, _statHits, _statOwned, _statTris, _statEmpty;
		float _statPhysOffset;

		/// <summary>What harvesting has done since the last call, as one log line.</summary>
		public string TakeStats()
		{
			string s = $"collision: {_statHarvests} regions harvested ({_statEmpty} with no ground), {_statHits} colliders overlapped, " +
				$"{_statOwned} on the planet, {_statTris} triangles, {UnreadableMeshes} unreadable meshes so far, " +
				$"physics/transform offset {_statPhysOffset:F2} m, {RegionsSent} regions sent in total, side samples {_statSideMs:F0} ms";
			_statHarvests = _statHits = _statOwned = _statTris = _statEmpty = 0;
			_statPhysOffset = 0f;
			_statSideMs = 0;
			return s;
		}

		sealed class MeshData
		{
			public Vector3[] Vertices;
			public int[] Triangles;
			public Bounds[] TriBounds;
			// Triangles bucketed on a coarse grid over the mesh bounds: a region only looks at its own
			// cells instead of every triangle of a whole planet's terrain.
			public Vector3 GridMin, CellSize;
			public int Nx, Ny, Nz;
			public int[][] Cells;
			public int[] Stamp; // per triangle: the last query that saw it
			public int Query;
		}

		const int MaxCellsPerAxis = 48;

		static void BuildGrid(MeshData d, Bounds meshBounds)
		{
			Vector3 size = Vector3.Max(meshBounds.size, Vector3.one * 0.01f);
			int Cells(float extent) => Mathf.Clamp(Mathf.CeilToInt(extent / Mathf.Max(size.x, size.y, size.z) * MaxCellsPerAxis), 1, MaxCellsPerAxis);
			d.Nx = Cells(size.x);
			d.Ny = Cells(size.y);
			d.Nz = Cells(size.z);
			d.GridMin = meshBounds.min;
			d.CellSize = new Vector3(size.x / d.Nx, size.y / d.Ny, size.z / d.Nz);
			var lists = new List<int>[d.Nx * d.Ny * d.Nz];
			for (int tri = 0; tri < d.TriBounds.Length; tri++)
			{
				CellRange(d, d.TriBounds[tri], out int x0, out int y0, out int z0, out int x1, out int y1, out int z1);
				for (int z = z0; z <= z1; z++)
				for (int y = y0; y <= y1; y++)
				for (int x = x0; x <= x1; x++)
				{
					int c = (z * d.Ny + y) * d.Nx + x;
					(lists[c] ?? (lists[c] = new List<int>())).Add(tri);
				}
			}
			d.Cells = new int[lists.Length][];
			for (int c = 0; c < lists.Length; c++) d.Cells[c] = lists[c]?.ToArray();
			d.Stamp = new int[d.TriBounds.Length];
		}

		static void CellRange(MeshData d, Bounds b, out int x0, out int y0, out int z0, out int x1, out int y1, out int z1)
		{
			Vector3 lo = b.min - d.GridMin, hi = b.max - d.GridMin;
			x0 = Mathf.Clamp(Mathf.FloorToInt(lo.x / d.CellSize.x), 0, d.Nx - 1);
			y0 = Mathf.Clamp(Mathf.FloorToInt(lo.y / d.CellSize.y), 0, d.Ny - 1);
			z0 = Mathf.Clamp(Mathf.FloorToInt(lo.z / d.CellSize.z), 0, d.Nz - 1);
			x1 = Mathf.Clamp(Mathf.FloorToInt(hi.x / d.CellSize.x), 0, d.Nx - 1);
			y1 = Mathf.Clamp(Mathf.FloorToInt(hi.y / d.CellSize.y), 0, d.Ny - 1);
			z1 = Mathf.Clamp(Mathf.FloorToInt(hi.z / d.CellSize.z), 0, d.Nz - 1);
		}

		public CollisionStreamer(SharedLink link)
		{
			_link = link;
			for (int dx = -Radius; dx <= Radius; dx++)
			{
				for (int dz = -Radius; dz <= Radius; dz++)
				{
					for (int dy = -Below; dy <= Above; dy++)
					{
						_offsets.Add(new Vector3Int(dx, dy, dz));
					}
				}
			}
			_offsets.Sort((a, b) => (a.x * a.x + a.z * a.z + a.y * a.y * 2).CompareTo(b.x * b.x + b.z * b.z + b.y * b.y * 2));
			// The region under the feet right after the feet's own: Minecraft holds the player until it has ground.
			_offsets.Remove(new Vector3Int(0, -1, 0));
			_offsets.Insert(1, new Vector3Int(0, -1, 0));
			_worker = new Thread(WorkerLoop) { IsBackground = true, Name = "OWCraft collision" };
			_worker.Start();
		}

		/// <summary>Colliders that are never Minecraft's ground (our own placed-block colliders).</summary>
		public void Ignore(Collider c) => _ignored.Add(c);

		public void Unignore(Collider c) => _ignored.Remove(c);

		/// <summary>Drops everything; Minecraft clears its store when it sees the new epoch.</summary>
		public void Reset(uint epoch)
		{
			_epoch = epoch;
			_harvested.Clear();
			while (_queue.TryTake(out _)) { }
			_queue.Add(new Job { Clear = true, Epoch = epoch });
			_urgent = true;
		}

		// After a reset (teleport, new tile) Minecraft freezes the player until the ground under it
		// arrives, so the next frame harvests the regions around the feet in one go.
		bool _urgent;
		bool _refreshing;
		const int UrgentHarvests = 18;
		const double UrgentBudgetMs = 12;

		/// <summary>Once per frame, with the player's feet in Minecraft coordinates.</summary>
		public void Update(Anchor anchor, double px, double py, double pz)
		{
			if (anchor == null || anchor.Body == null) return;
			int prx = (int)Math.Floor(px / RegionSize), pry = (int)Math.Floor(py / RegionSize), prz = (int)Math.Floor(pz / RegionSize);
			float now = Time.unscaledTime;
			_clock.Restart();
			int done = 0;
			int cap = _urgent ? UrgentHarvests : MaxHarvestsPerFrame;
			double budget = _urgent ? UrgentBudgetMs : FrameBudgetMs;
			_urgent = false;
			foreach (var o in _offsets)
			{
				int rx = prx + o.x, ry = pry + o.y, rz = prz + o.z;
				long key = RegionKey(rx, ry, rz);
				bool isNear = Math.Abs(o.x) <= 1 && Math.Abs(o.z) <= 1 && o.y >= -1 && o.y <= 0;
				if (_harvested.TryGetValue(key, out float at) && !(isNear && now - at > RefreshNearSeconds)) continue;
				if (_queue.Count > 64) break; // the worker (or Minecraft) is behind
				// A refresh is for sand and crumbling ground, seen from above. The side samples (trunks,
				// walls) don't change and were the biggest frame hitches, so a refresh skips them.
				_refreshing = at > 0f;
				Harvest(anchor, rx, ry, rz);
				_refreshing = false;
				_harvested[key] = now;
				if (++done >= cap || _clock.Elapsed.TotalMilliseconds > budget) break;
			}
			if (_harvested.Count > _offsets.Count * 4) _harvested.Clear();
		}

		static long RegionKey(int x, int y, int z) =>
			((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);

		// ---- harvesting (main thread) -----------------------------------------------------------

		void Harvest(Anchor anchor, int rx, int ry, int rz)
		{
			var job = new Job { Rx = rx, Ry = ry, Rz = rz, Epoch = _epoch, Tris = new List<Tri>() };
			// The region (+ half a block) as a box in world space.
			double cx = (rx + 0.5) * RegionSize, cy = (ry + 0.5) * RegionSize, cz = (rz + 0.5) * RegionSize;
			Vector3 centre = anchor.McToWorld(cx, cy, cz);
			Quaternion rot = anchor.WorldRotation;
			float half = RegionSize * 0.5f + 0.5f;
			int count = Physics.OverlapBoxNonAlloc(centre, new Vector3(half, half, half), _hits, rot, OWLayerMask.physicalMask, QueryTriggerInteraction.Ignore);
			Vector3 lo = new Vector3(rx * RegionSize - 0.5f, ry * RegionSize - 0.5f, rz * RegionSize - 0.5f);
			Vector3 hi = lo + new Vector3(RegionSize + 1, RegionSize + 1, RegionSize + 1);
			for (int k = 0; k < count; k++)
			{
				var col = _hits[k];
				if (col == null || !col.enabled || _ignored.Contains(col)) continue;
				// Only what's part of this planet: not the ship, the player, the probe, loose objects.
				var owner = col.attachedRigidbody != null ? col.attachedRigidbody.GetComponent<OWRigidbody>() : null;
				if (owner != anchor.Body) continue;
				_statOwned++;
				var arb = col.attachedRigidbody;
				_statPhysOffset = Mathf.Max(_statPhysOffset, (arb.position - arb.transform.position).magnitude);
				try
				{
					AddCollider(anchor, col, job.Tris, lo, hi, centre, rot);
				}
				catch (Exception e)
				{
					OWCraft.LogOnce("harvest-" + col.GetType().Name, $"couldn't read collider {col.name} ({col.GetType().Name}): {e.Message}");
				}
			}
			_statHarvests++;
			_statHits += count;
			_statTris += job.Tris.Count;
			if (job.Tris.Count == 0) _statEmpty++;
			_queue.Add(job);
		}

		void AddCollider(Anchor anchor, Collider col, List<Tri> tris, Vector3 lo, Vector3 hi, Vector3 centre, Quaternion rot)
		{
			Transform t = col.transform;
			switch (col)
			{
				case BoxCollider box:
				{
					Vector3 c = box.center, s = box.size * 0.5f;
					var corners = new Vector3[8];
					for (int i = 0; i < 8; i++)
					{
						var local = c + new Vector3((i & 1) != 0 ? s.x : -s.x, (i & 2) != 0 ? s.y : -s.y, (i & 4) != 0 ? s.z : -s.z);
						corners[i] = ToMc(anchor, t.TransformPoint(local));
					}
					foreach (var f in BoxFaces)
					{
						AddTri(tris, corners[f[0]], corners[f[1]], corners[f[2]], lo, hi);
						AddTri(tris, corners[f[0]], corners[f[2]], corners[f[3]], lo, hi);
					}
					return;
				}
				case SphereCollider sphere:
				{
					Vector3 sc = t.lossyScale;
					float r = sphere.radius * Mathf.Max(Mathf.Abs(sc.x), Mathf.Abs(sc.y), Mathf.Abs(sc.z));
					AddSphere(anchor, tris, t.TransformPoint(sphere.center), t.TransformPoint(sphere.center), r, lo, hi);
					return;
				}
				case CapsuleCollider capsule:
				{
					Vector3 sc = t.lossyScale;
					Vector3 dir = capsule.direction == 0 ? Vector3.right : capsule.direction == 1 ? Vector3.up : Vector3.forward;
					float r = capsule.radius * Mathf.Max(Mathf.Abs(sc.x), Mathf.Abs(sc.y), Mathf.Abs(sc.z));
					float halfLen = Mathf.Max(0f, capsule.height * 0.5f - capsule.radius);
					Vector3 a = t.TransformPoint(capsule.center + dir * halfLen), b = t.TransformPoint(capsule.center - dir * halfLen);
					AddSphere(anchor, tris, a, b, r, lo, hi);
					return;
				}
				case MeshCollider meshCol when meshCol.sharedMesh != null:
				{
					var data = GetMesh(meshCol.sharedMesh);
					if (data == null)
					{
						UnreadableMeshes++;
						AddSurfaceSamples(anchor, col, tris, lo, hi, 1, true, 0f);
						if (_refreshing) return;
						// From the sides too: from above, a tree or a house is only a sheet at its top
						// and you'd walk straight through its trunk or walls.
						_clock2.Restart();
						AddSurfaceSamples(anchor, col, tris, lo, hi, 0, true, SideMaxStep);
						AddSurfaceSamples(anchor, col, tris, lo, hi, 0, false, SideMaxStep);
						AddSurfaceSamples(anchor, col, tris, lo, hi, 2, true, SideMaxStep);
						AddSurfaceSamples(anchor, col, tris, lo, hi, 2, false, SideMaxStep);
						_statSideMs += _clock2.Elapsed.TotalMilliseconds;
						return;
					}
					// The region box in the mesh's local space, to skip most triangles cheaply.
					Bounds local = new Bounds(t.InverseTransformPoint(anchor.McToWorld(lo.x, lo.y, lo.z)), Vector3.zero);
					for (int i = 1; i < 8; i++)
					{
						local.Encapsulate(t.InverseTransformPoint(anchor.McToWorld((i & 1) != 0 ? hi.x : lo.x, (i & 2) != 0 ? hi.y : lo.y, (i & 4) != 0 ? hi.z : lo.z)));
					}
					var v = data.Vertices;
					var idx = data.Triangles;
					int query = ++data.Query;
					CellRange(data, local, out int x0, out int y0, out int z0, out int x1, out int y1, out int z1);
					for (int z = z0; z <= z1; z++)
					for (int y = y0; y <= y1; y++)
					for (int x = x0; x <= x1; x++)
					{
						var cell = data.Cells[(z * data.Ny + y) * data.Nx + x];
						if (cell == null) continue;
						foreach (int tri in cell)
						{
							if (data.Stamp[tri] == query) continue;
							data.Stamp[tri] = query;
							if (!data.TriBounds[tri].Intersects(local)) continue;
							int i = tri * 3;
							AddTri(tris, ToMc(anchor, t.TransformPoint(v[idx[i]])), ToMc(anchor, t.TransformPoint(v[idx[i + 1]])), ToMc(anchor, t.TransformPoint(v[idx[i + 2]])), lo, hi);
						}
					}
					return;
				}
				default:
					AddSurfaceSamples(anchor, col, tris, lo, hi, 1, true, 0f);
					return;
			}
		}

		static readonly int[][] BoxFaces =
		{
			new[] { 0, 1, 3, 2 }, new[] { 4, 6, 7, 5 }, new[] { 0, 4, 5, 1 }, new[] { 2, 3, 7, 6 }, new[] { 0, 2, 6, 4 }, new[] { 1, 5, 7, 3 },
		};

		MeshData GetMesh(Mesh mesh)
		{
			if (_meshes.TryGetValue(mesh, out var data)) return data;
			if (mesh.isReadable)
			{
				var v = mesh.vertices;
				var idx = mesh.triangles;
				var bounds = new Bounds[idx.Length / 3];
				for (int i = 0, tri = 0; i + 2 < idx.Length; i += 3, tri++)
				{
					var b = new Bounds(v[idx[i]], Vector3.zero);
					b.Encapsulate(v[idx[i + 1]]);
					b.Encapsulate(v[idx[i + 2]]);
					bounds[tri] = b;
				}
				data = new MeshData { Vertices = v, Triangles = idx, TriBounds = bounds };
				var all = v.Length > 0 ? new Bounds(v[0], Vector3.zero) : new Bounds();
				foreach (var p in v) all.Encapsulate(p);
				BuildGrid(data, all);
			}
			_meshes[mesh] = data;
			return data;
		}

		/// <summary>
		/// Colliders whose triangles Unity won't hand out (meshes baked without Read/Write): cast rays at
		/// the collider along one Minecraft axis on a half-block grid, and stitch neighbouring hits into
		/// triangles. From above that's the ground; from the sides, walls and trunks. maxStep &gt; 0 skips
		/// cells whose hits jump further than that along the ray (an edge, not a surface).
		/// </summary>
		void AddSurfaceSamples(Anchor anchor, Collider col, List<Tri> tris, Vector3 lo, Vector3 hi, int axis, bool fromHigh, float maxStep)
		{
			int u = (axis + 1) % 3, v = (axis + 2) % 3;
			int n = Mathf.RoundToInt((hi[u] - lo[u]) / SampleStep) + 1;
			if (_samples == null || _samples.Length < n * n)
			{
				_samples = new Vector3[n * n];
				_sampleHit = new bool[n * n];
			}
			Bounds bounds = col.bounds;
			for (int iv = 0; iv < n; iv++)
			{
				for (int iu = 0; iu < n; iu++)
				{
					int k = iv * n + iu;
					_sampleHit[k] = false;
					Vector3 a = default;
					a[u] = lo[u] + iu * SampleStep;
					a[v] = lo[v] + iv * SampleStep;
					a[axis] = fromHigh ? hi[axis] : lo[axis];
					Vector3 b = a;
					b[axis] = fromHigh ? lo[axis] : hi[axis];
					Vector3 wa = anchor.McToWorld(a.x, a.y, a.z), d = anchor.McToWorld(b.x, b.y, b.z) - wa;
					float len = d.magnitude;
					var ray = new Ray(wa, d / len);
					if (!bounds.IntersectRay(ray, out float enter) || enter > len) continue;
					if (!col.Raycast(ray, out var hit, len)) continue;
					_samples[k] = ToMc(anchor, hit.point);
					_sampleHit[k] = true;
				}
			}
			for (int iv = 0; iv + 1 < n; iv++)
			{
				for (int iu = 0; iu + 1 < n; iu++)
				{
					int k00 = iv * n + iu, k10 = k00 + 1, k01 = k00 + n, k11 = k01 + 1;
					if (!_sampleHit[k00] || !_sampleHit[k10] || !_sampleHit[k01] || !_sampleHit[k11]) continue;
					Vector3 p00 = _samples[k00], p10 = _samples[k10], p01 = _samples[k01], p11 = _samples[k11];
					if (maxStep > 0f)
					{
						float mn = Mathf.Min(Mathf.Min(p00[axis], p10[axis]), Mathf.Min(p01[axis], p11[axis]));
						float mx = Mathf.Max(Mathf.Max(p00[axis], p10[axis]), Mathf.Max(p01[axis], p11[axis]));
						if (mx - mn > maxStep) continue;
					}
					AddTri(tris, p00, p10, p11, lo, hi);
					AddTri(tris, p00, p11, p01, lo, hi);
				}
			}
		}

		Vector3[] _samples;
		bool[] _sampleHit;
		readonly Stopwatch _clock2 = new Stopwatch();
		double _statSideMs;
		const float SideMaxStep = 1f;

		void AddSphere(Anchor anchor, List<Tri> tris, Vector3 a, Vector3 b, float r, Vector3 lo, Vector3 hi)
		{
			Vector3 ma = ToMc(anchor, a), mb = ToMc(anchor, b);
			if (Mathf.Max(ma.x, mb.x) + r < lo.x || Mathf.Min(ma.x, mb.x) - r > hi.x || Mathf.Max(ma.y, mb.y) + r < lo.y || Mathf.Min(ma.y, mb.y) - r > hi.y
				|| Mathf.Max(ma.z, mb.z) + r < lo.z || Mathf.Min(ma.z, mb.z) - r > hi.z)
			{
				return;
			}
			// Lat-long shell, top half around a and bottom half around b (a capsule; a sphere when a == b).
			const int Rings = 8, Segs = 12;
			Vector3 axis = (ma - mb).sqrMagnitude > 1e-8f ? (ma - mb).normalized : Vector3.up;
			Vector3 side = Vector3.Cross(axis, Mathf.Abs(axis.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
			Vector3 side2 = Vector3.Cross(axis, side);
			Vector3 Point(int ring, int seg)
			{
				float theta = Mathf.PI * ring / Rings;
				float phi = 2f * Mathf.PI * seg / Segs;
				Vector3 d = axis * Mathf.Cos(theta) + (side * Mathf.Cos(phi) + side2 * Mathf.Sin(phi)) * Mathf.Sin(theta);
				return (ring <= Rings / 2 ? ma : mb) + d * r;
			}
			for (int ring = 0; ring < Rings; ring++)
			{
				for (int seg = 0; seg < Segs; seg++)
				{
					Vector3 p00 = Point(ring, seg), p01 = Point(ring, seg + 1), p10 = Point(ring + 1, seg), p11 = Point(ring + 1, seg + 1);
					AddTri(tris, p00, p10, p11, lo, hi);
					AddTri(tris, p00, p11, p01, lo, hi);
				}
			}
		}

		static Vector3 ToMc(Anchor anchor, Vector3 world)
		{
			anchor.WorldToMc(world, out double x, out double y, out double z);
			return new Vector3((float)x, (float)y, (float)z);
		}

		static void AddTri(List<Tri> tris, Vector3 a, Vector3 b, Vector3 c, Vector3 lo, Vector3 hi)
		{
			if (Mathf.Max(a.x, b.x, c.x) < lo.x || Mathf.Min(a.x, b.x, c.x) > hi.x) return;
			if (Mathf.Max(a.y, b.y, c.y) < lo.y || Mathf.Min(a.y, b.y, c.y) > hi.y) return;
			if (Mathf.Max(a.z, b.z, c.z) < lo.z || Mathf.Min(a.z, b.z, c.z) > hi.z) return;
			if (!Finite(a) || !Finite(b) || !Finite(c)) return;
			// Unity -> Minecraft is a mirror: swap two corners so the winding still faces out.
			tris.Add(new Tri { A = a, B = c, C = b });
		}

		static bool Finite(Vector3 v) =>
			!float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z) && Mathf.Abs(v.x) < 1e7f && Mathf.Abs(v.y) < 1e7f && Mathf.Abs(v.z) < 1e7f;

		// ---- worker thread ----------------------------------------------------------------------

		void WorkerLoop()
		{
			while (!_disposed)
			{
				Job job;
				try
				{
					if (!_queue.TryTake(out job, 200)) continue;
				}
				catch (ObjectDisposedException)
				{
					return;
				}
				try
				{
					if (job.Clear)
					{
						Send(Proto.ColClear, 4, p => Marshal.WriteInt32(p, (int)job.Epoch));
						continue;
					}
					SendTriangles(job);
					Voxelize(job);
					RegionsSent++;
				}
				catch (Exception e)
				{
					OWCraft.LogOnce("collision-worker", "collision worker: " + e);
				}
			}
		}

		void Send(uint type, int payload, Action<IntPtr> fill)
		{
			// Minecraft drains this ring on its own thread; wait for room rather than drop a region.
			for (int attempt = 0; attempt < 500 && !_disposed; attempt++)
			{
				if (_link.WriteCollision(type, payload, fill)) return;
				Thread.Sleep(2);
			}
			OWCraft.LogOnce("collision-full", "collision ring stayed full; is Minecraft running?");
		}

		static void WriteRegionHeader(IntPtr p, Job job, int count)
		{
			int x0 = job.Rx * RegionSize, y0 = job.Ry * RegionSize, z0 = job.Rz * RegionSize;
			Marshal.WriteInt32(p, 0, x0);
			Marshal.WriteInt32(p, 4, y0);
			Marshal.WriteInt32(p, 8, z0);
			Marshal.WriteInt32(p, 12, x0 + RegionSize - 1);
			Marshal.WriteInt32(p, 16, y0 + RegionSize - 1);
			Marshal.WriteInt32(p, 20, z0 + RegionSize - 1);
			Marshal.WriteInt32(p, 24, (int)job.Epoch);
			Marshal.WriteInt32(p, 28, count);
		}

		unsafe void SendTriangles(Job job)
		{
			var tris = job.Tris;
			Send(Proto.ColTris, Proto.ColRegionHeaderBytes + tris.Count * Proto.ColTriBytes, p =>
			{
				WriteRegionHeader(p, job, tris.Count);
				float* f = (float*)(p + Proto.ColRegionHeaderBytes);
				foreach (var t in tris)
				{
					f[0] = t.A.x; f[1] = t.A.y; f[2] = t.A.z;
					f[3] = t.B.x; f[4] = t.B.y; f[5] = t.B.z;
					f[6] = t.C.x; f[7] = t.C.y; f[8] = t.C.z;
					((uint*)f)[9] = 0;
					f += 10;
				}
			});
		}

		unsafe void Voxelize(Job job)
		{
			const int G = Grid;
			var solid = new ulong[G * G];
			var steep = new ulong[G * G];
			float ox = job.Rx * RegionSize, oy = job.Ry * RegionSize, oz = job.Rz * RegionSize;
			int ClampLo(float v) => Math.Min(Math.Max((int)Math.Floor(v), 0), G - 1);
			int ClampHi(float v) => Math.Min(Math.Max((int)Math.Ceiling(v) - 1, 0), G - 1);

			// Triangles: plane-guided SAT test so big triangles cost O(area), not O(volume).
			var a = new float[3];
			var b = new float[3];
			var c = new float[3];
			var n = new float[3];
			var lo = new float[3];
			var hi = new float[3];
			var cen = new float[3];
			foreach (var tri in job.Tris)
			{
				a[0] = (tri.A.x - ox) * 8; a[1] = (tri.A.y - oy) * 8; a[2] = (tri.A.z - oz) * 8;
				b[0] = (tri.B.x - ox) * 8; b[1] = (tri.B.y - oy) * 8; b[2] = (tri.B.z - oz) * 8;
				c[0] = (tri.C.x - ox) * 8; c[1] = (tri.C.y - oy) * 8; c[2] = (tri.C.z - oz) * 8;
				for (int i = 0; i < 3; i++)
				{
					lo[i] = Math.Min(a[i], Math.Min(b[i], c[i]));
					hi[i] = Math.Max(a[i], Math.Max(b[i], c[i]));
				}
				if (hi[0] < 0 || hi[1] < 0 || hi[2] < 0 || lo[0] > G || lo[1] > G || lo[2] > G) continue;
				float e1x = b[0] - a[0], e1y = b[1] - a[1], e1z = b[2] - a[2];
				float e2x = c[0] - a[0], e2y = c[1] - a[1], e2z = c[2] - a[2];
				n[0] = e1y * e2z - e1z * e2y;
				n[1] = e1z * e2x - e1x * e2z;
				n[2] = e1x * e2y - e1y * e2x;
				float len = (float)Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
				if (len < 1e-9f) continue;
				n[0] /= len; n[1] /= len; n[2] /= len;
				float ny = Math.Abs(n[1]);
				var grid = ny >= SteepMax || ny < SteepMin ? solid : steep;

				int dom = 0;
				if (Math.Abs(n[1]) > Math.Abs(n[dom])) dom = 1;
				if (Math.Abs(n[2]) > Math.Abs(n[dom])) dom = 2;
				int u = (dom + 1) % 3, v = (dom + 2) % 3;
				float d = n[0] * a[0] + n[1] * a[1] + n[2] * a[2];
				float r = 0.5f * (Math.Abs(n[0]) + Math.Abs(n[1]) + Math.Abs(n[2]));
				int iu0 = ClampLo(lo[u]), iu1 = ClampHi(hi[u]), iv0 = ClampLo(lo[v]), iv1 = ClampHi(hi[v]);
				int id0 = ClampLo(lo[dom]), id1 = ClampHi(hi[dom]);
				for (int iu = iu0; iu <= iu1; iu++)
				{
					for (int iv = iv0; iv <= iv1; iv++)
					{
						float cu = iu + 0.5f, cv = iv + 0.5f;
						float s0 = (d - r - n[u] * cu - n[v] * cv) / n[dom];
						float s1 = (d + r - n[u] * cu - n[v] * cv) / n[dom];
						int a0 = Math.Max(id0, (int)Math.Floor(Math.Min(s0, s1) - 0.5f));
						int a1 = Math.Min(id1, (int)Math.Ceiling(Math.Max(s0, s1) - 0.5f));
						for (int id = a0; id <= a1; id++)
						{
							cen[dom] = id + 0.5f;
							cen[u] = cu;
							cen[v] = cv;
							if (TriBoxOverlap(cen, 0.5f, a, b, c, n))
							{
								int px = dom == 0 ? id : u == 0 ? iu : iv;
								int py = dom == 1 ? id : u == 1 ? iu : iv;
								int pz = dom == 2 ? id : u == 2 ? iu : iv;
								grid[py * G + pz] |= 1UL << px;
							}
						}
					}
				}
			}

			// Steep (50-84 degree) surfaces: whole-block footprints, so the risers between columns
			// exceed Minecraft's step height and its own jump rules decide what's climbable.
			for (int by = 0; by < RegionSize; by++)
			{
				for (int bz = 0; bz < RegionSize; bz++)
				{
					for (int bx = 0; bx < RegionSize; bx++)
					{
						ulong xmask = 0xFFUL << (bx * 8);
						int minY = 99, maxY = -1;
						for (int y = by * 8; y < by * 8 + 8; y++)
						{
							for (int z = bz * 8; z < bz * 8 + 8; z++)
							{
								if ((steep[y * G + z] & xmask) != 0)
								{
									minY = Math.Min(minY, y);
									maxY = Math.Max(maxY, y);
								}
							}
						}
						for (int y = minY; y <= maxY; y++)
						{
							for (int z = bz * 8; z < bz * 8 + 8; z++)
							{
								solid[y * G + z] |= xmask;
							}
						}
					}
				}
			}

			// Pack the non-empty blocks.
			var blocks = new List<(int x, int y, int z, ulong[] bits)>();
			for (int by = 0; by < RegionSize; by++)
			{
				for (int bz = 0; bz < RegionSize; bz++)
				{
					for (int bx = 0; bx < RegionSize; bx++)
					{
						ulong[] bits = null;
						for (int sy = 0; sy < 8; sy++)
						{
							ulong layer = 0;
							for (int sz = 0; sz < 8; sz++)
							{
								ulong row = (solid[(by * 8 + sy) * G + (bz * 8 + sz)] >> (bx * 8)) & 0xFF;
								layer |= row << (sz * 8);
							}
							if (layer != 0)
							{
								if (bits == null) bits = new ulong[8];
								bits[sy] = layer;
							}
						}
						if (bits != null)
						{
							blocks.Add((job.Rx * RegionSize + bx, job.Ry * RegionSize + by, job.Rz * RegionSize + bz, bits));
						}
					}
				}
			}

			Send(Proto.ColRegion, Proto.ColRegionHeaderBytes + blocks.Count * Proto.ColBlockBytes, p =>
			{
				WriteRegionHeader(p, job, blocks.Count);
				byte* e = (byte*)(p + Proto.ColRegionHeaderBytes);
				foreach (var blk in blocks)
				{
					*(int*)e = blk.x;
					*(int*)(e + 4) = blk.y;
					*(int*)(e + 8) = blk.z;
					*(int*)(e + 12) = 0;
					for (int i = 0; i < 8; i++)
					{
						*(ulong*)(e + 16 + i * 8) = blk.bits[i];
					}
					e += Proto.ColBlockBytes;
				}
			});
		}

		static bool AxisTest(float[] v0, float[] v1, float[] v2, float ax, float ay, float az, float h)
		{
			float p0 = v0[0] * ax + v0[1] * ay + v0[2] * az;
			float p1 = v1[0] * ax + v1[1] * ay + v1[2] * az;
			float p2 = v2[0] * ax + v2[1] * ay + v2[2] * az;
			float mn = Math.Min(p0, Math.Min(p1, p2)), mx = Math.Max(p0, Math.Max(p1, p2));
			float r = h * (Math.Abs(ax) + Math.Abs(ay) + Math.Abs(az));
			return !(mn > r || mx < -r);
		}

		[ThreadStatic] static float[] _v0, _v1, _v2;

		/// <summary>Akenine-Moller triangle/box overlap: box centred at c with half-size h.</summary>
		static bool TriBoxOverlap(float[] c, float h, float[] ta, float[] tb, float[] tc, float[] n)
		{
			var v0 = _v0 ?? (_v0 = new float[3]);
			var v1 = _v1 ?? (_v1 = new float[3]);
			var v2 = _v2 ?? (_v2 = new float[3]);
			for (int i = 0; i < 3; i++)
			{
				v0[i] = ta[i] - c[i];
				v1[i] = tb[i] - c[i];
				v2[i] = tc[i] - c[i];
				float mn = Math.Min(v0[i], Math.Min(v1[i], v2[i])), mx = Math.Max(v0[i], Math.Max(v1[i], v2[i]));
				if (mn > h || mx < -h) return false;
			}
			float d = n[0] * v0[0] + n[1] * v0[1] + n[2] * v0[2];
			float r = h * (Math.Abs(n[0]) + Math.Abs(n[1]) + Math.Abs(n[2]));
			if (Math.Abs(d) > r) return false;
			for (int e = 0; e < 3; e++)
			{
				float[] p = e == 0 ? v0 : e == 1 ? v1 : v2;
				float[] q = e == 0 ? v1 : e == 1 ? v2 : v0;
				float ex = q[0] - p[0], ey = q[1] - p[1], ez = q[2] - p[2];
				// edge x unit axes
				if (!AxisTest(v0, v1, v2, 0, ez, -ey, h)) return false;
				if (!AxisTest(v0, v1, v2, -ez, 0, ex, h)) return false;
				if (!AxisTest(v0, v1, v2, ey, -ex, 0, h)) return false;
			}
			return true;
		}

		public void Dispose()
		{
			_disposed = true;
			_worker.Join(1000);
			_queue.Dispose();
		}
	}
}
