# MODLOG

## 2026-10-02: Outer Wilds host for SkyCraft protocol v11

**Goal (user):** play Minecraft inside Outer Wilds: fly to a planet, get out, build a Minecraft
house there. A true passthrough, as in SkyCraft.

**Environment checked**
- Outer Wilds: Unity 2019.4.39, Mono.
- OWML / Mod Manager: not installed. The OWML 2.16.3 NuGet package is used to compile only.
- Minecraft 26.3: installed.
- JDK 21 and 25: present.

**Approach.** Implemented the *host* side of SkyCraft's protocol v11 as an OWML mod, so SkyCraft's
Fabric mod can be the Minecraft side unchanged. Reference sources were read outside the repo:

- SkyCraft at `bfcaf178`, in a local clone;
- an ilspycmd 8.2 decompile of Assembly-CSharp, in a local folder.

Neither is copied into this repo.

**Done**
- `Link/`:
  - the mapping layout;
  - `SharedLink` (seqlocks, rings, overlay triple buffer, heartbeat);
  - a custom mapping name, `Local\OWCraft_v1`.
- `World/AnchorGrid`: cube-sphere tiles per planet, each with a fixed tangent frame and a fixed
  Minecraft area.
- `World/CollisionStreamer`: harvest of planet colliders in Unity, plus a port of SkyCraft's
  voxelizer on a worker thread.
- `World/BlockRenderer`:
  - atlas, sections and animated atlas regions on the game's `Particles/Standard Surface` shader;
  - meshes survive time-loop scene reloads.
- `Player/PlayerDriver`:
  - puppet via `OWRigidbody.Suspend` on the planet;
  - tick interpolation;
  - re-anchoring;
  - an off-tile guard;
  - a Harmony patch on pause.
- `Player/InputForwarder`: InputSystem keys mapped to HID, mouse, scroll, text, cursor.
- `UI/OverlayRenderer`.
- `OWCraft.cs`: the ModBehaviour.

**Verified**
- `dotnet build -c Release`: 0 warnings, 0 errors.
- Every Outer Wilds API used was checked in the decompile:
  - `Suspend` / `Unsuspend`, `LockMovement`, `SetColliderActivation`;
  - the `InputMode` stack, `PauseCommandListener`, `PlayerState.IsDead`;
  - `NotificationManager`, `LoadManager.OnCompleteSceneLoad`.
- `tests/VoxelTest` (runs without the game). A flat floor at y = 64 gives exactly 64 blocks, each
  with only its bottom voxel layer solid. Message types, header and epoch, and ring head accounting
  are all correct. Result: ALL PASSED.

**Not verified (needs the game + Minecraft):** everything in game. Open questions are listed in
`docs/DESIGN.md`.

**Not done, waiting for the user**
- Building SkyCraft's Fabric mod (Gradle downloads and runs external code). Auto mode refused this,
  so it needs the user's go-ahead.
- Installing OWML / the Mod Manager into the game.
- Choosing how to launch Minecraft with `-Dskycraft.link=Local\OWCraft_v1`.

## 2026-10-02: OWML installed by the user
- OWML 2.16.3, matching the version we compile against.
- The build copied `OWCraft.dll`, `manifest.json` and `default-config.json` into
  `%AppData%\OuterWildsModManager\OWML\Mods\Yaekai.OWCraft`. Nothing was written to the game folder.
- Not yet started in game.

## 2026-10-02: first in-game run (Outer Wilds only, Minecraft not running)
- The mod loads, the planets scan (Timber Hearth, Brittle Hollow, the Twins and others), and there
  are no exceptions in Player.log. F6 correctly refused with "Minecraft isn't connected".
- **Bug: link name.** The log printed `Local/OWCraft_v1`, because OWML's settings turned the
  backslash into a slash. That name would never have matched Minecraft's `Local\OWCraft_v1`. Fixed:
  the `linkName` setting is removed and the name is hard-coded.
- **Bug: shader.** At the title screen the mod picked `Standard`, which ignores vertex colours, so
  grass would have been grey and caves unlit. Fixed: `BlockRenderer.PickShader()` runs again after
  each solar-system load, and swaps the shader on the existing materials. With verbose logging on,
  it lists the loaded shaders when it has to fall back.
- Rebuilt: 0 warnings, 0 errors. Redeployed to the Mods folder.

## 2026-10-02: first F6 test with Minecraft running
- The link came up (`Minecraft connected`, and SkyCraft logged `Skyrim link up`). The atlas and 9
  sections arrived, F6 entered Minecraft mode on Timber Hearth, and Minecraft teleported to the
  matching spot.
- **Bug: the player suffocated** (Player.log: "Player was killed by Asphyxiation", 5 s after F6, with
  no suit yet). `OWRigidbody.Suspend` turns off every child collider, including the oxygen
  detector's. Fixed: the player is now pinned the way `PlayerAttachPoint` does it (`MakeKinematic`,
  parent to the planet, body collider off). On exit, unparent, `MakeNonKinematic`, and set the
  planet's point velocity.
- **Bug: blurry "potato" picture.** Minecraft's pause screen was open: Minecraft paused on focus loss
  before the link disabled that. Its blurred backdrop was drawn full-screen as the overlay. Fixed:
  F6 sends Escape when a Minecraft screen is open.
- Shader: `Particles/Standard Surface` isn't loaded in the solar system either; still `Standard`
  (no vertex colours). This build logs the properties of `Outer Wilds/Particles/Cutoff` and
  `/Alpha` so the next run can pick one.
- Rebuilt: 0 warnings, 0 errors. Not yet retested in game.

## 2026-10-02: second F6 test (works: HUD, placing blocks and torches)
- **Blocks looked hollow/"shallow".** The Z flip mirrors the mesh and the triangles weren't
  reversed, so front faces pointed inward and were culled. Fixed: indices are now (t, t+2, t+1).
- **Death: "Player killed from impact with BatchedMeshColliders_4 … TimberHearth_Body".** Solid
  player colliders other than the body capsule stayed on and brushed the planet, which is a moving
  rigidbody. Fixed: every enabled non-trigger collider under the player is turned off for the mode
  and restored on exit. Triggers (oxygen, hazards, fluids) stay on.
- **Choppy movement.** The likely cause: Rigidbody interpolation pulled the body back to the last
  physics step after we placed it. Fixed: interpolation is off during the mode and restored on exit.
  Unconfirmed.
- **Bow "didn't fire".** Mouse codes match the protocol (SDL 1 L / 2 M / 3 R). Arrows are entities,
  and entities aren't drawn yet (`RenScene` not implemented), so the shots were probably invisible.
- **Shader.** The log listed `Outer Wilds/Particles/Cutoff`'s properties (the Standard set plus
  `_CameraFadeDist`, `_Ambient`, queue 2450). It's now preferred over `Standard`, with
  `_CameraFadeDist` = 0. Translucent faces use it too for now (cutout, no blending).
- Rebuilt: 0 warnings, 0 errors. Not yet retested.

## 2026-10-02: third test: "the character is falling after F6"
- No death this time. `Outer Wilds/Particles/Cutoff` was picked as the block shader.
- Theory: Minecraft gets no usable ground under the player, so it falls through the Void world and
  the Outer Wilds body follows it into the planet. The previous "impact" death fits this too.
  Neither side logs received collision, so this is unconfirmed.
- Added diagnostics: every 5 s in Minecraft mode the mod logs Minecraft's feet, plus regions
  harvested (and how many had no ground), colliders overlapped and owned by the planet, triangles,
  unreadable meshes, and the planet's physics-vs-transform offset. That last one checks whether
  rigidbody interpolation shifts `Collider.Raycast` and `OverlapBox` against transform-space anchors.
- Rebuilt: 0 warnings, 0 errors.

## 2026-10-02: entities, and a faster tile change (from reading the logs)
- **Entities are drawn.** Mobs, animals, arrows, dropped items, block entities (chests, beds, signs)
  and particles now appear. Minecraft already sent them every frame as one posed mesh (`RenScene`),
  plus their textures (`RenTexture`), but the mod ignored both.
- New `World/SceneRenderer`:
  - keeps one dynamic mesh on the planet tile under the scene origin, with one submesh per batch;
  - textures are point-filtered and use the same row order as the atlas;
  - materials are clones of the block material, and are recloned when the block shader changes;
  - textures are dropped on `RenClearAll` (SkyCraft resends them after a reset).
- The section vertex decode is shared (`BlockRenderer.DecodeVertices`). Vertices with no normal
  (particles) are now lit as top faces instead of having a zero normal.
- **Tile-change hitch.** After a re-anchor Minecraft freezes the player until there's ground under
  it. The first frame after a collision reset now harvests up to 18 regions (12 ms budget) instead
  of 3, and the region under the feet comes second, right after the feet's own.
- Rebuilt: 0 warnings, 0 errors. Not yet tested in game; the previous build's diagnostics weren't
  tested either.

## 2026-10-02: fourth test: wind sound, two impact deaths (in water I placed, and near the elevator)
- Logs: Minecraft's feet held a steady height the whole time, so Minecraft wasn't falling. Both
  deaths were "Player killed from impact with BatchedMeshColliders_N … TimberHearth_Body".
- **Cause (from the decompile).** `PlayerBody.ManagedFixedUpdate` takes the player's velocity from
  `_activeRigidbody` minus the static frame velocity. Pinned (kinematic, parented), the body reads
  as still in space, i.e. moving fast relative to the planet. That fast relative motion explains:
  - the constant falling wind;
  - `HighSpeedImpactSensor.CheckForImpacts_New` raycasting along it every physics step, and killing
    the player when the ray reaches terrain.
- **Fix.** Like the game's own seats (`OnAttachPlayerToPoint`), `_activeRigidbody` is set to the
  planet's rigidbody while in Minecraft mode, and restored to the player's own on exit.
- Also seen: SkyCraft refused some water flow into "unknown region" about 28 blocks below the feet
  (outside the harvested regions). Harmless.
- "Movement still weird": cause not identified from the logs; the velocity fix may change it.
  Waiting for a more precise description.
- Rebuilt: 0 warnings, 0 errors. Not yet tested.

## 2026-10-02: fifth test: still falling, hand item gone
- **The `_activeRigidbody` fix was a no-op.** Planets move by `KinematicRigidbody`
  (`OWRigidbody._kinematicSimulation`), so the planet's Unity `Rigidbody.GetPointVelocity` is zero
  too. Replaced with a Harmony postfix on `PlayerBody.ManagedFixedUpdate(float, Vector3)`. While
  pinned it sets `_currentVelocity` to `planet.GetPointVelocity(player)` and recomputes
  `_currentAccel`. That runs before `HighSpeedImpactSensor.FixedUpdate`, which reads `GetVelocity()`.
  The patch logs once, "pinned velocity patch active: game had N m/s relative to the planet", which
  measures the old error.
- **Hand item gone, HUD still there.** This matches Minecraft's third-person camera; F5 was being
  forwarded with F1 to F12. Now:
  - F5 isn't forwarded;
  - in Minecraft mode, any non-first-person `cameraMode` reported by the guest gets an F5 press
    (rate-limited to 0.5 s), which cycles it back, and that's logged.
  Unconfirmed that this was the cause.
- `OnDestroy` (quitting the game while in Minecraft mode) threw a NullReferenceException in
  `PlayerDriver.Exit`; it's now caught.
- Seen: OWCraft logs paused for 29 s (02:10:25 to 02:10:54) and Minecraft logged "link down" in
  that gap, so Outer Wilds stopped running for a while.
- Rebuilt: 0 warnings, 0 errors. Not yet tested.

## 2026-10-02: sixth test (about 30 s in Minecraft mode)
- The velocity patch ran. No deaths and no high-speed impact in Player.log.
- The camera fix worked: "Minecraft camera mode 2: switching it back to first person" was logged
  when the mode started, so the missing hand was Minecraft's third-person view.
- The one-time velocity log came from the first physics step, when the error is still 0, so it
  proved nothing. Replaced it: the 5 s stats line now includes the largest velocity correction.
- The NullReferenceException on quit is the game's own `QuantumMoon.OnDisable`.
- Rebuilt: 0 warnings, 0 errors.

## 2026-10-02: seventh test: falling animation, heavy stutter
- The user asked whether this is because Minecraft is "in the void, falling". It isn't: Minecraft's
  feet stay at a steady height in every stats line.
- **Falling animation.** `PlayerAnimController.LateUpdate` sets `Grounded` from
  `controller.IsGrounded()`, and `LockMovement` clears `_isGrounded`. The controller skips
  `UpdateGrounded` while locked, so in Minecraft mode the driver now sets:
  - `_isGrounded` = true;
  - `_groundBody` = the planet;
  - `_groundCollider` = the collider the player stood on at F6 (`HasGroundControl` reads its
    friction).
- **Walk animation.** Minecraft's tick velocity, (cur − prev) / tickMs turned into world space, is
  added to the pinned velocity. `GetRelativeGroundVelocity` then drives RunSpeedX/Y. It's capped
  at 30 m/s, so the impact sensor stays quiet.
- **Stutter.**
  - Suspect: the GC. `SceneRenderer` allocated new vertex and index arrays every frame. It now
    reuses its buffers and `List`s (`Mesh.SetVertices(List)` and so on).
  - Frame stats were added to the 5 s line: frames over 50 ms, worst frame, the mod's own average
    and worst ms, and gen-0 GC count. The next log will show whether the stutter is GC, mod time,
    or neither.
- Rebuilt: 0 warnings, 0 errors. Not yet tested.

## 2026-10-02: "is Minecraft fps locked low?": tick interpolation clock bug
- Minecraft's frame rate isn't the limit. While linked, SkyCraft's `FramerateLimitTrackerMixin`
  returns 260, and `paceFrame()` draws one Minecraft frame per host frame (SkyState seq).
- **Real bug.** The player follows Minecraft's 20 Hz physics ticks, interpolated by the tick's age.
  SkyCraft stamps `tickQpc` with `QueryPerformanceCounter`, but `PlayerDriver` compared it with
  `Stopwatch.GetTimestamp()`. Under Mono that counts 100 ns units from its own origin, so the age was
  nonsense, the interpolation factor sat clamped at 0 or 1, and the player moved in 20 Hz steps.
  Fixed: kernel32 `QueryPerformanceCounter` / `QueryPerformanceFrequency` via P/Invoke.
- The stats line now shows the tick age (it should stay roughly within 0 to 50 ms).
- Rebuilt: 0 warnings, 0 errors. Not yet tested.

## 2026-10-02: eighth test (ran the 02:25 build, before the QPC fix)
- **Report:** slight shaking with F6 on and off; torches give no light; movement bad; the view
  "realigns" on leaving; a splash at the feet; blocks placed closer than aimed; going through some
  blocks when climbing; a creeper explodes but can't path to the player.
- The log has no "tick age", so Outer Wilds was started (02:25) before the QPC build was deployed
  (02:28). The stutter result doesn't count yet.
- **View tilt (the "realign").** The view was turned to the tile's flat frame, which tilts up to ~5°
  away from the planet's real up at the tile edges, and jumps at tile changes. Now the heading is
  kept in the planet's local frame and re-levelled to the radial up every frame. Minecraft's
  yaw/pitch are derived from the camera's world direction in the tile frame, so its aim matches.
- **Aim / placement offset.** The Outer Wilds camera sat about 0.2 m above Minecraft's eye. The body
  is now shifted each frame so the camera is exactly at Minecraft's eye (interpolated eye height:
  sneaking included). Pitch goes to ±90° (the game's controller stops at 80°) by setting the camera's
  local rotation after its FixedUpdate.
- **Torch light.** `RenLights` is handled (new `LightRenderer`): one Unity point light per
  light-emitting block, using Minecraft's colour. Range is about 0.9 × level, intensity scales with
  level, and flames and lava flicker. Lights are rebuilt after a scene load.
- **Mod frame cost.** Worst-frame mod time was 30–40 ms.
  - Collision harvesting checked every triangle of a mesh for each region; it now uses a per-mesh
    grid (up to 48 cells per axis).
  - The full-screen overlay is uploaded only while Minecraft mode is on.
  - The stats line now gives the worst ms per part: player, collision, blocks, overlay.
- **Not fixed yet:**
  - Creeper pathfinding: the planet is collision-only for Minecraft, and mobs path over real blocks.
  - The splash at the feet: probably rain; `/weather clear` to check.
- Rebuilt: 0 warnings, 0 errors.

## 2026-10-02: ninth test (the build with the view and eye fixes)
- **Report (with a screenshot):** placed blocks look sunk into the ground; the player feels about
  1.5 blocks tall instead of 2. Request: an F7 key to clear blocks.
- **Log.**
  - Frame cost is down: the mod's worst frame is 12–16 ms (collision ~10 ms, blocks ~5 ms) and no
    frames go over 50 ms in steady play.
  - Tick age is sometimes 100–220 ms, so Minecraft ticks late now and then.
- **Height.** The camera now sits exactly at Minecraft's eye (1.62 m; the player is 1.8 m tall).
  Being taller means changing Minecraft's own size; the camera follows its eye height:
  `/attribute @s minecraft:scale base set 1.11` gives 2.0 m.
- **Sinking.** Blocks sit on Minecraft's 1 m grid, so on a slope part of each block is under the
  terrain. The stats line now also gives the gap between Minecraft's feet and the game's own ground
  under them (a raycast), to rule out a real vertical offset. The enter line logs the game's own eye
  height.
- **F7 clear.** Press F7 twice within 3 s. It types `/fill` commands into Minecraft's chat (new
  `CommandRunner`: T, then the text as characters, then Enter, one command at a time). That's
  32×32×32 per command, ±48 blocks around the player (−16..+80 vertically), plus a `/kill` of
  dropped items. Needs cheats on in the Minecraft world. F7 is no longer forwarded to Minecraft.
- Rebuilt: 0 warnings, 0 errors.

## 2026-10-02: Minecraft's screens lag the mouse
- **Report:** the inventory's hover highlight trails the mouse; Minecraft feels slow.
- **Cause.** Minecraft's 2D layer reached Outer Wilds two or three frames late:
  - SkyCraft copies each frame back from the GPU asynchronously. The copy is only marked done when
    a later frame is presented, and only shipped at the capture after that: about 2 Minecraft
    frames.
  - Minecraft draws one frame per host frame and waits for our state before starting. We sent that
    state at the end of our Update, after collision and blocks.
  - We took the overlay in Update, before Minecraft had finished the frame we had just unblocked.
- **Fixes.**
  - Host: the state goes to Minecraft right after the player update, before collision and blocks.
    The overlay is taken in OnGUI, just before it is drawn.
  - Guest: **first change to SkyCraft's own code** (`FrameExporter.java`, now in
    `guest/skycraft-owcraft.patch`). The frame is read back right away: on OpenGL, mapping the buffer just after
    `glReadPixels` waits for the copy. `-Dskycraft.syncOverlay=false` restores the asynchronous
    copy. Compiled with JDK 25 (`gradlew compileClientJava`).
- **Diagnostics.** The stats line now gives Minecraft fps, overlay fps and our fps.
- Rebuilt: 0 warnings, 0 errors.

## 2026-10-02: log check (the run before the overlay latency fix)
- That run used the previous build, so the new fps numbers aren't in it.
- **Late Minecraft ticks.** The tick age sampled every 5 s reached 93–161 ms; it should stay near
  50 ms. A late tick leaves the camera nothing new to move towards: a freeze, then a jump.
- **Fine:**
  - the mod costs about 1 ms per frame on average, and Outer Wilds runs at about 115 fps;
  - the collision part peaks at 14–21 ms once per 5 s.
- **"Velocity corrected" keeps growing.** That's expected: it compares against the kinematic body's
  frozen velocity, and the real value is written every physics step.
- **More diagnostics, to find the late ticks:**
  - Host: worst tick age per 5 s (not one sample), and the number of frames that waited on a late tick.
  - Guest (`FrameTimings.java`, SkyCraft-side): every 10 s it logs Minecraft's frames, work ms
    (ticks + render + export), pacing wait, overlay readback ms and the worst gap between client
    ticks. This shows whether Minecraft is slow, waiting on us, or stalled by the synchronous readback.
- Rebuilt (host and guest): 0 warnings, 0 errors.

## 2026-10-02: tenth test (overlay latency build)
- **Report:** F3 in Minecraft shows 25 fps and it still feels slow. The feet sink a bit on pressing F6.
- **Log, host side.** Outer Wilds runs at 120–160 fps and the mod costs about 0.5 ms per frame. But
  Minecraft only runs at 8–25 fps, and the worst tick age is 100–250 ms in every 5 s window.
- **Log, guest side (`FrameTimings`).**
  - Minecraft's own work is 40–137 ms per frame, and it never waits on us (pacing wait about 0 ms).
  - The synchronous readback costs up to 36 ms (it waits for the GPU, which Outer Wilds also uses).
  - The work drops from about 130 to about 40 ms as fewer collision regions are sent, so part of it
    is probably Minecraft reacting to new collision.
- **Readback.** It is now synchronous only while a Minecraft screen is open, and asynchronous
  otherwise.
- **Stage timings.** The guest log now splits the frame into ticks, section meshing, entities,
  avatar and overlay capture. The launch command also turns on Java Flight Recorder (built into the
  JDK, about 1% overhead), so hot methods can be read with `jcmd JFR.dump`.
- **Sinking on F6.** The ground gap is 0.00–0.02 m, so the feet are right. But the game's own eye is
  at 1.85 m and Minecraft's at 1.62 m, so the view dropped 0.23 m on entering. The mod now runs
  `/attribute @s minecraft:scale base set 1.11` once per Minecraft session (needs cheats): 2.0 m
  tall, eye at 1.80 m, still fits a 2-block doorway.
- Rebuilt (host and guest): 0 warnings, 0 errors.

## 2026-10-02: eleventh test, Minecraft profiled
- **Report:** a bit more stable, but Minecraft runs at 12–25 fps, and that's the real problem.
- **Stage timings:** SkyCraft's own work is about 2 ms per frame: ticks 0.5, capture 1.7, meshing,
  entities and avatar about 0. The other ~50 ms is inside Minecraft's frame.
- **Java Flight Recorder (`jcmd JFR.dump`, 246 s)**, render thread, native samples:
  - 8437 in `glBindVertexArray` on the first draw (`GlCommandEncoder.setupDraw`);
  - 1224 in `glGetError` inside `GpuBuffer.map`;
  - 364 in `glClientWaitSync` (`awaitSubmit`).
  
  Minecraft sits waiting on the GPU driver. Its own GPU work (a hand and the HUD) is tiny, but it's a
  background process sharing the GPU with Outer Wilds, which ran uncapped at 120–160 fps, so its
  work waits behind the foreground game's. SkyCraft never hit this because Skyrim normally runs
  capped at 60.
  Smaller CPU cost: `SkyAtlas.animate` (animated textures) is the biggest Java hotspot. It wasn't in
  the stage timings.
- **Fix:** a new setting, `minecraftModeFpsCap` (default 60, 0 = off). While Minecraft mode is on,
  vsync is turned off and `Application.targetFrameRate` is set to the cap. It is re-applied if the
  game resets it, and the old values come back on leaving. The setting was also added to the
  installed `config.json`.
- Rebuilt: 0 warnings, 0 errors (host only).

## 2026-10-02: twelfth test, size, walking speed, TNT
- **Report:** a bit better again, but the feet still sink on F6, TNT wouldn't light with redstone or
  fire, and walking should be faster.
- **Minecraft frame pinned at 40.0 ms.** After the fps cap, Minecraft's frame work averaged exactly
  40.0 ms (25 fps), a sign that Windows throttles presents to its hidden window. The guest now skips
  `acquireNextTexture`/present while linked (`MinecraftMixin`, a redirect; `-Dskycraft.present=true`
  turns it back on). Nobody sees that window anyway: the overlay goes through shared memory.
- **Why the scale never ran.** The OWML log never says "Minecraft connected". Minecraft writes its
  pid to the shared header once, when it opens the mapping, and keeps the mapping open across our
  restarts. Each Outer Wilds start zeroed that header, so the pid stayed 0, and "once per Minecraft
  pid" (0 = 0) never fired. Fixes:
  - the host's reset keeps Minecraft's pid;
  - the sizing is a flag, reset when Minecraft (re)connects or stops responding, with no eye-height
    condition.
- **Walking speed.** A new setting, `minecraftWalkSpeed` (m/s, default 6.0, the Hearthian's run).
  It is sent with the scale as `/attribute @s minecraft:movement_speed base set 0.139`
  (Minecraft's 0.1 walks 4.3 blocks/s; sprinting stays ×1.3). The number is always formatted
  with a dot, since the French locale would write "0,139". It was added to the installed
  `config.json` too.
- **TNT.** The save itself (region files decoded with a small NBT reader, outside the repo) shows:
  - The server ticks: the fire blocks had scheduled fire ticks when their chunk was saved.
  - TNT gamerule `tnt_explodes` is on and cheats are on (survival).
  - One TNT still has a redstone wall torch hanging on it. Vanilla Minecraft never powers the
    block a torch is attached to, so that TNT can't light. It needs a torch on the ground or
    another block next to it, a redstone block or lever, or flint and steel used on the TNT itself.
  - Fire next to TNT lights it only after a random delay (several seconds).
  - What was missing on our side: the explosion events (SkyCraft's event ring) were never read.
    Each one is now logged ("Minecraft explosion at ...") and flashes an orange point light on
    the planet (0.6 s). Minecraft still draws its own smoke particles.
- Rebuilt: host 0 warnings, 0 errors; guest compiles.

## 2026-10-02: thirteenth test, explosions
- **Report:** the lag is mostly fixed (log: Minecraft a steady 58–66 fps, ours 59–60). TNT is
  finicky to trigger, doesn't set off other TNT, and leaves random stone after the blast.
- **Log:** three "Minecraft explosion" lines, each a single blast (no chain).
- **Cause: SkyCraft's "Skyrim destruction".** It digs blasts into the host's terrain:
  - ground in the blast becomes real dirt and stone blocks (`SkyDigBlast`), which the explosion
    breaks;
  - the crater is then lined with stone, since in Skyrim the host hides the dug terrain.
  
  We can't carve Outer Wilds planets, so the lining stays as floating stone. The ground's stone
  resistance also soaks up the rays near the ground, so the blast dies before it reaches the next
  TNT.
- **Fix:** `destruction=false` in Minecraft's `run/config/skycraft.properties` (the same setting as the
  pause menu's "Skyrim destruction" button). Explosions are now vanilla: Outer Wilds' ground doesn't
  resist them and isn't dug. Holes already dug stay, lined with their stone.

## 2026-10-02: fourteenth test, constant slight shake
- **Report:** feels amazing, but everything shakes a little all the time. (Nether portals: looked
  at, then dropped at the user's request. Neither side handles dimensions, so don't light one.)
- **Log:**
  - Minecraft 60 fps, ours 60.
  - The worst Minecraft tick age is 61–67 ms, with 26–62 frames per 5 s "waiting on a late tick".
  - Guest: tick gap 66.7 ms worst.
- **Cause:** Minecraft runs its 20 Hz tick at the start of a frame and publishes it at the end, so a
  tick reaches us up to one Minecraft frame (about 17 ms) after its stamp. We interpolated
  previous → latest tick and showed the latest one exactly 50 ms after its stamp. On 10–20% of
  frames the next tick hadn't arrived yet: the view held for a frame, then jumped. That's the shake
  while moving.
- **Fix (`PlayerDriver`):**
  - Ticks go into a small timeline (the last 8 stamps, positions and eye heights).
  - It is shown 20 ms later than before (one tick + 20 ms behind the latest stamp), so a newer tick
    is nearly always in hand.
  - The timeline restarts on teleports, jumps over 8 blocks and gaps over 4 ticks.
  - Walking speed for the animation comes from the same pair of ticks.
  - Cost: 20 ms more movement latency. The look (mouse) is ours and isn't delayed.
  
  "frames waiting on a late tick" in the stats line should now drop to near 0.
- Rebuilt: 0 warnings, 0 errors (host only).

## 2026-10-02: last log read of the night
- **Shake fix confirmed.** "frames waiting on a late tick" went from 26–62 per 5 s to 0 in every
  stats line. Minecraft and ours both run at 60 fps.
- **Not ours:** a `NullReferenceException` in `QuantumMoon.OnDisable` while Outer Wilds quits (the
  game's own code).
- **Bug: on quitting Outer Wilds, Minecraft's player fell out of the world in a loop**
  (Minecraft log: "fell out of the world" every 2–3 s, respawns at y −1089).
  1. On quit, our `SharedLink.Dispose` set the host pid to 0. SkyCraft saw that as a new game
     instance and dropped all our collision. The link still looked alive (the heartbeat was under
     2 s old), so it didn't freeze its player, who fell through.
  2. When the player dies, SkyCraft respawns it at our position. Outside Minecraft mode, that
     position echoes Minecraft's own, so it pointed at the fallen spot below the world, and each
     respawn died again.
- **Fixes:**
  - `Dispose` now zeroes our heartbeat instead of the pid. Minecraft sees the link down at once,
    and SkyCraft's `freezeWhileUnlinked` holds the player where it stands.
  - The echo ignores positions below y −64, so a respawn goes to the last spot where the player
    stood.
- Rebuilt: 0 warnings, 0 errors (host only).

## 2026-10-02: published on GitHub, first release (0.1.0)
- **Repo:** github.com/Yaekai/OWCraft, MIT.
  - The SkyCraft changes ship as `guest/skycraft-owcraft.patch`. It applies cleanly to `bfcaf178`
    and compiles.
  - The mod id is now `Yaekai.OWCraft`. The installed folder was moved and the config kept.
- **Privacy:**
  - The repo history is one commit, signed with GitHub's no-reply address. GitHub's own starter
    commit, which carried the account's email, was replaced.
  - The DLL used to embed the builder's local PDB path. `PathMap` + `Deterministic` now record
    `/_/OWCraft/...` instead.
  - Repo, zip and jar were scanned for local paths and names: nothing found.
- **Guest defaults for players** (in the patch, so the normal launcher works without JVM
  arguments):
  - link name `Local\OWCraft_v1`;
  - `destruction` off;
  - Minecraft keeps running when Outer Wilds closes (`skycraft.quitWithSkyrim=false`);
  - SkyCraft's Discord status off. It would have said "Playing Skyrim" under chasmlol's app.
  - Mod name "SkyCraft (OWCraft build)", version `0.1.2+owcraft.1`.
- **Release files:**
  - `Yaekai.OWCraft-0.1.0.zip`: the OWML mod folder, license and notices;
  - `skycraft-0.1.2+owcraft.1.jar`: SkyCraft's MIT license added as `LICENSE_SkyCraft.txt`. It holds
    no Minecraft classes or assets.
- **Not yet tested:** this jar from the normal Minecraft launcher with Fabric. All play tests so far
  ran through `gradlew runClient`.

## 2026-10-02: elytra freezes, invisible arrows
- **Elytra froze in the air every few seconds.**
  - Cause: flying crosses a ~40 m tile every 2–5 s (seen in the OWML log). Each tile change was a
    full teleport: velocity zeroed, then the guest's hold kept the player still until ground was
    known (up to 6 s).
  - Fix: the host flags tile-change teleports (`HostReanchor`, bit 3 of the host flags).
    - Airborne: the guest moves the player by the offset with a relative teleport (`Relative.ALL`),
      which keeps velocity and flight, with no hold. It acks once the player is within 64 blocks
      of the target. If that doesn't happen within 1 s, it falls back to the old teleport.
    - On the ground, the old path stays.
  - Host rebuilt: 0 warnings, 0 errors. Guest compiles. **Not yet tested in game.**
  - Known gaps: velocity isn't rotated into the new tile's frame (small angle). Skipping the hold
    near terrain could clip briefly.
- **Arrows were invisible (only their particle trail showed).**
  - Cause: SkyCraft sends arrows, dropped items and thrown items through a separate light table
    (`WorldExporter`, 0x1C000). Its scene export skips them, and our host never reads that table.
  - Fix (guest): the scene export no longer skips them. They now come through with Minecraft's real
    models, like mobs. Tridents, dropped items, snowballs and ender pearls are covered too.
    **Not yet tested in game.**
  - Note: the scene holds 48 entities within 64 blocks, so a big pile of dropped items could crowd
    out mobs.
- Guest is now `0.1.2+owcraft.2`. The patch was regenerated and applies to `bfcaf178`.

## 2026-10-02: play test of owcraft.2, and three fixes
**Play test:** elytra flight and arrows work. Still wrong: slight shake, a spawned creeper vanished
after a few blocks of walking, and the player clips through most Outer Wilds objects.

**Shake.** The log had 20 `IndexOutOfRangeException`s in `PlayerDriver.Update`, each just before a
tile change.
- Cause: a mid-air tile shift is confirmed before Minecraft's next tick, and the tick buffer was
  empty (it's reset on teleport). That frame threw before the player was placed, so Unity moved them
  on its own: a jolt.
- Fix: with no ticks yet, use Minecraft's reported position as is.

**Creeper vanished.**
- Cause: a tile change moves the Minecraft player 512 blocks. Mobs stayed in the old area, lost their
  ground when the collision was cleared, and despawned at that distance.
- Fix:
  - The host now sends the rotation between the old and new tile (`SsShiftRot`, 9 floats at 0x40 in
    the host state; neighbouring tiles lean a few degrees apart).
  - The guest (`TileCarry`, server thread) moves every non-player entity within 48 blocks along with
    the player: same offset, turned by that rotation, and yaw turned too.
  - Each moved entity is lifted 0.25 blocks, because the tiles' 1/8-block grids don't line up. It is
    held still until the new tile's collision under it is known, at most 4 s.

**Clipping.**
- Cause: about 2000 Outer Wilds collider meshes are unreadable (no Read/Write), and those were only
  sampled from straight above. A tree or house became a thin sheet at its top, with nothing at body
  height.
- Fix: unreadable meshes are now also sampled from ±X and ±Z, on the same half-block ray grid.
  Neighbouring hits are stitched into triangles, skipping cells where the hits jump more than one
  block (an edge, not a surface). Rays that miss the collider's bounds are skipped cheaply.
- The stats line now reports `side samples N ms`, to watch the cost.

Host: 0 warnings, 0 errors. Guest `0.1.2+owcraft.3` builds; the patch was regenerated and applies to
`bfcaf178`. **None of the three fixes has been tested in game yet.**

## 2026-10-02: play test (new host, old guest), elytra freezes and mobs
**Play test:** collision is fixed (the side samples work). New problems: freezes during elytra
flight, and mobs that die on their own or don't move at all.

This test ran with the new host but the **old guest** (`owcraft.2`). The `owcraft.3` jar was never
copied into `.minecraft\mods`, so `TileCarry` wasn't there.

**Elytra freezes.**
- Log: two tile changes in the same second, then `Minecraft moved the player off this tile;
  bringing it back`. That's a full teleport with the player held: the freeze.
- Cause:
  - The first tick after a tile shift reports its *previous* position from before the shift, on the
    old tile.
  - The host started its smoothing from there, so for a frame the player looked off-tile.
- Fix (host):
  - When the previous position is over 8 blocks from the current one, smoothing starts from the
    current one.
  - If the smoothed position is off-tile but Minecraft's own position isn't, the host uses
    Minecraft's.

**Mobs dying.**
- Cause: Minecraft only has planet ground within about 40 blocks of the player, and every tile change
  clears it. Mobs outside it fall into the void.
- Fix (guest, `TileCarry`): living entities and dropped items that are in the air where no collision
  is known (at their feet or one region below) are held in place each server tick.

**Mobs not moving.**
- Cause: Minecraft's pathfinding only looks at blocks. To it the planet ground is air, so a mob finds
  no floor and its path goals fail.
- Fix (guest, new `PathfindingContextMixin`): during pathfinding, air under a planet surface (the
  same test that lets torches stand on terrain) counts as solid. These lookups bypass Minecraft's
  path-type cache, since the ground streams in later.

Host: 0 warnings, 0 errors. Guest `0.1.2+owcraft.4` builds and is installed in `.minecraft\mods`
(owcraft.2 removed). The patch was regenerated and applies to `bfcaf178`. **Not tested in game
yet.**

## 2026-10-02: owcraft.4 play test; Steve body, F5, footsteps
**Play test:** mobs spawn and move now, but don't come for the player. You hear footsteps on blocks
while gliding with the elytra. Asked for: Minecraft's player model instead of the Hearthian, and F5
third person.

**Mobs not coming at you.** The player was in Creative mode (the log shows `Set own game mode to
Creative Mode`). Hostile mobs ignore Creative players in vanilla. Nothing to change. Needs a retest in
Survival.

**Footsteps while gliding.**
- Cause: the host told Outer Wilds the player was grounded every frame (so the animator wouldn't play
  a fall). A grounded player that moves gets footstep sounds.
- Fix (host): grounded only while Minecraft says it's on the ground (or swimming). In the air it isn't.

**Steve body.**
- Minecraft already captures the player's own body in third person (`REN_AVATAR`, posed, relative to
  the feet; empty in first person). The host ignored it.
- Fix (host): `SceneRenderer` now has two layers, the scene and the body. The body is drawn at the
  feet the host shows every frame, on the tile's root, with the same materials as mobs.
- In Minecraft mode the Hearthian's skinned meshes are hidden. They come back on leaving.

**F5.**
- The host used to block F5 and switch Minecraft back to first person whenever it left it. Both are
  gone.
- In third person the Outer Wilds camera sits Minecraft's own camera distance behind the eye, or in
  front looking back. Minecraft already shortens that against its blocks. A sphere cast shortens it
  further against the planet's real geometry.
- Aim stays Minecraft's: what you hit is what Minecraft's crosshair hits from the eye.

Host only (guest unchanged at `owcraft.4`): 0 warnings, 0 errors, deployed. **Not tested in game
yet.**

## 2026-10-02: invincible in Creative and Spectator
**Ask:** the Outer Wilds body suffocates when you fly off the planet in Minecraft mode. That's fine in
Survival, but not in Creative or Spectator.
- Guest: a new McState flag, `MC_INVULNERABLE` (1 << 8), set from `player.getAbilities().invulnerable`.
  That's true in Creative and Spectator.
- Host: while in Minecraft mode with that flag, `PlayerResources._invincible` is on. This is the
  game's own debug invincibility. Its oxygen, health and instant-damage code all check that field
  (checked by reading their IL for the field token, not by decompiling). It turns off when the flag
  clears or Minecraft mode ends. If the game's cheat had already turned it on, it's left alone.
- Guest is now `0.1.2+owcraft.5`; the patch was regenerated and applies to `bfcaf178`. Host: 0 warnings,
  0 errors. **Not tested in game yet.**

## 2026-10-02: log checker, and fewer collision hitches
- New `tools/check-logs.py`. It reads the newest OWML log and Minecraft's `latest.log` and lists the
  known problems: tile resyncs (the elytra freeze), errors in OWCraft code, stalls, low fps, mid-air
  teleports, mixin failures. It exits with 1 if it finds any.
- On the `owcraft.4` play test it found 43 tile changes and 0 "off this tile" resyncs, so the elytra
  freeze is fixed. It also found collision spikes: up to 138 ms in one frame, and up to 428 ms of
  side samples (the raycasts from the sides that catch tree trunks and walls) in one 5 s window.
- The regions next to the player are harvested again every second, for sand and crumbling ground.
  Those refreshes now skip the side samples, since trunks and walls don't move. The first harvest
  of a region still does them. Host: 0 errors. **Not tested in game yet.**

## 2026-10-02: play test on Giant's Deep's islands
**Report:** everything broke and shook after flying to another planet; F5 and the footstep sounds are
fixed, and elytra flight feels mostly okay.
- The log: the player stood on `BrambleIsland_Body`, one of Giant's Deep's floating islands. It's its
  own rigidbody with no gravity of its own, so OWCraft took it for a 100 m planet and pointed
  Minecraft's "up" away from the island's centre instead of up from Giant's Deep. Flying off the
  island, the player crossed that fake sphere's tile edges over and over (one tile change after
  another, flipping straight back), and the world jumped each time.
- Fix: a body with no gravity of its own is now one flat tile through its centre. Its "up" is the
  body's own up when that is within 30 degrees of gravity (the same tile every loop), or else the
  gravity direction when you first press F6 there. Flying more than 256 blocks from the island
  brings you back, like any other tile edge.
- `tools/check-logs.py` now flags tile changes that flip straight back.
- Also seen: Outer Wilds stopped drawing for 44 s around 15:24:20 (Minecraft saw the link go down).
  Yaekai had alt-tabbed, so it's not a bug. Host: 0 errors. **Not tested in game yet.**

## 2026-10-02: second island test
**Report:** standing on the island works, but the camera is sideways and jumpy; flying out over Giant's
Deep, the player froze in the air and things started shaking again.
- Camera: every frame the player was turned upright to "away from the body's centre". On an island
  that's sideways. Flat tiles now use the tile's own up.
- Freeze: 29 "off this tile" resyncs, the island's 256-block edge acting as a wall, again and again.
  Now, 160 blocks from the island's centre, the player moves over to the nearest real planet (the one
  with gravity whose surface is closest, so Giant's Deep), like a normal tile change. The player is
  re-pinned to that body, and the tile-shift rotation is now worked out in world space so it works
  between two bodies.
- Known: once you're on Giant's Deep's tiles, the islands aren't its collision (they're separate
  bodies), so Minecraft doesn't see them until you F6 again while standing on one.
- Host: 0 errors. **Not tested in game yet.**

## 2026-10-02: third island test, and elytra over Giant's Deep
**Report:** went nuts towards the end: Minecraft's menu opened by itself, sinking, unresponsive. Asked for
smoother elytra flight (rubber-banding and lag).
- The island-to-Giant's-Deep handover worked (one tile change, then Giant's Deep's own tiles).
- Then, flying at elytra speed over 40 m tiles: a tile change about every second. Each one moves the
  Minecraft player 512+ blocks to another area, reloads chunks there and clears the collision. The
  server fell 2 s behind, the tile shifts arrived late ("teleporting instead"), the player fell with no
  ground loaded, died, and the death screen was the "menu" that opened. Minecraft then stalled for
  up to 31 s per frame.
- Fix: in the air, the player now stays on a tile until 160 blocks from its centre, or until
  "up" leans 20 degrees from the planet's, whichever is closer (on Timber Hearth about 90 blocks, on
  Giant's Deep 160). That's 4 to 6 times fewer tile changes in flight. Back on the ground the normal
  limit (60% of a tile) applies again, so it moves to the nearest tile when you land. Tile sizes are
  unchanged, so saved builds stay where they are.
- Host: 0 errors. **Not tested in game yet.**

## 2026-10-02: elytra test
**Report:** elytra flight feels smoother now. The log: 44 tile changes in one session, 0 resyncs, no
flips. One landing gave a full teleport (2 s after a tile change) with "standing on air"; left as is
for now. `tools/check-logs.py` no longer counts the game's own errors at quit (an OWCraft line right
after them) as ours: it now looks only at the error's own stack trace.
