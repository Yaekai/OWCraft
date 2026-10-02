// Feeds the collision voxelizer a known shape and checks the bytes it puts on the ring, without the game.
using System;
using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using OWCraft.Link;
using OWCraft.World;
using UnityEngine;

static unsafe class Program
{
	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr OpenFileMappingW(uint access, bool inherit, string name);
	[DllImport("kernel32.dll")] static extern IntPtr MapViewOfFile(IntPtr h, uint access, uint hi, uint lo, UIntPtr n);

	static int failures;
	static void Check(bool ok, string what) { Console.WriteLine((ok ? "ok   " : "FAIL ") + what); if (!ok) failures++; }

	static int Main()
	{
		const string name = @"LocalOWCraft_test";
		using var link = new SharedLink(name);
		byte* b = (byte*)MapViewOfFile(OpenFileMappingW(0xF001F, false, name), 0xF001F, 0, 0, UIntPtr.Zero);
		Check(*(uint*)b == Proto.Magic && *(uint*)(b + 4) == Proto.Version, "header magic/version");

		// Region (0, 8, 0): blocks 0..7, 64..71, 0..7. A floor at exactly y = 64 (two triangles).
		var streamer = (CollisionStreamer)Activator.CreateInstance(typeof(CollisionStreamer), link);
		var t = typeof(CollisionStreamer);
		var triType = t.GetNestedType("Tri", BindingFlags.NonPublic);
		var jobType = t.GetNestedType("Job", BindingFlags.NonPublic);
		var listType = typeof(System.Collections.Generic.List<>).MakeGenericType(triType);
		var tris = (IList)Activator.CreateInstance(listType);
		object Tri(Vector3 a, Vector3 c, Vector3 d) { var o = Activator.CreateInstance(triType); triType.GetField("A").SetValue(o, a); triType.GetField("B").SetValue(o, c); triType.GetField("C").SetValue(o, d); return o; }
		tris.Add(Tri(new Vector3(-4, 64, -4), new Vector3(12, 64, -4), new Vector3(12, 64, 12)));
		tris.Add(Tri(new Vector3(-4, 64, -4), new Vector3(12, 64, 12), new Vector3(-4, 64, 12)));
		var job = Activator.CreateInstance(jobType);
		jobType.GetField("Rx").SetValue(job, 0); jobType.GetField("Ry").SetValue(job, 8); jobType.GetField("Rz").SetValue(job, 0);
		jobType.GetField("Epoch").SetValue(job, 7u); jobType.GetField("Tris").SetValue(job, tris);
		t.GetMethod("SendTriangles", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(streamer, new[] { job });
		t.GetMethod("Voxelize", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(streamer, new[] { job });

		byte* ring = b + Proto.OffCollisionRing;
		long head = *(long*)(ring + Proto.CrHead);
		byte* m = ring + Proto.CrData;
		Check(*(uint*)m == Proto.ColTris, "first message is COL_TRIS");
		int len = *(int*)(m + 4);
		int* h = (int*)(m + 8);
		Check(h[0] == 0 && h[1] == 64 && h[2] == 0 && h[3] == 7 && h[4] == 71 && h[5] == 7 && h[6] == 7 && h[7] == 2, $"tris header box/epoch/count ({h[0]},{h[1]},{h[2]})-({h[3]},{h[4]},{h[5]}) e{h[6]} n{h[7]}");
		float* f = (float*)(m + 8 + 32);
		// Triangles go through as given (winding doesn't matter: Minecraft classifies walkable by |ny|).
		Check(f[0] == -4 && f[1] == 64 && f[2] == -4 && f[3] == 12 && f[8] == 12, "triangle vertices round-trip");
		Check(len == 32 + 2 * 40, "tris payload size");

		byte* m2 = m + ((8 + len + 7) & ~7);
		Check(*(uint*)m2 == Proto.ColRegion, "second message is COL_REGION");
		int* h2 = (int*)(m2 + 8);
		Check(h2[7] == 64, $"64 blocks touched by the floor (got {h2[7]})");
		bool allBottom = true, nothingElse = true;
		for (int i = 0; i < h2[7]; i++)
		{
			byte* e = m2 + 8 + 32 + i * 80;
			int y = *(int*)(e + 4);
			ulong* bits = (ulong*)(e + 16);
			if (y != 64 || bits[0] != ulong.MaxValue) allBottom = false;
			for (int k = 1; k < 8; k++) if (bits[k] != 0) nothingElse = false;
		}
		Check(allBottom, "each block at y=64 has its whole bottom voxel layer solid");
		Check(nothingElse, "no voxels above the floor layer");
		Check(head == (m2 - m) + ((8 + 32 + h2[7] * 80 + 7) & ~7), "ring head matches written bytes");
		streamer.Dispose();
		Console.WriteLine(failures == 0 ? "ALL PASSED" : failures + " FAILED");
		return failures;
	}
}
