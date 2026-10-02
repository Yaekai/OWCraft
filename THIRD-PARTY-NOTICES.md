# Third-party notices

## SkyCraft (chasmlol/SkyCraft), MIT

OWCraft speaks SkyCraft's shared-memory protocol (v11), so SkyCraft's Fabric mod is the Minecraft
side. `guest/skycraft-owcraft.patch` is a diff against SkyCraft's Fabric mod: a few small changes to
frame pacing and the overlay copy. Parts of this mod are ported from SkyCraft's SKSE plugin (C++ to
C#):

- `host/Link/Proto.cs`: the byte layout of `protocol/skycraft_protocol.h`.
- `host/World/CollisionStreamer.cs`: the region schedule, triangle voxelizer (plane-guided
  triangle/box test), steep-surface coarsening and block packing from `skse/src/Collision.cpp`.
- `host/Player/InputForwarder.cs` and `PlayerDriver.cs`: the HID key codes and Minecraft's mouse
  sensitivity curve, as used in `skse/src/Input.cpp`.

Reference commit: bfcaf178524b92c2cdeb88e4ce0f13ef9ded6f32.

    MIT License
    
    Copyright (c) 2026 chasmlol
    
    Permission is hereby granted, free of charge, to any person obtaining a copy
    of this software and associated documentation files (the "Software"), to deal
    in the Software without restriction, including without limitation the rights
    to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
    copies of the Software, and to permit persons to whom the Software is
    furnished to do so, subject to the following conditions:
    
    The above copyright notice and this permission notice shall be included in all
    copies or substantial portions of the Software.
    
    THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
    IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
    FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
    AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
    LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
    OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
    SOFTWARE.
