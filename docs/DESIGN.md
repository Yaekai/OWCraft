# OWCraft design

Real Minecraft (26.3, Fabric) runs next to Outer Wilds. Minecraft does the simulation: movement,
breaking and placing blocks, inventory, crafting. Outer Wilds does the world and the drawing. The two
talk over one named shared-memory mapping laid out by SkyCraft's protocol v11. Outer Wilds is the
"host" (Skyrim's role in SkyCraft); Minecraft with SkyCraft's Fabric mod is the "guest".

```
Outer Wilds (OWML mod, C#)                         Minecraft + SkyCraft Fabric mod
  SkyState: feet, look, teleportSeq, epoch  ───►   follows: teleports, sets its look
  input ring: keys, buttons, scroll, text   ───►   plays them
  collision ring: planet surface, 8^3 regions ─►   collides with it as if it were blocks
  ◄── McState: feet (raw 20 Hz ticks), flags, sensitivity
  ◄── render ring: atlas + chunk section meshes
  ◄── overlay: hand, HUD, screens (RGBA, triple-buffered)
```

## The round-planet problem: anchors
Minecraft's world is flat with a fixed "down", and planets are round. `AnchorGrid` tiles each planet
as an equi-angular cube sphere, with tiles of about 40 m. Each tile gets:

- a fixed tangent frame on the planet body;
- a fixed, private 512×512-block patch of the Minecraft world.

Patch X comes from face and tile index; patch Z comes from the planet's slot and tile index. Every
tile origin sits at Y 192.

The same tile always maps to the same Minecraft blocks. So whatever you build stays where you built
it, across tile changes, time loops and restarts, because Minecraft saves it.

Within a tile, Unity anchor-local `(x, y, z)` equals Minecraft `(x − X0, y − 192, −(z − Z0))`. The Z
flip turns Unity's left-handed space into Minecraft's right-handed one.

When you walk more than 0.6 tile from your tile's centre into another tile, the player is
re-anchored:

1. Same world spot, new Minecraft coordinates.
2. `teleportSeq` is bumped, and Minecraft teleports its player.
3. The collision epoch is bumped, and Minecraft drops the old tile's collision.

Blocks built across a tile edge belong to the tile whose patch they're in. They're drawn on the
patch they were built in, so a house straddling the line stays whole.

## Puppet
F6 (only while standing on a planet or moon) does the following:

- suspends the Outer Wilds player body on the planet (kinematic, parented);
- locks movement, turns off its collider, and sets the input mode to `None`;
- teleports Minecraft's player to the matching spot.

Every frame after that:

- **Position.** The Outer Wilds body is placed at Minecraft's feet, interpolated between Minecraft's
  last two physics ticks on Outer Wilds' own clock (same as Minecraft's partial ticks).
- **Look.** The mod integrates the mouse with Minecraft's sensitivity curve and writes yaw and pitch
  to SkyState. Minecraft follows. Yaw turns the Outer Wilds body; pitch goes to the camera
  (`SetDegreesY`, clamped to ±80°).
- **Leaving.** F6 again, death, a scene change, or Minecraft disappearing for 5 s ends the mode. The
  body is unsuspended with the planet's velocity.

## Collision (`CollisionStreamer`, ported from SkyCraft's `Collision.cpp`)
- **Which regions.** 8³-block regions around the feet: radius 5, 3 below, 2 above, nearest first.
  At most 3 regions or 2.5 ms per frame. The 2×2×2 regions under the player are refreshed every
  second, because sand moves.
- **Harvest (main thread).** `Physics.OverlapBox` in the anchor frame keeps only colliders that
  belong to the planet's `OWRigidbody`. Each collider becomes triangles in Minecraft space:
  - boxes, spheres and capsules are tessellated;
  - readable mesh colliders contribute their triangles, cached per mesh;
  - anything else (or meshes without Read/Write) is sampled with `Collider.Raycast` on a half-block
    grid.
- **Voxelize (worker thread).** Triangles become 1/8-block voxels. Faces between 50° and 84° are
  coarsened to whole-block columns so Minecraft's step rules decide what's climbable. The worker
  sends `COL_TRIS` (exact smooth surface), then `COL_REGION` (voxels).

## Drawing (`BlockRenderer`)
- **Sections.** Minecraft sends each 16³ section already meshed: positions, atlas UVs, colour,
  light, and normals packed in flags. Each section becomes a Unity mesh under its tile's anchor
  object on the planet, so blocks turn with the planet, take the sun's light and shadows, and are
  visible from space.
- **Shader.** `Particles/Standard Surface`, the game's own: lit, vertex colour, alpha cutout.
  Translucent faces (water, glass) go in a second submesh with blending.
- **Light.** Minecraft's sky and block light are baked into vertex colour, so caves are dark.
- **Time loops.** Meshes are kept as persistent assets. After a loop reloads the scene they're put
  back on the new planet objects, because Minecraft doesn't resend sections it already sent.

## Overlay
Minecraft's hand, HUD and screens arrive as an RGBA frame. They're drawn full-screen with IMGUI
while Minecraft mode is on, plus a drawn cursor while a Minecraft screen is open.

Controls:
- **Escape** pauses Outer Wilds, unless a Minecraft screen is open, in which case it closes that
  screen. A Harmony prefix on `PauseCommandListener.Update` handles this.
- **O** opens Minecraft's own menu.

## Not done yet
- Entities and mobs; the player's own model in third person; particles (`RenScene` / `RenAvatar`).
- Placed blocks are not solid for Outer Wilds itself outside Minecraft mode, so you can walk
  through your house when not in Minecraft mode.
- Water grid, day/night sync (`GameHour` is fixed at noon), hurt events.
- Moving surfaces inside a planet (Brittle Hollow fragments, sand columns) are only refreshed near
  the player.
- Open questions to settle in game:
  - pitch sign;
  - eye-height alignment;
  - overlay alpha (straight or premultiplied);
  - how many planet mesh colliders are readable.
