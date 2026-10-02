# Changelog

All notable changes to OWCraft. Versions are the Outer Wilds mod's (`host/manifest.json`); the
matching Minecraft jar is listed with each release. [MODLOG.md](MODLOG.md) has the full development
log, test by test.

## [0.1.1] - 2026-10-02
Minecraft jar: `skycraft-0.1.2+owcraft.5.jar`.

### Added
- **F5 third person.** Minecraft's own player body is drawn on the planet (front and back view). The
  camera stops at walls instead of going through them.
- The Outer Wilds player model is hidden in Minecraft mode.
- **Giant's Deep's islands** (and anything else without gravity of its own) can be built on: each one
  is a single flat area with "up" matching gravity. More than 160 blocks out, you carry on over the
  planet it floats on.
- **Invincible in Creative and Spectator:** the Outer Wilds body no longer suffocates or takes damage
  when Minecraft can't be hurt.
- Mobs path over the planet's surface (Minecraft's pathfinding sees the planet's ground).
- Mobs and items are held in place where the planet's ground hasn't streamed in yet, instead of
  falling into the void.
- Mobs and items near you come along when you move to a new planet tile.
- Arrows, tridents and dropped items are drawn.
- `tools/check-logs.py`: reads both games' logs after a play test and lists known problems.

### Changed
- **Smoother elytra flight:** in the air you stay on a planet tile much longer before moving to the
  next one (up to 160 blocks, or until "up" leans 20 degrees). That's 4 to 6 times fewer tile changes.
- Elytra speed and flight are kept across tile changes.
- Fewer collision hitches: refreshing the ground around you no longer re-scans tree trunks and walls
  from the side.

### Fixed
- Freezing in mid-air every few seconds while flying.
- The world shaking and jumping after flying off one of Giant's Deep's islands.
- The camera turning sideways on Giant's Deep's islands.
- Footstep sounds while flying or falling.
- Walking through trees and walls.

## [0.1.0] - 2026-10-02
Minecraft jar: `skycraft-0.1.2+owcraft.1.jar`.

First public release.
- Play real Minecraft on Outer Wilds planets: land, press **F6**, and break and place blocks, use
  your inventory, light TNT.
- Minecraft simulates the player against the planet's real surface. Outer Wilds draws the blocks
  with its own lighting, plus Minecraft's hand, HUD and screens.
- Each patch of planet has its own fixed area of the Minecraft world, so builds are saved across time
  loops and restarts.
- Mod Manager settings: toggle key, menu key, FPS cap, walking speed, verbose logging.

[0.1.1]: https://github.com/Yaekai/OWCraft/releases/tag/v0.1.1
[0.1.0]: https://github.com/Yaekai/OWCraft/releases/tag/v0.1.0
