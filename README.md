# OWCraft: Minecraft inside Outer Wilds

Fly your ship to a planet, get out, press **F6**, and you're playing real Minecraft there. Break and
place blocks, open your inventory, light TNT, and build a house on Timber Hearth. Press **F6** again
to get back into your spacesuit. The house stays on the planet.

This is a *passthrough* mod, built on chasmlol's [SkyCraft](https://github.com/chasmlol/SkyCraft)
(Minecraft in Skyrim). Both games run at the same time and talk over shared memory:

- Minecraft simulates the player and the blocks against the planet's real surface.
- Outer Wilds draws the blocks on the planet with its own lighting, plus Minecraft's hand, HUD and
  screens on top.

See [docs/DESIGN.md](docs/DESIGN.md) for how it works.

**Status: experimental, but playable.** It has been tested on one Windows PC: building, TNT and
walking around planets work at 60 fps. Expect rough edges (see *Known limitations*).

## What you need
- Windows.
- Outer Wilds (Steam), with the [Outer Wilds Mod Manager](https://outerwildsmods.com/mod-manager/)
  (OWML 2.16).
- Minecraft Java **26.3** and a JDK 25, to build and run SkyCraft's Fabric mod from source.
- The .NET SDK, to build this mod.

## Setup

### 1. Minecraft side: SkyCraft plus the OWCraft patch
SkyCraft's Fabric mod is the Minecraft side. `guest/skycraft-owcraft.patch` adds small changes on top
of it:

- skip presenting Minecraft's own hidden window while linked (this took Minecraft from 25 to 60 fps);
- read the overlay back at once, so inventory screens keep up with the mouse;
- a frame-time log.

```bash
git clone https://github.com/chasmlol/SkyCraft.git
cd SkyCraft
git checkout bfcaf178524b92c2cdeb88e4ce0f13ef9ded6f32
git apply path/to/OWCraft/guest/skycraft-owcraft.patch
```

Then in `fabric/run/config/skycraft.properties` (created on first run), set:
```
destruction=false
```
Outer Wilds can't carve its planets, so this keeps explosions pure Minecraft. Without it you get
stray stone after every blast.

Start Minecraft from `fabric` (PowerShell) with the OWCraft link name:
```powershell
$env:JAVA_TOOL_OPTIONS = '-Dskycraft.link=Local\OWCraft_v1'
.\gradlew.bat runClient
```
The link name keeps it from crossing wires with a real Skyrim/SkyCraft setup.

### 2. Outer Wilds side: this mod
```bash
dotnet build host/OWCraft.csproj -c Release
```
If Outer Wilds isn't in the default Steam folder, add `-p:GameDir="D:\path\to\Outer Wilds"`.
When the Mod Manager is installed, the build copies `OWCraft.dll`, `manifest.json` and
`default-config.json` into `%AppData%\OuterWildsModManager\OWML\Mods\Yaekai.OWCraft`.

## Play
1. Start Minecraft and open a world. A superflat **void** world is best, because the planet supplies
   the ground.
2. Start Outer Wilds, land somewhere, stand on the ground, and press **F6**.
3. Minecraft controls apply:
   - **O** opens Minecraft's menu;
   - **Esc** pauses Outer Wilds, or closes a Minecraft screen;
   - **F6** leaves Minecraft mode.

Each patch of planet maps to its own fixed area of the Minecraft world, so whatever you build is
saved by Minecraft. It is still there after time loops and restarts.

Settings (Mod Manager):

| Setting | Default | What it does |
|---|---|---|
| Toggle key | F6 | Enter and leave Minecraft mode |
| Minecraft menu key | O | Open Minecraft's pause menu |
| Minecraft mode FPS cap | 60 | Outer Wilds frame cap while in Minecraft mode |
| Minecraft walk speed | 6.0 | Walking speed in m/s (vanilla Minecraft is 4.3) |
| Verbose logging | off | Extra diagnostics in the OWML log |

## Known limitations
- Translucent blocks (glass, water, ice) are drawn opaque.
- Outer Wilds' own player doesn't collide with placed blocks yet. Only the Minecraft player does.
- There's no visible Steve body, and the Minecraft world doesn't follow Outer Wilds' day/night.
- Nether portals and other dimensions aren't supported.
- Mobs path across the planet surface as if it were Minecraft terrain, which isn't always right.

## Repository layout
- `host/`: the OWML mod (C#). `Link/` holds the shared-memory protocol, `World/` the planet tiles,
  collision and rendering, `Player/` the movement, input and commands, `UI/` the overlay.
- `guest/`: the patch to SkyCraft's Fabric mod.
- `tests/VoxelTest`: a unit test for the collision voxelizer.
- `docs/DESIGN.md`: the architecture.
- `MODLOG.md`: the full development log, test by test.

## Honesty note
This mod was written by an AI (Claude, by Anthropic), directed and play-tested by Yaekai. It ports
SkyCraft's Skyrim-side design to Outer Wilds. [MODLOG.md](MODLOG.md) records what was done and
verified, and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) what was ported.

No game files, decompiled code or Minecraft assets are in this repository. Textures come from your
own Minecraft at runtime.

## License
MIT ([LICENSE](LICENSE)). Parts ported from SkyCraft are MIT too; see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
