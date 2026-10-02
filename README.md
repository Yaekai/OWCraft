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

## Install (players)
You need Windows, Outer Wilds (Steam) and Minecraft Java Edition.

1. **Outer Wilds:** install the [Outer Wilds Mod Manager](https://outerwildsmods.com/mod-manager/) and
   start Outer Wilds from it once.
2. **OWCraft:** download `Yaekai.OWCraft-<version>.zip` from
   [Releases](https://github.com/Yaekai/OWCraft/releases). Unzip it into
   `%AppData%\OuterWildsModManager\OWML\Mods\`, so you get a `Mods\Yaekai.OWCraft` folder.
3. **Minecraft:** install [Fabric Loader](https://fabricmc.net/use/installer/) 0.19.5 or newer for
   Minecraft **26.3**. Put two files in your `.minecraft\mods` folder:
   - `skycraft-<version>+owcraft.<n>.jar` from [Releases](https://github.com/Yaekai/OWCraft/releases);
   - [Fabric API](https://modrinth.com/mod/fabric-api) for 26.3.
4. Start Minecraft with the Fabric profile in the normal launcher.
5. Start Outer Wilds from the Mod Manager.

## Play
1. Once Outer Wilds connects, Minecraft opens its own void world (or creates it the first time). The
   planet supplies the ground.
2. In Outer Wilds, land somewhere, stand on the ground and press **F6**.
3. Minecraft controls apply:
   - **O** opens Minecraft's menu;
   - **F5** switches to third person (Minecraft's body is drawn on the planet), like in Minecraft;
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

## Build from source
You need the .NET SDK and a JDK 25.

**Outer Wilds side:**
```bash
dotnet build host/OWCraft.csproj -c Release
```
If Outer Wilds isn't in the default Steam folder, add `-p:GameDir="D:\Games\Outer Wilds"`.
When the Mod Manager is installed, the build copies `OWCraft.dll`, `manifest.json` and
`default-config.json` into `%AppData%\OuterWildsModManager\OWML\Mods\Yaekai.OWCraft`.

**Minecraft side:** SkyCraft's Fabric mod plus `guest/skycraft-owcraft.patch`. The patch:
- skips presenting Minecraft's own hidden window while linked (this took Minecraft from 25 to 60 fps);
- reads the overlay back at once, so inventory screens keep up with the mouse;
- sets OWCraft's defaults: the `Local\OWCraft_v1` link, no "Skyrim destruction" (Outer Wilds can't
  carve its planets), Minecraft keeps running when Outer Wilds closes, and no Discord status;
- keeps speed and elytra flight when Outer Wilds moves you to a new planet tile, and brings the
  mobs and items around you along;
- lets mobs path over the planet surface, and holds mobs and items still where the planet's ground
  hasn't streamed in yet (instead of letting them fall into the void);
- sends arrows, tridents and dropped items through the scene so Outer Wilds draws them;
- adds a frame-time log.

```bash
git clone https://github.com/chasmlol/SkyCraft.git
cd SkyCraft
git checkout bfcaf178524b92c2cdeb88e4ce0f13ef9ded6f32
git apply path/to/OWCraft/guest/skycraft-owcraft.patch
cd fabric
./gradlew build       # the jar lands in build/libs
./gradlew runClient   # or run it straight from source
```

## Known limitations
- Translucent blocks (glass, water, ice) are drawn opaque.
- Outer Wilds' own player doesn't collide with placed blocks yet. Only the Minecraft player does.
- The Minecraft world doesn't follow Outer Wilds' day/night.
- Nether portals and other dimensions aren't supported.
- Mobs only find the planet's ground within about 40 blocks of you. Further away they wait in place.

## Repository layout
- `host/`: the OWML mod (C#). `Link/` holds the shared-memory protocol, `World/` the planet tiles,
  collision and rendering, `Player/` the movement, input and commands, `UI/` the overlay.
- `guest/`: the patch to SkyCraft's Fabric mod.
- `tests/VoxelTest`: a unit test for the collision voxelizer.
- `tools/check-logs.py`: reads both games' logs after a play test and lists known problems.
- `docs/DESIGN.md`: the architecture.
- `CHANGELOG.md`: what changed in each release.
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
