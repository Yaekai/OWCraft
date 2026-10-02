using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace OWCraft.Link
{
	/// <summary>
	/// The host end of the shared mapping: creates it, keeps the heartbeat, publishes the host state,
	/// produces the input and collision rings, and consumes Minecraft's state, events, overlay and
	/// render ring. Main-thread only unless a member says otherwise.
	/// </summary>
	public sealed unsafe class SharedLink : IDisposable
	{
		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		static extern IntPtr CreateFileMappingW(IntPtr hFile, IntPtr attrs, uint protect, uint sizeHigh, uint sizeLow, string name);

		[DllImport("kernel32.dll", SetLastError = true)]
		static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offHigh, uint offLow, UIntPtr bytes);

		[DllImport("kernel32.dll")]
		static extern bool UnmapViewOfFile(IntPtr view);

		[DllImport("kernel32.dll")]
		static extern bool CloseHandle(IntPtr handle);

		[DllImport("kernel32.dll")]
		static extern ulong GetTickCount64();

		const uint PageReadWrite = 0x04;
		const uint FileMapAllAccess = 0xF001F;

		readonly IntPtr _mapping;
		readonly byte* _base;
		int _hostSeq;
		long _inputHead;
		long _collisionHead;
		int _overlayFront = 2; // MC's back starts at 1, middle at 0

		public string Name { get; }

		public SharedLink(string name)
		{
			Name = name;
			_mapping = CreateFileMappingW(new IntPtr(-1), IntPtr.Zero, PageReadWrite,
				(uint)(Proto.MappingBytes >> 32), (uint)(Proto.MappingBytes & 0xFFFFFFFF), name);
			if (_mapping == IntPtr.Zero)
			{
				throw new InvalidOperationException("CreateFileMappingW failed: " + Marshal.GetLastWin32Error());
			}
			IntPtr view = MapViewOfFile(_mapping, FileMapAllAccess, 0, 0, UIntPtr.Zero);
			if (view == IntPtr.Zero)
			{
				CloseHandle(_mapping);
				throw new InvalidOperationException("MapViewOfFile failed: " + Marshal.GetLastWin32Error());
			}
			_base = (byte*)view;
			Reset();
		}

		/// <summary>Fresh state for every region the host owns. Minecraft notices the new pid and starts over.</summary>
		void Reset()
		{
			// Minecraft writes its pid once, when it opens the mapping: if it was already running (it
			// keeps the mapping alive across our restarts), keep it or we'd never know which one it is.
			int mcPid = I32(Proto.OffHeader + Proto.HMcPid);
			Zero(Proto.OffHeader, 0x100);
			I32(Proto.OffHeader + Proto.HMcPid) = mcPid;
			Zero(Proto.OffSkyState, 0x40);
			Zero(Proto.OffOverlayCtl, 0x100);
			Zero(Proto.OffInputRing, 0x80);
			Zero(Proto.OffCollisionRing, 0x80);
			Zero(Proto.OffActorTable, 0x40);
			Zero(Proto.OffEventRing, 0x80);
			Zero(Proto.OffWorldEntities, 0x40);
			Zero(Proto.OffRenderRing, 0x80);
			Zero(Proto.OffWaterGrid, 0x10);
			*(int*)(_base + Proto.OffWaterGrid + 0xC) = -1; // no water grid
			I32(Proto.OffHeader + Proto.HMagic) = (int)Proto.Magic;
			I32(Proto.OffHeader + Proto.HVersion) = (int)Proto.Version;
			I32(Proto.OffHeader + Proto.HHostPid) = Process.GetCurrentProcess().Id;
			Heartbeat();
		}

		void Zero(long off, int bytes)
		{
			for (int i = 0; i < bytes; i++) _base[off + i] = 0;
		}

		ref int I32(long off) => ref *(int*)(_base + off);
		ref long I64(long off) => ref *(long*)(_base + off);
		ref float F32(long off) => ref *(float*)(_base + off);
		ref double F64(long off) => ref *(double*)(_base + off);

		public void Heartbeat() => Volatile.Write(ref I64(Proto.OffHeader + Proto.HHostHeartbeat), (long)GetTickCount64());

		/// <summary>Minecraft is attached and ticking (its heartbeat is under two seconds old).</summary>
		public bool GuestAlive
		{
			get
			{
				long hb = Volatile.Read(ref I64(Proto.OffHeader + Proto.HMcHeartbeat));
				return hb != 0 && (long)GetTickCount64() - hb < 2000;
			}
		}

		public int GuestPid => I32(Proto.OffHeader + Proto.HMcPid);

		// ---- host state ---------------------------------------------------------------------

		public struct HostState
		{
			public uint Flags;
			public uint WorldId;
			public uint CollisionEpoch;
			public double X, Y, Z; // feet, MC coords
			public float Yaw, Pitch; // MC degrees: the authoritative look
			public uint TeleportSeq; // MC moves its player to X/Y/Z when this changes
			public int ViewportW, ViewportH;
			public float GameHour;
		}

		public void WriteHostState(in HostState s)
		{
			long b = Proto.OffSkyState;
			_hostSeq += 2;
			Volatile.Write(ref I32(b + Proto.SsSeq), _hostSeq - 1); // odd: writing
			Thread.MemoryBarrier();
			I32(b + Proto.SsFlags) = (int)s.Flags;
			I32(b + Proto.SsWorldId) = (int)s.WorldId;
			I32(b + Proto.SsCollisionEpoch) = (int)s.CollisionEpoch;
			F64(b + Proto.SsPosX) = s.X;
			F64(b + Proto.SsPosY) = s.Y;
			F64(b + Proto.SsPosZ) = s.Z;
			F32(b + Proto.SsYaw) = s.Yaw;
			F32(b + Proto.SsPitch) = s.Pitch;
			I32(b + Proto.SsTeleportSeq) = (int)s.TeleportSeq;
			I32(b + Proto.SsViewportW) = s.ViewportW;
			I32(b + Proto.SsViewportH) = s.ViewportH;
			F32(b + Proto.SsGameHour) = s.GameHour;
			Thread.MemoryBarrier();
			Volatile.Write(ref I32(b + Proto.SsSeq), _hostSeq);
		}

		// ---- Minecraft state ----------------------------------------------------------------

		public struct GuestState
		{
			public uint Flags;
			public double X, Y, Z; // interpolated feet
			public float Yaw, Pitch;
			public float EyeHeight;
			public float Sensitivity;
			public uint TeleportAck;
			public long FrameCounter;
			public float Fov; // vertical degrees
			public double EyeX, EyeY, EyeZ;
			public int CameraMode;
			public float CameraDistance;
			// raw ticks, for interpolating on our own frame clock
			public long TickQpc;
			public double PrevX, PrevY, PrevZ, CurX, CurY, CurZ;
			public float TickEyeO, TickEye, TickMs;

			public bool Has(uint flag) => (Flags & flag) != 0;
		}

		public bool ReadGuestState(out GuestState s)
		{
			long b = Proto.OffMcState;
			s = default;
			for (int attempt = 0; attempt < 8; attempt++)
			{
				int seq0 = Volatile.Read(ref I32(b + Proto.MsSeq));
				if ((seq0 & 1) != 0) continue;
				Thread.MemoryBarrier();
				s.Flags = (uint)I32(b + Proto.MsFlags);
				s.X = F64(b + Proto.MsX);
				s.Y = F64(b + Proto.MsY);
				s.Z = F64(b + Proto.MsZ);
				s.Yaw = F32(b + Proto.MsYaw);
				s.Pitch = F32(b + Proto.MsPitch);
				s.EyeHeight = F32(b + Proto.MsEyeHeight);
				s.Sensitivity = F32(b + Proto.MsSensitivity);
				s.TeleportAck = (uint)I32(b + Proto.MsTeleportAck);
				s.FrameCounter = I64(b + Proto.MsFrameCounter);
				s.Fov = F32(b + Proto.MsFov);
				s.EyeX = F64(b + Proto.MsEyeX);
				s.EyeY = F64(b + Proto.MsEyeY);
				s.EyeZ = F64(b + Proto.MsEyeZ);
				s.CameraMode = I32(b + Proto.MsCameraMode);
				s.CameraDistance = F32(b + Proto.MsCameraDistance);
				s.TickQpc = I64(b + Proto.MsTickQpc);
				s.PrevX = F64(b + Proto.MsPrevX);
				s.PrevY = F64(b + Proto.MsPrevX + 8);
				s.PrevZ = F64(b + Proto.MsPrevX + 16);
				s.CurX = F64(b + Proto.MsCurX);
				s.CurY = F64(b + Proto.MsCurX + 8);
				s.CurZ = F64(b + Proto.MsCurX + 16);
				s.TickEyeO = F32(b + Proto.MsCurX + 24);
				s.TickEye = F32(b + Proto.MsCurX + 28);
				s.TickMs = F32(b + Proto.MsTickMs);
				Thread.MemoryBarrier();
				if (Volatile.Read(ref I32(b + Proto.MsSeq)) == seq0) return seq0 != 0;
			}
			return false;
		}

		// ---- input ring ---------------------------------------------------------------------

		public void PushInput(ushort type, ushort code, int a, int b = 0, int c = 0)
		{
			long ring = Proto.OffInputRing;
			byte* e = _base + ring + Proto.IrData + (_inputHead & (Proto.InputRingEntries - 1)) * 16;
			*(ushort*)e = type;
			*(ushort*)(e + 2) = code;
			*(int*)(e + 4) = a;
			*(int*)(e + 8) = b;
			*(int*)(e + 12) = c;
			_inputHead++;
			Volatile.Write(ref I64(ring + Proto.IrHead), _inputHead);
		}

		// ---- collision ring -----------------------------------------------------------------

		/// <summary>Room for a message of this many payload bytes right now (Minecraft keeps up, or it doesn't).</summary>
		public bool CollisionHasRoom(int payloadBytes)
		{
			long msg = (8 + payloadBytes + 7) & ~7L;
			long tail = Volatile.Read(ref I64(Proto.OffCollisionRing + Proto.CrTail));
			return Proto.CrDataBytes - (_collisionHead - tail) >= msg * 2;
		}

		/// <summary>Writes one message; the caller fills the payload. False if the ring is full.</summary>
		public bool WriteCollision(uint type, int payloadBytes, Action<IntPtr> fill)
		{
			long ring = Proto.OffCollisionRing;
			long msg = (8 + payloadBytes + 7) & ~7L;
			long tail = Volatile.Read(ref I64(ring + Proto.CrTail));
			long head = _collisionHead;
			long pos = head % Proto.CrDataBytes;
			long pad = pos + msg > Proto.CrDataBytes ? Proto.CrDataBytes - pos : 0;
			if (Proto.CrDataBytes - (head - tail) < msg + pad) return false;
			if (pad > 0)
			{
				*(uint*)(_base + ring + Proto.CrData + pos) = Proto.ColPad;
				*(uint*)(_base + ring + Proto.CrData + pos + 4) = 0;
				head += pad;
				pos = 0;
			}
			byte* at = _base + ring + Proto.CrData + pos;
			*(uint*)at = type;
			*(uint*)(at + 4) = (uint)payloadBytes;
			fill(new IntPtr(at + 8));
			_collisionHead = head + msg;
			Volatile.Write(ref I64(ring + Proto.CrHead), _collisionHead);
			return true;
		}

		// ---- event ring (MC -> host) --------------------------------------------------------

		public struct GuestEvent
		{
			public uint Type, FormId;
			public float A, B, C, D;
			public uint Flags, Weapon;
		}

		public bool TryPopEvent(out GuestEvent ev)
		{
			long ring = Proto.OffEventRing;
			long head = Volatile.Read(ref I64(ring + Proto.ErHead));
			long tail = I64(ring + Proto.ErTail);
			ev = default;
			if (head - tail > Proto.EventRingEntries) tail = head - Proto.EventRingEntries;
			if (tail >= head) return false;
			byte* e = _base + ring + Proto.ErData + (tail & (Proto.EventRingEntries - 1)) * Proto.EventBytes;
			ev.Type = *(uint*)e;
			ev.FormId = *(uint*)(e + 4);
			ev.A = *(float*)(e + 8);
			ev.B = *(float*)(e + 12);
			ev.C = *(float*)(e + 16);
			ev.D = *(float*)(e + 20);
			ev.Flags = *(uint*)(e + 24);
			ev.Weapon = *(uint*)(e + 28);
			Volatile.Write(ref I64(ring + Proto.ErTail), tail + 1);
			return true;
		}

		// ---- world entities: just the block selection for now ----------------------------------

		public bool ReadSelection(out UnityEngine.Vector3 min, out UnityEngine.Vector3 max)
		{
			long b = Proto.OffWorldEntities;
			min = max = default;
			for (int attempt = 0; attempt < 8; attempt++)
			{
				int seq0 = Volatile.Read(ref I32(b + Proto.WeSeq));
				if ((seq0 & 1) != 0) continue;
				bool has = I32(b + Proto.WeHasSelection) != 0;
				min = new UnityEngine.Vector3(F32(b + Proto.WeSelMin), F32(b + Proto.WeSelMin + 4), F32(b + Proto.WeSelMin + 8));
				max = new UnityEngine.Vector3(F32(b + Proto.WeSelMax), F32(b + Proto.WeSelMax + 4), F32(b + Proto.WeSelMax + 8));
				if (Volatile.Read(ref I32(b + Proto.WeSeq)) == seq0) return has;
			}
			return false;
		}

		// ---- overlay (hand + HUD + screens) ---------------------------------------------------

		/// <summary>
		/// Takes the newest published overlay frame, if there is one we haven't seen. The pointer stays
		/// valid until the next call (Minecraft never writes the front slot).
		/// </summary>
		public bool TryTakeOverlay(out IntPtr pixels, out int width, out int height, out bool bottomUp)
		{
			pixels = IntPtr.Zero;
			width = height = 0;
			bottomUp = false;
			ref int state = ref I32(Proto.OffOverlayCtl + Proto.OcState);
			if ((Volatile.Read(ref state) & Proto.OverlayDirty) == 0) return false;
			int old = Interlocked.Exchange(ref state, _overlayFront);
			_overlayFront = old & 3;
			long hdr = Proto.OffOverlaySlotHdr + _overlayFront * Proto.SlotHdrSize;
			width = I32(hdr + Proto.ShWidth);
			height = I32(hdr + Proto.ShHeight);
			bottomUp = (I32(hdr + Proto.ShFlags) & 1) != 0;
			if (width <= 0 || height <= 0 || width > Proto.MaxOverlayW || height > Proto.MaxOverlayH) return false;
			pixels = new IntPtr(_base + Proto.OffOverlayPixels + _overlayFront * Proto.OverlaySlotBytes);
			return true;
		}

		// ---- render ring (MC -> host) ----------------------------------------------------------

		/// <summary>
		/// Hands each complete render message to <paramref name="handle"/> (type, payload pointer, payload
		/// bytes) until the ring is empty or <paramref name="byteBudget"/> is spent. The payload is only
		/// valid during the callback.
		/// </summary>
		public int DrainRender(Action<uint, IntPtr, int> handle, long byteBudget)
		{
			long ring = Proto.OffRenderRing;
			long head = Volatile.Read(ref I64(ring + Proto.RrHead));
			long tail = I64(ring + Proto.RrTail);
			long spent = 0;
			int count = 0;
			while (tail < head && spent < byteBudget)
			{
				long pos = tail % Proto.RrDataBytes;
				byte* at = _base + ring + Proto.RrData + pos;
				uint type = *(uint*)at;
				int payload = *(int*)(at + 4);
				if (type == Proto.RenPad)
				{
					tail += Proto.RrDataBytes - pos;
					continue;
				}
				long msg = (8 + payload + 7) & ~7L;
				handle(type, new IntPtr(at + 8), payload);
				tail += msg;
				spent += msg;
				count++;
			}
			Volatile.Write(ref I64(ring + Proto.RrTail), tail);
			return count;
		}

		public void Dispose()
		{
			// A stale heartbeat makes Minecraft see us gone at once and hold its player where it stands.
			// Not a pid change: Minecraft took that for a new game, dropped all our collision while the
			// link still looked alive for 2 s, and its player fell out of the world.
			Volatile.Write(ref I64(Proto.OffHeader + Proto.HHostHeartbeat), 0L);
			UnmapViewOfFile(new IntPtr(_base));
			CloseHandle(_mapping);
		}
	}
}
