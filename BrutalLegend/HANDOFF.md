# Brütal Legend → s&box Mount — Handoff

**Goal:** s&box game mount that reads Brütal Legend (Buddha engine) assets from a Steam install and
converts them on the fly. Working dir `E:/sboxaddons/brutal_mount`. Companion docs:
`brutal_legend_mount_handoff.md` (full file-format + API research),
`brutal_legend_sbox_roadmap.md` (task list).

## Build / test loop
- Project is a normal s&box project (`brutal_mount.sbproj`), **auto-compiled by the editor**.
  Mount code lives in **`Editor/`** (NOT `Code/`). Shaders in **`Assets/shaders/`**.
- Drive the running editor via `sbox-cli` (from the project folder):
  - `sbox-cli --call editor_status`  → `LastCompileSucceeded` / `LastCompileErrors`
  - `sbox-cli --call compile_status` → per-file C# diagnostics
  - `sbox-cli --call read_console '{"minimumLevel":"Warn"}'` → shader + runtime errors
  - `sbox-cli --call run_csharp '{"path":"<abs>.cs"}'` or inline `{"code":"..."}` → run C# in-engine
- After editing C# or `.shader`, the editor hotloads/recompiles; poll `editor_status` until
  `IsCompiling` is false.
- Steam AppID **225260**. Mount reads `<install>/Win/Packs/*.~h` and registers all files.

## What WORKS (verified)
- **Archive reader** `Editor/Budha/BudhaArchive.cs` — reads paired `.~h` + `.~p` (FAT + payload).
  Big-endian `dfpf` header, bit-packed 16-byte index (Version 5 layout), zlib entries.
  Loads all 21 packs (~71k files).
- **Mesh & Rig readers** `Editor/Mesh/MeshReader.cs` & `Editor/Mesh/RigReader.cs` — parses `hsem`
  geometry (submeshes, 4 vertex formats, 16-bit indices, triangle-strip expansion) and `.Rig`
  hierarchies (bone names, parent indices, inverted 4x4 bind pose matrices).
- **Model loader** `Editor/Resource/BudhaModelLoader.cs` — builds s&box `Model` with full
  skeletons (`builder.AddBone`) and hardware skinning (`SkinnedVertex` with 4-bone indices and
  normalized blend weights). Verified in-engine: `b03_bride` (163 bones), `a01_avatar` (212 bones).
- **Embedded LODs** — a base mesh's sibling `_lod1`..`_lod7` entries are parsed and added with
  `builder.AddMesh( mesh, lod )`, so one `.vmdl` carries the whole chain like a cooked model
  (`Model.GetIndexCountForLod(n)`). `RegisterAssets` skips `_lodN` entries when their base mesh
  exists; every LOD in the packs has a base (7711 archive meshes → 3739 LODs folded → 3972 models).
  Verified: `a00_foreman`/`b03_bride` LOD0–4, static `megabasescaffholding` LOD0–4.
  Switch distances use the engine default (`lod * 50`); tune with `ModelBuilder.WithLodDistance`. 
- **Rig / bind transforms — s&box convention (verified 2026-10-19, do not regress):**
  - `.Rig` stores the **inverse bind pose** (`model → bone`) as 4×4 row-vector/D3D matrices with
    translation in the **last row** (`m[12..14]`). Bind translation (`RigReader.cs`):
    `tx' = -(tx*r00 + ty*r01 + tz*r02)`, `ty' = -(tx*r10 + ty*r11 + tz*r12)`,
    `tz' = -(tx*r20 + ty*r21 + tz*r22)`.
  - Bind **rotation** is the stored 3×3 as-is (do **not** transpose):
    `MatrixToRotation(r00,r01,r02, r10,r11,r12, r20,r21,r22)`.
  - `ModelBuilder.AddBone(position, rotation, parent)` takes the **model-space (root-relative) bind
    transform** — the same value cooked models expose as `Bone.LocalTransform` / `Model.GetBoneTransform()`.
    Verified against `models/citizen/citizen.vmdl` (pelvis z=31.07", hand_L z=39.25", head z=56.54":
    all model-space, not parent-relative).
  - **Do not convert bones to parent-relative.** The mesh still renders (bind pose is baked from the
    same transforms), but `SkinnedModelRenderer.CreateBoneObjects` collapses every bone object to the
    model origin — the exact "bone objects are way off" symptom.
  - Verified bone objects: `a01_avatar` (212 bones) places each at `object.WorldPosition + bind`, e.g.
    Head `bind=(-0.687, 0, 72.52)` → world `(-87.06, 7.87, 72.52)` for the object at `(-86.38, 7.87, 0)`.
    `TryGetBoneTransform`, `GetBoneObject`, and `Model.GetBoneTransform` all agree.
- **Model reload:** `Model.Load()` returns the editor's cached compiled model. After changing loader
  code, recompile/reload the mount asset (editor model reload); hotload + `Model.Load` alone keeps the
  old skeleton. `Editor.MountAsset.UpdateInternals(true)` + `CacheAsync()` (internal) also refreshed it.
- **Animation cycler (test component)** `Code/AnimationCycler.cs` — auto-finds a
  `SkinnedModelRenderer`, sets `UseAnimGraph = false`, reads `Model.AnimationNames`, and cycles
  `Renderer.Sequence.Name` every `SecondsPerAnimation` (loops via `Sequence.Looping`). Verified in
  play mode on `models/citizen/citizen.vmdl`: 475 animations found, logged and advanced
  (`Eyes_Blink` → `Eyes_Blink_delta` → `Eyes_Closed` → `Eyes_Open`). No animgraph required.
- **Player controller (2026-10-20)** `Code/BrutalPlayerController.cs` — third-person player on the
  built-in `CharacterController`, driving the baked `a01_avatar` sequences directly (no animgraph).
  - Controls: WASD (`Input.AnalogMove`) camera-relative, Shift = run, Space = jump, mouse look
    (`Input.AnalogLook`). Shift/Walk/Jump actions are already in `ProjectSettings/Input.config`.
  - Clips: `relaxed_breathe` idle; `action_walk_forward` / `action_run_forward`; backpedal
    `action_walk_backward` / `action_run_backward`; strafe `action_strafewalk_left|right` /
    `action_straferun_left|right`; airborne `fall_breathe`; landing `fall_land`. Playback rate scales
    with actual horizontal speed; the character faces camera yaw for correct strafe/backpedal.
  - Follow camera: existing main `Camera` at `EyeHeight` + `CameraDistance` behind, pitched/yawed by
    the mouse, with a scene ray to pull in on geometry.
  - `InputOverride` (Vector3, X=strafe/Y=forward) and `RunOverride` drive it from code/AI and were
    used for in-engine tests.
  - `scenes/minimal.scene` now has `a01_avatar` at `(0,0,10)` with `SkinnedModelRenderer`
    (`UseAnimGraph=false`), `CharacterController` (72 high, 16 radius), and `BrutalPlayerController`;
    the test `AnimationCycler` was removed. Verified in play mode: idle → walk → run → idle, strafe
    left/right, backpedal, jump (53" apex) and landing, plus a third-person screenshot.
- **Material fixes (hair/eyes/metal):**
  - `nomaterial` submeshes are remapped to the hair-sheet material instead of drawing the magenta
    checker (they are missing hair cards; this also fills sideburn gaps).
  - Hair normal maps are decoded as **DXT5nm** based on the DDS fourCC (`g_bDxt5nm`), not a per-texel
    heuristic; hair cutout lowered to `0.01` so soft sideburn strands survive, roughness `0.8`.
  - `kBLEND_Additive` materials (eye cornea/reflections) use `brutal_additive.shader` (additive,
    no depth write) instead of an opaque white cap.
  - `brutal.shader` now applies normal maps and the material `DiffuseColor` as a tint.
  - Metals use `metalness = 1` with standard `ShadingModelStandard` PBR and a neutral chrome
    reflectance (0.58,0.60,0.64) when no diffuse texture exists. Their shine depends on the scene's
    reflection source; the minimal test scene has no usable reflection probe, so they read matte there.
- **Texture decoder** `Editor/Resource/BudhaTexture.cs` + `DdsDecoder.cs` — strips 40-byte Buddha
  texture header → DDS → RGBA → `Texture.Create`.
- **Shaders & Material Mapping**:
  - `Assets/shaders/brutal.shader`: Standard opaque PBR material shader using Source 2's
    `ShadingModelStandard::Shade( i, m )`. Full directional sunlight, cascading shadow maps,
    ambient probes, and reflections.
  - `Assets/shaders/brutal_hair.shader`: Double-sided strand rendering with DXT5nm normal map
    strand details (`avatarhairsheet_norm`), subtle depth variation, and smooth alpha cutout.
    Solid skullcap base hair (`hair`) has zero alpha clipping to prevent scalp holes, while
    outer hair cards (`avatar_hairsheet`) use a gentle `0.05` threshold to preserve fine strands.
    Reads each character's exact `DiffuseColor` and `Ramp1Texture` (e.g. Eddie's dark espresso,
    Lionwhyte's platinum blonde, Lita's dirty blonde, Ophelia's black, Crossbow Girl's blonde).
  - `Assets/shaders/brutal_translucent.shader`: Alpha-blended translucent shader with double-sided
    rendering (`RenderState( BlendEnable, true )`, `DepthWriteEnable, false`, `CullMode, NONE`)
    for veils, dresses, glass, tears, and effects.
  - `BudhaModelLoader`:
    - Unilaterally detects material blending from Buddha's `BlendMode`:
      - `kBLEND_Alpha`: Routed to `brutal_translucent.shader` (e.g. `b03_bride`'s veil & translucent skirt).
      - `kBLEND_BinaryAlpha`: Routed to `brutal.shader` with `alphaTest = 0.35f` (e.g. `b03_bride`'s apron lace).
      - `kBLEND_Default` / `kBLEND_Opaque`: Opaque rendering.
    - Differentiates metals (`chrome`, `metal`, `buckle`, `zipper`, `button`, `axemetal`, `lighter`
      -> `Metalness = 1.0`, smooth roughness `0.05-0.35`) from dielectrics (`skin`, `vest`, `jeans`,
      `wing_structure`, `face` -> `Metalness = 0.0`, realistic roughness `0.35-0.85`).
    - Falls back to `Layer1Texture` (e.g. `environments/textures/general/metal_metaldark_01`) or
      `DarkMetalTexture` when `DiffuseTexture` is empty (`@`) on metal/chrome.
    - Routes `RenderHair` materials to `brutal_hair.shader`, transparent materials to
      `brutal_translucent.shader`, and opaque materials to `brutal.shader`.

## Animation RE state (2026-10-19, verified by emulating the game's sampler)
**Product requirement: animations are embedded into the model's `.vmdl` as sequences, never registered as
standalone assets.** Target API: `ModelBuilder.AddAnimation( name, fps )` returns an `AnimationBuilder`;
call `AddFrame( List<Transform> )` per frame with one transform per bone (`Transform` is model-space,
matching the bone bind transforms). `Model.AnimationNames` then exposes the sequence to
`SkinnedModelRenderer.Sequence` without an animgraph.

### Container (`AnimResource` → `mina`+`dnap`), corrected
Offsets from the `dnap` tag:
| off | type | field |
|---|---|---|
| 0x04 | f32 | duration |
| 0x08 | f32 | fps (30) |
| 0x0C | u16 | A = first pose block offset (relative to chunk) |
| 0x0E | u16 | 0 |
| 0x10 | u16 | frame count |
| 0x12 | u16 | rotation track count |
| 0x14 | u16 | position track count |
| 0x16 | u16 | pose count N (N-1 sampled intervals; last pose is the endpoint) |
| 0x18 | u16 | B (block-region byte budget; exact semantics unused so far) |
| 0x1A | u16 | C = max frames per block (32 / 64) |
| 0x1C | u8[4] | codec version/quantization `02 01 01 01` |
| 0x20 | u32 | 0 |
| 0x24 | 16 B | constant `5c 7c f1 c5` ×4 (always present; used as alignment landmark) |
| 0x34 | u16[8] | group fields, e.g. walk `42,25,82,5,11,5,23,0`; gestures `45,18,86,0,11,1,15,0` |
| 0x44 | u32 | pose-table offset, relative to `chunk+0x48` |
| 0x48 | u16 | frame-table offset, relative to `chunk+0x48` |
| 0x4A | u16 | 0 |
| 0x4C..0x58 | u32[4] | base-quaternion array offsets, each relative to `chunk + fieldOffset` |
| 0x5C.. | u16[] | track/bone index lists (rotation list starts at `chunk+0x60`; sparse ascending indices) |
| chunk+0x48+u32[0x44] | N×8 | pose table: `{u32 blockOffset; u32 unused}`; parser **adds chunk base in place** to the first dword |
| chunk+0x48+u16[0x48] | N×4 | frame table: `{u16 firstFrame; u16 lastFrameIndex}`; N entries, N-1 intervals; first entry overlaps the last pose entry's second dword |

Block at `A`: `u16 lens[16]` (stream lengths), `u16 blockSize` at +0x20, `u16 dataOffset` at +0x22.
Streams start at `block+dataOffset` and are cumulative; `sum(lens) == blockSize - dataOffset`.
Typical `dataOffset` = 0x24. `lens` split the data into 16 streams used by the sampler; non-zero
streams for walk `s0=150, s1=30, s2=32, s4=3072, s5=97, s6=20, s7=20, s9=1024, s10=2311, s11=144, s12=176, s15=8`.

### Quantized quaternion (verified against engine `0x6871e0`)
6 bytes = 48-bit **big-endian** integer `u` (`b0<<40 | … | b5`).
```
shift = u & 3
c0 = (u >> 32) & 0x7fff
c1 = (u >> 17) & 0x7fff
c2 = (u >>  2) & 0x7fff
K  = 16384.0f        // runtime-init global at 0x10123E8 (0 in the file!)
S  = 4.3159689e-05f  // float at 0xF7C268
v  = { (c0-K)*S, (c1-K)*S, (c2-K)*S }
w  = sqrt(1 - |v|²)
```
Component order table at `0xE7EE08 + shift*16` (4 ints):
`shift0=[3,0,1,2] shift1=[0,3,1,2] shift2=[0,1,3,2] shift3=[0,1,2,3]`.
Verified outputs: `755d395872b2 → (.5896,-.39034,.58961,-.39034)`,
`2e5bfb08b96c → (.67972,-.19495,.67968,-.19495)`.

### Sampling model (verified by emulation)
- Object passed to the class methods is a 0x14-byte wrapper: `[0]=0xE83798`, `[4]=1`,
  `[8]=chunk ptr`, `[0xC]=chunk size`, `[0x10]=chunk ptr`. Class vtable lives at `0xE83798`:
  duration `0x414170`, rot count `0x414180`, pos count `0x414190`, samplers `0x4141F0`/`0x414220`,
  blend `0x414250`.
- The sampler's 4th argument is a **bitmask of track groups**; `0xFFFF` samples the full pose.
  With flag=1 only group 0 is written; setting bits adds the corresponding groups (walk: 25 dynamic
  tracks total).
- Per call: `0x419C10` converts `time*fps` to a frame, binary-searches the frame table (`0x419490`),
  computes block-local frame + fraction, then `0x419500` builds the 16 stream pointers from the block
  header. Base/static rotations come from the four chunk arrays via `0x427B20` (6-byte quats written to
  `dest + boneIndex*48 + 0x10`). Dynamic tracks are bulk-evaluated by `0x427BD0`: two control quats per
  track, slerped by `0x686F00` with a per-track factor. Positions use the analogous `0x4235E0`.
- **Keyframe masks, track→bone map, and control arrays — fully decoded and validated.**
  - The channel index lists (rotation/scale/position, static and dynamic) contain **transform-track
    indices**, not bone indices. The outer AnimResource header holds a `u8[R]` **track→bone map** at
    `blob + 0x0E` (`R = dnap u16 +0x12`). Applying `bone = boneMap[track]` is required; the map is not
    the identity (walk `R=153`, bone values up to 211). This was the final source of folded poses.
  - Mask stream start = `dataStart + blockFields[0] + blockFields[1] + blockFields[2]`
    (walk `0x688`, gestures `0x628`, cliff `0xBC`). There is **no 12-byte header**. Track `k`'s mask is
    `L` bits, MSB-first, at bit **`maskStart*8 + k*L`**, where `L` is the block's `lastFrameIndex`
    (walk 31, gestures 63, final 1-frame blocks 0). A set bit `j` is a control key at frame `j+1`.
  - `popcount(mask)` = number of control keys for that track in the contiguous array at arg5/ECX
    (`p1`; walk `0x721`, gestures `0x6BE`). Track starts are the running sum of previous popcounts × 6.
    Arg4/`p0` (`dataStart`) holds one **start value per track** in list order.
  - Keys = start value at time 0, then each control value at its mask-bit frame, then the next block's
    start value at time `L+1`; slerp (rotations) or lerp (half3 scale/position) piecewise. The final
    1-frame block (`L=0`) always has alpha 0 at frame 0, so its next-block pointer is unused.
  - **Values:** rotations = 6-byte Havok quats decoded to canonical `(x,y,z,w)` (the component table
    places the computed component on the omitted axis); scale/position = little-endian IEEE half3;
    static arrays use the same 6-byte stride. The engine's own quat multiply at `0x686A00` confirms
    `(x,y,z,w)`.
  - **Validation:** with the corrected x87 `_CIacos`/`_CIsin` stubs (the old stubs popped the wrong
    stack slot), a Python port matches the engine's sampled pose for **all** dynamic tracks of walk
    (frames 0/15/30/31/32) and gestures (frames 0/10/50/63/64/97/98), including positions and scales,
    with error 0.0 (float precision). The 64-frame block needs no mask rescaling; `L` is 63.
- **s&box integration (implemented 2026-10-20):** `Editor/Animation/BudhaAnimation.cs` parses these
  blocks and appends frames to `ModelBuilder.AddAnimation`. `AnimationBuilder.AddFrame` expects
  **parent-local** transforms, not model-space: compose the animation locals in Buddha space, convert
  model-space positions/rotations to s&box, then derive each frame's local transform with
  `parentFrame.ToLocal(boneFrame)`. Use the converted bind skeleton for the same derivation. Verified
  in-engine: `a01_avatar` loads 340 embedded sequences (4.4 s first load, 212 bones), and a
  `SkinnedModelRenderer` renders a coherent `action_walk_forward` pose at two sequence times. The
  model-space result returned by `TryGetBoneTransformAnimation` matches the composed pose. Animations
  are under `<character>/animations/` and are type **`AnimResource`**; the mesh may live under
  `<character>/rig/` or `<character>/model/`, so the loader searches the model dir and its parent for
  `/animations/`.
- **Stock Havok is math-only here. Do not expect `hkaSplineCompressedAnimation`'s stock block layout.**
  The container/packing is Double Fine's: 16 u16 stream sections, separate rotation/position track counts,
  8-byte pose entries, and a group-bitmask sampler. The stock per-track sampler (`0x419AF0` → `0x4238C0`)
  is **not** used by the vtable path we need; the full-pose path calls the custom bulk evaluator
  `0x427BD0` once (verified: `427B20` ×1, `427BD0` ×1 per pose; no `419AF0`/`4238C0` calls).
  The bulk loop's track-index pointer is stack arg 7 (walk `chunk+0x1B0`, gestures `chunk+0x1A8`,
  cliff `chunk+0x60`), read as a `u16` per iteration. Arg 4 (`p0`) is the first control-point array
  (walk: `s0`, 25×6-byte quats); arg 5/ECX (`p1`) is the second control-point array (walk: `s4+0x99`,
  e.g. five quats for the root track). Each loop iteration reads one quat from each array, slerps them
  with a per-track factor, and writes `dest + boneIndex*48 + 0x10`. Per-track factors were logged for
  walk frames 0..30 (root: 0, .2, .7, .667, .1, .6, .333 — i.e. piecewise knot spans; second track:
  0,0,.333,.5,.667,1,1).
- **Reusable math (verified against the engine):**
  - `PredatorCZ/HavokLib` `source/hka_spline_decompressor.cpp`: `Read48Quat` matches the engine's
    `0x6871E0` exactly; `FindKnotSpan` + `GetSinglePoint` give degree-1..3 De Boor evaluation;
    `SplineDynamicTrackQuat/Vector::GetValue` is the model.
  - `BadDogSkyrim/PyNifly` `io_scene_nifly/hkx/anim_fo4.py::_decompress_spline` is a pure-Python stock
    decoder (masks, knots as bytes, control points, per-frame evaluation) — use it as a reference port.
  - `sigmaco/havok-2013` and `Bewolf2/projectanarchy` have the headers/`.inl` (`readKnots`,
    `readPackedQuaternions`, `sampleRotation`) but **no `.cpp`**; the PC SDK was distributed without them.
- **Decode status (2026-10-20): solved and ported.** The C# decoder in
  `Editor/Animation/BudhaAnimation.cs` parses the container, static/dynamic rotation/scale/position
  channels, applies the outer track→bone map, builds keys, samples per frame, and appends local frames
  to the owning model. The emulator oracle matches every tested frame/channel of walk (32-frame block)
  and gestures (64-frame block), including positions and scales, at float precision. Verified
  in-engine: `a01_avatar` exposes 340 embedded sequences and renders a coherent walk pose with
  `SkinnedModelRenderer`.
- **Remaining polish:** sequence naming/looping policy, load time/memory (340 sequences ≈ 21.8k frames
  ≈ 222 MB of `Transform` data per character), and testing additional rigs with different track maps.

### Emulation harness (reproducible oracle)
PE32, image base `0x400000`, file `S:/Program Files/Steam/steamapps/common/BrutalLegend/BrutalLegend.exe`.
Map the memory image, place the chunk at a fixed base, and fix up the pose table's first dwords by adding
that base. Initialize the runtime globals `0x10123E8 = 16384.0f`, `0xE7EED0 = (0,0,0,1)`. Stub the
uninitialized CRT dispatchers: `0xC8C9EA` = floor, `0xC8C9F0` = acos, `0xC8C9D8` = sin.
**Important:** both are called with the argument already on the x87 stack (`st(0)`), like
`_CIacos`/`_CIsin`. A stub that starts with `fld dword [esp+4]` shifts the x87 stack and silently
corrupts results (this caused the earlier 2/25 walk outliers). Use `fsin; ret` for sine and the
`fld st(0); fmul st(0); fld1; fsubrp; fsqrt; fxch; fpatan; ret` sequence for acos, without the
leading load. With the corrected stubs the oracle matches the C# decoder exactly.
Call `0x4141F0` with `ECX=wrapper`, cdecl args `(dest, 0, time, flagMask)`;
read 48-byte records (rotation at `+0x10`, position at `0x00`, scale at `+0x20`). This produced full walk
poses and is the ground truth for the C# decoder. Anchors: container deserializer `0x40E2D0`, serializer
`0x40DE30`, dnap parser `0x414480`, factory `0x4131E0`, 4CC reader `0x45F430`, bulk rotation evaluator
`0x427BD0`, per-track spline `0x4238C0`, position `0x4235E0`, slerp `0x686F00`, quat unpack `0x6871E0`.

## World / map data (2026-10-20 feasibility)

**Answer: the game worlds are packed in the `.~h`/`.~p` archives, not compiled into the exe.**
The exe only contains the loaders. Every world is a tiled dataset under
`worlds/<world>/tile/xN/yM/`, with a text manifest per world.

### Manifest: `LevelData` is text
`worlds/dlc1_1/dlc1_1` (34,886 B) and `worlds/continent3/continent3` (917,601 B) begin with a
4-byte length then `1LevelData{AggregateTiles={...}}`. Each `<x,y>` entry looks like:
```
TileData{
  PlacedObjects=@worlds/dlc1_1/tile/x-4/y-4/base_objects;
  Heightfield=@worlds/dlc1_1/tile/x-4/y-4/height;
  StaticShape=@worlds/dlc1_1/tile/x-4/y-4/base_objects;
  PathTileData=@worlds/dlc1_1/tile/x-4/y-4/base_ptile;
  Index=<-4,-4>;
  VisualObjects=@worlds/dlc1_1/tile/x-4/y-4/base_visuals;
  Scale=<512,1024,512>;
  PhysicsMaterial=@worlds/dlc1_1/tile/x-4/y-4/blend;
  RndTileData=@worlds/dlc1_1/tile/x-4/y-4/blend;
  Center=<-1792,0,-1792>;
  TerrainShape=@worlds/dlc1_1/tile/x-4/y-4/base_tile;
}
```
- Tile footprint is **512 x 512**; height range field is 1024. Centers follow
  `center = tileIndex * 512 + 256` (tile `<-4,-4>` -> `<-1792,0,-1792>`).
- `LevelList` is also text and lists the chapters: `ResourcePath=Worlds/SK_1/SK_1.LevelData`,
  `SK_2`... plus DLC worlds. `worlds/continent3` is the large open world; `dlc1_1` is a 64-tile
  DLC map. `GameMapRegions` is text with region bounds/vertices.

### Per-tile data (archive types and sample sizes)
| Type | Count | Sample | Notes |
|---|---:|---|---|
| `Heightfield` | 1667 | 524288 B | 512x512 **uint16** height samples. Central tile x0/y0: values 0..4980; x-1/y-1: 0..6261. Decoded as R16_UNORM scaled by `Scale.Y=1024`; 64-tile `dlc1_1` bounds are ±80,630 X/Z and 0..9,180 Z. |
| `QuadTileData` | 2188 | 20-100 KB | Terrain patches, DXVB/DXIB-style (`BVXD`/`BIXD` tags, e.g. x0/y0 quad_0 header implies ~830 verts / 3438 faces). Custom vertex layout; adapt `MeshReader`. |
| `ObjectData` | 3279 | 676-3013 B | `base_visuals` (props) and `base_objects`; custom binary object tree with reversed 4CC tags `objd`, `prot`, `mesh`, `tree`. `base_visuals` records decoded: `u32 pathLen; path; float; Vector3 pos; quat; float scale`. `dlc1_1` has 992 visual placements; `continent3` has 137,839. |
| `CollisionShape` | 4946 | 336 B / 43824 B | Havok 6.1.0 tagfile (`Havok-6.1.0-r1`, `__classnames__`, `__types__`, `__data__`, `CollisionShape`). `base_tile` terrain collision is 43824 B. |
| `PathTileData` | 2052 | 147 B | `base_ptile`; references base_tile/height. |
| `AmbTileData` | 163 | 50-744 KB | Special tiles' ambient meshes; `AmbMeshDefinition` (42 text files) defines ambient mesh sets. |
| `RndTileData` | 1667 | ~8.3 KB | Shares `blend` path with `Texture blend` (131 KB) + `Texture occlusion`; terrain render layers. |
| `TerrainMaterial` | 108 | text | `1TerrainMaterial{...DiffuseTexture=@...}`. |
| `Weather`/`Climate`/`OceanData` | 146/47/5 | text | Atmosphere/water parameters. |

### Implemented (2026-10-20)
- **Merged terrain models:** `Editor/Resource/BudhaTerrainLoader.cs` parses `LevelData`, reads every
  tile's `Heightfield`, decimates adaptively (step 8 for `dlc1_1`, step 32 for `continent3`), welds
  shared tile-edge heights, and builds one mesh + `AddCollisionMesh` per map. `dlc1_1`: 64 tiles,
  270,400 verts, 524,288 tris, collision true. `continent3`: 1,605 tiles, 463,845 verts, 821,760 tris.
- **Model assets, not paths:** each map is registered as `ResourceType.Model` at
  `worlds/<map>/<map>_terrain` (asset path `mount://brutal_legend/worlds/<map>/<map>_terrain.vmdl`).
  Search the map name (e.g. `dlc1_1`) in the asset browser and drag the Model into
  `BrutalWorldMap.MapModel` (a real Model property; no path typing).
- **Texture:** the terrain material uses the first available `TerrainMaterial`'s `DiffuseTexture`
  (currently one texture per map, world-space UVs / 64; no per-tile blend/occlusion layers yet).
- **Map objects:** `base_visuals` records are parsed (`pathLen; path; float; position; quaternion;
  scale`) and embedded in the model under a typed `[GameData("brutal_world_objects")]` key. Runtime
  `BrutalWorldMap` loads each referenced `Mesh` model and spawns a `ModelRenderer` at the converted
  transform. `dlc1_1` spawns 992 props; `MaxObjects` defaults to 1,500 to protect huge maps.
- **Scene:** `scenes/minimal.scene` has a `BrutalMap` object with `BrutalWorldMap` assigned
  `worlds/dlc1_1/dlc1_1_terrain`; it adds a static `ModelCollider`, spawns the player above the map,
  and drops it onto the terrain.
- **Verified:** rendered rock-textured terrain, player grounded and idle on the merged mesh, 992
  spawned props (screenshot near `searuinstackarchd`), asset search finds the map Model.

### Remaining plan
1. **Ambient objects:** parse `AmbTileData` / `AmbMeshDefinition` (grass, rocks, small clutter) and
   instantiate them; `base_visuals` covers the main placed props only.
2. **Object collision:** spawned props have no colliders yet; add mesh/box colliders or parse the
   Havok `CollisionShape` for major structures.
3. **Material layers:** use `RndTileData` + per-tile `blend`/`occlusion` textures to blend the
   terrain layers instead of the current single `TerrainMaterial`.
4. **Streaming/LOD:** one merged mesh per map means the whole map is resident. The open world
   (`continent3`, 1605 tiles) needs tile streaming/LOD; the object cap is a temporary guard.

### Integration architecture
- The mount (editor-side) exposes each whole map as a merged `Model` resource
  (`worlds/<map>/<map>_terrain.vmdl`, `ModelBuilder.AddCollisionMesh`) and embeds visual placements as
  a typed `[GameData("brutal_world_objects")]` payload on that model. Runtime `BrutalWorldMap` uses
  the mapped `Model` asset and spawns props from the typed data; no runtime archive access is needed.
- The Model property is drag/drop friendly: search the map name in the asset browser and assign it to
  `BrutalWorldMap.MapModel`. The scene's `BrutalMap` already has `dlc1_1_terrain` assigned.
- For streaming later, split the per-map merged mesh into per-tile Models; the current whole-map
  approach is fine for 64-tile maps but not for `continent3` (1,605 tiles).
- Risks: `QuadTileData`/`AmbTileData` layouts, Havok tagfile collision, per-tile terrain material
  blending, and mesh/object count for the open world.

## Remaining work
1. **Animations — implemented; polish remains:**
   - `Editor/Animation/BudhaAnimation.cs`, `BrutalLegendMount.GetFilesUnder`, and the model embedding
     are in place. `AnimResource` is not registered as a standalone asset.
   - Validate additional characters with different `R`/track maps; applying the outer `u8[R]`
     track→bone map is mandatory.
   - Decide looping per sequence (currently `AnimationBuilder.Looping = false`) and consider a cap or
     lazy strategy if several 200+ MB character models are loaded. See the animation section above.
2. **Hair shader** — needs specifics; candidates: DXT5nm detection heuristic, ramp lookup by
   diffuse red, and missing anisotropic/backlit hair shading.
3. **Terrain/World Texture Decoder**:
   - Some world terrain blend/occlusion maps throw `Index was outside the bounds of the array`
     in `DdsDecoder` due to non-standard resolutions or uncompressed formats.
4. **Worlds/maps — merged terrain + placed props implemented; polish remains.**
   - `BudhaTerrainLoader` builds one textured, collidable Model per `LevelData`; `BrutalWorldMap`
     spawns `base_visuals` props from typed model data and has a drag/drop `Model` slot.
   - Remaining: `AmbTileData`/`AmbMeshDefinition` ambient clutter, object collision, per-tile
     `RndTileData`/blend texture layers, and streaming/LOD for the open world.
   - See "World / map data" above for implementation details and limits.

## Copy-paste prompt for the next agent
```
Work in E:/sboxaddons/brutal_mount (s&box project, Brütal Legend game mount). FIRST read HANDOFF.md
and brutal_legend_mount_handoff.md. Animation import is implemented and verified; do not redo the
archive/model/texture/rig work or the animation decoder.

Current state:
- Editor/Animation/BudhaAnimation.cs decodes AnimResource dnap chunks (static/dynamic rot/scale/pos,
  L-bit masks, 48-bit quats, half3 vectors), maps transform tracks to rig bones via the outer u8[R]
  map at blob+0x0E, and embeds local frames into the model via ModelBuilder.AddAnimation/AddFrame.
- BrutalLegendMount.GetFilesUnder(prefix,type) and BudhaModelLoader.AddAnimations associate
  <character>/animations/AnimResource entries with the mesh under <character>/rig or /model.
- Verified in-engine on characters/bipeds/a01_avatar/rig/a01_avatar: 340 sequences, 212 bones,
  coherent rendered walk pose. The Unicorn oracle matches all tested walk/gesture frames.
- Code/BrutalPlayerController.cs is a third-person CharacterController player using those sequences:
  WASD + camera-relative movement, Shift run, Space jump, mouse look, idle/walk/run/strafe/backpedal/
  fall/land clips, and a follow camera. scenes/minimal.scene has the configured a01_avatar player.
- Editor/Resource/BudhaTerrainLoader.cs builds one textured, collidable merged Model per LevelData map
  at worlds/<map>/<map>_terrain.vmdl. BrutalWorldMap has a draggable Model property, spawns
  base_visuals props (992 on dlc1_1; MaxObjects cap), and scenes/minimal.scene has BrutalMap set to
  dlc1_1_terrain.

Remaining work (optional polish):
1. Verify other characters/rigs (the track->bone map is not identity; ensure MaxBoneIndex filtering
   and path association work for non-biped rigs).
2. Decide per-sequence looping (currently AnimationBuilder.Looping=false); walk/idle should loop,
   one-shots should not. There is no looping metadata in AnimResource.
3. Load time/memory: a01_avatar is ~21.8k frames / ~222 MB of Transform data and ~4.4 s first load.
   Add a cap or lazy strategy if loading multiple characters at once is too heavy.
4. Optional: decode the P float tracks (facial morphs) and map them to model morphs.
5. Optional player polish: attacks/crouch/coyote time, footstep sounds, root-motion extraction, and
   camera boom smoothing/collision tags.
6. Maps polish: parse AmbTileData/AmbMeshDefinition ambient props, add object collision, use
   RndTileData/blend/occlusion terrain layers, and split/stream tiles for continent3.
Build/test loop, emulator oracle, and exact offsets are in HANDOFF.md. Do not redo the decoder.
```
