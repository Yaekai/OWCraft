namespace OWCraft.Link
{
	/// <summary>
	/// Byte layout of the shared mapping. Mirrors guest/skycraft_protocol.h (SkyCraft protocol v11,
	/// MIT, chasmlol/SkyCraft) so the Minecraft side can be SkyCraft's Fabric mod. The host
	/// (Outer Wilds here, Skyrim there) creates the mapping; Minecraft opens it by name.
	/// All coordinates in the mapping are Minecraft space: blocks, +Y up, +Z south (right-handed).
	/// </summary>
	public static class Proto
	{
		public const uint Magic = 0x43594B53; // "SKYC"
		public const uint Version = 11;
		// Not SkyCraft's default name, so a real SkyCraft/Skyrim pair can't cross wires with us.
		// Minecraft is started with -Dskycraft.link=Local\OWCraft_v1.
		public const string DefaultMappingName = "Local\\OWCraft_v1";

		public const long OffHeader = 0x0;
		public const long OffSkyState = 0x100;
		public const long OffMcState = 0x200;
		public const long OffOverlayCtl = 0x300;
		public const long OffOverlaySlotHdr = 0x340;
		public const long OffWaterGrid = 0x400;
		public const long OffInputRing = 0x1000;
		public const long OffActorTable = 0x12000;
		public const long OffEventRing = 0x17000;
		public const long OffWorldEntities = 0x1C000;
		public const long OffCollisionRing = 0x20000;
		public const long CollisionRingBytes = 32L << 20;
		public const long OffOverlayPixels = OffCollisionRing + CollisionRingBytes;
		public const int MaxOverlayW = 3840;
		public const int MaxOverlayH = 2160;
		public const long OverlaySlotBytes = (long)MaxOverlayW * MaxOverlayH * 4;
		public const int OverlaySlots = 3;
		public const long OffRenderRing = OffOverlayPixels + OverlaySlotBytes * OverlaySlots;
		public const long RenderRingBytes = 64L << 20;
		public const long MappingBytes = OffRenderRing + RenderRingBytes;

		// header
		public const long HMagic = 0x00, HVersion = 0x04, HHostPid = 0x08, HMcPid = 0x0C, HHostHeartbeat = 0x10, HMcHeartbeat = 0x18;

		// host -> MC state (seqlock)
		public const long SsSeq = 0x00, SsFlags = 0x04, SsWorldId = 0x08, SsCollisionEpoch = 0x0C;
		public const long SsPosX = 0x10, SsPosY = 0x18, SsPosZ = 0x20, SsYaw = 0x28, SsPitch = 0x2C;
		public const long SsTeleportSeq = 0x30, SsViewportW = 0x34, SsViewportH = 0x38, SsGameHour = 0x3C;
		// OWCraft: the last tile change as a 3x3 rotation (row-major, old tile's Minecraft axes -> new tile's),
		// so Minecraft can carry the mobs around the player along with it.
		public const long SsShiftRot = 0x40;
		public const uint HostInGame = 1, HostMenuOpen = 1 << 1, HostLoading = 1 << 2;
		// OWCraft's own: the current teleport is a tile change (same spot, new Minecraft coordinates).
		public const uint HostReanchor = 1 << 3;

		// MC -> host state (seqlock)
		public const long MsSeq = 0x00, MsFlags = 0x04, MsX = 0x08, MsY = 0x10, MsZ = 0x18, MsYaw = 0x20, MsPitch = 0x24;
		public const long MsEyeHeight = 0x28, MsSensitivity = 0x2C, MsTeleportAck = 0x30, MsGuiScale = 0x34, MsFrameCounter = 0x38;
		public const long MsFov = 0x40, MsBobPhase = 0x44, MsBobAmount = 0x48, MsEyeX = 0x50, MsEyeY = 0x58, MsEyeZ = 0x60;
		public const long MsTickQpc = 0x68, MsPrevX = 0x70, MsCurX = 0x88, MsTickMs = 0xB8, MsCameraMode = 0xC0, MsCameraDistance = 0xC4;
		public const uint McInWorld = 1, McScreenOpen = 1 << 1, McOnGround = 1 << 2, McSneaking = 1 << 3;
		public const uint McSprinting = 1 << 4, McDead = 1 << 5, McSwimming = 1 << 6, McFlying = 1 << 7;
		public const uint McInvulnerable = 1 << 8; // OWCraft: Creative or Spectator

		// overlay triple buffer
		public const long OcState = 0x00, OcFramesPublished = 0x08;
		public const int OverlayDirty = 1 << 2;
		public const long SlotHdrSize = 0x40, ShWidth = 0x00, ShHeight = 0x04, ShFlags = 0x08, ShFrameId = 0x10;

		// input ring (host produces)
		public const int InputRingEntries = 4096;
		public const long IrHead = 0x00, IrTail = 0x40, IrData = 0x80;
		public const ushort InKey = 1, InMouseButton = 2, InScroll = 3, InCursor = 4, InText = 5, InReleaseAll = 6, InHurt = 7, InOpenMenu = 8;

		// event ring (MC produces)
		public const int EventRingEntries = 512;
		public const long ErHead = 0x00, ErTail = 0x40, ErData = 0x80, EventBytes = 32;
		public const uint EvHitActor = 1, EvPlayerDied = 2, EvExplosion = 3, EvArrowStuck = 4, EvSkillUse = 5;

		// world entities (MC -> host, seqlock)
		public const long WeSeq = 0x00, WeCount = 0x04, WeHasSelection = 0x08, WeSelMin = 0x0C, WeSelMax = 0x18, WeRecords = 0x40;
		public const int MaxWorldEntities = 160;
		public const long WorldEntityBytes = 96;

		// collision ring (host produces)
		public const long CrHead = 0x00, CrTail = 0x40, CrData = 0x80;
		public const long CrDataBytes = CollisionRingBytes - CrData;
		public const uint ColPad = 0, ColClear = 1, ColRegion = 2, ColTris = 3;
		public const int ColRegionHeaderBytes = 32, ColBlockBytes = 80, ColTriBytes = 40;
		public const uint TriStairHelper = 1, TriDiggable = 2, TriGhost = 4, TriTerrain = 8;
		/// <summary>Minecraft streams collision as cubes of this many blocks (SkyCollision.REGION_SIZE).</summary>
		public const int RegionSize = 8;

		// render ring (MC produces)
		public const long RrHead = 0x00, RrTail = 0x40, RrData = 0x80;
		public const long RrDataBytes = RenderRingBytes - RrData;
		public const uint RenPad = 0, RenAtlas = 1, RenSection = 2, RenClearAll = 3, RenTexture = 4, RenAvatar = 5, RenScene = 6;
		public const uint RenAtlasRegion = 7, RenLights = 8, RenRagdoll = 9, RenSolids = 10, RenDug = 11;
		public const int RenVertexBytes = 32;
	}
}
