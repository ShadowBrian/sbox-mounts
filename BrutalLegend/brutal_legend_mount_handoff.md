# Brütal Legend → s&box Mount — Research Handoff

> **Purpose.** Complete reference + implementation skeleton for building a Brütal Legend game
> mount for s&box. Compiled from the s&box mounting source and real reverse-engineering (the
> decompiled *Double Fine Tool*, `DoubleFine-Explorer`, and the Noesis plugin `fmt_brutal_mesh`).
> Hand this to another model/dev so **no research is repeated**. The companion
> `brutal_legend_sbox_roadmap.md` is the task checklist; **this** file is the facts + code.
>
> Tags: **[CONFIRMED]** = read from real source. **[UNVERIFIED]** = needs checking against a real
> install. Do not trust [UNVERIFIED] without testing.

---

## 0. TL;DR / Status

**Goal:** a `BaseGameMount` that detects a Brütal Legend Steam install and registers its assets
(models / textures / materials / sounds) as s&box resources, converting on-the-fly.

**Solid (all [CONFIRMED] from source):**
- Steam **AppID = 225260**. Engine = Double Fine **Buddha** engine.
- Assets are in paired archives **`NAME.~h`** (file allocation table) + **`NAME.~p`** (payload),
  in `<install>/Win/Packs/`. Format fully documented in Part B — big-endian, bit-packed index,
  **zlib** compression.
- Textures = **DDS** (DXT1/DXT5/A8R8G8B8). Sounds = **encrypted FSB4/5** (key + algo known).
  Models = **`.Mesh.header` + `.Mesh`** + **`.Rig.header` + `.Rig`** (layouts known).
- The s&box mount API + working sample mount (Quake) fully known — Part A.

> ⚠️ **Roadmap correction:** `brutal_legend_sbox_roadmap.md` lists AppID `22560`. The correct
> Steam AppID is **`225260`** (SteamDB). Use 225260. Also, the roadmap mentions "CRC hashes" —
> the FAT actually stores **string-table offsets**, not CRC lookups, so no CRC resolution needed.

**Still open (Part D):** animation track layout (`.anim`) is unparsed; exact `Material` payload
format; the `Texture.Create` compressed-upload question.

---

# PART A — The s&box mount system

Source: `https://github.com/Facepunch/sbox-public/tree/master/engine/Mounting`
(local engine also ships compiled `Sandbox.Mounting.dll` at `<engine>/bin/managed/`).

## A.1 Source layout (sample mounts — learn from these)

```
engine/Mounting/
  Sandbox.Mounting/              <- base API (the contract you code against)
    MountedGame/BaseGameMount.cs, InitializeContext.cs, MountContext.cs
    MountedAsset/ResourceLoader.cs, ResourceType.cs, ResourceFlags.cs, ResourceFolder.cs
    MountHost/MountHost.cs, Configuration.cs, ISteamIntegration.cs, IMountEvents.cs
  Sandbox.Mounting.Quake/        <- SIMPLEST real mount. USE AS TEMPLATE.
    QuakeMount.cs                <- BaseGameMount subclass (Initialize + Mount + Register)
    Pak.cs                       <- container reader (index+payload) ≈ maps 1:1 to .~h/.~p
    Resource/QuakeTexture.cs, QuakeModel.cs, QuakeSound.cs, QuakeMap*.cs, QuakeSkybox.cs
  Sandbox.Mounting.GoldSrc/  Sandbox.Mounting.NS2/  Sandbox.Mounting.SE3/  Sandbox.Mounting.Source1/
```
Engine discovers each mount as a DLL at **`<engine>/game/mount/<ident>/<ident>.dll`** (running
install shows `mount/quake/quake.dll` etc., each with optional `Assets/shaders/`).

## A.2 API contract (exact signatures)

**`BaseGameMount`** (subclass this):
```csharp
public abstract string Ident { get; }            // lowercase id, e.g. "brutal_legend"
public abstract string Title { get; }            // display name, e.g. "Brütal Legend"
public virtual long? SteamAppId => null;         // set to 225260
public bool IsInstalled { get; protected set; }  // set true in Initialize if found
public bool IsMounted { get; protected set; }    // set true at end of Mount
// auto Steam art from SteamAppId: CapsuleUrl, PortraitUrl, HeroUrl, LogoUrl
protected abstract void Initialize( InitializeContext context );
protected abstract Task Mount( MountContext context );
protected virtual void Shutdown() {}             // close open file handles here
```

**`InitializeContext`**: `bool IsAppInstalled(long appId); string GetAppDirectory(long appId);`
**`MountContext`**: `void Add(ResourceType type, string path, ResourceLoader entry); void AddError(string);`
`Add` wraps your path as `mount://<ident>/<path>` and forces the s&box extension
(`.vmdl/.vtex/.vmat/.vsnd/.scene/.prefab`) automatically — pass the native path.

**`ResourceLoader<THost>`** (one per file; `THost` = your mount):
```csharp
public abstract class ResourceLoader<T> : ResourceLoader where T : BaseGameMount
{
    public T Host => _mount as T;   // typed access to your mount's file-access helpers
}
protected override object Load();                  // return the built s&box object
protected override async Task<object> LoadAsync(); // or async
// optional: public ResourceFlags Flags; public HashSet<string> Tags;
```
Return type must match the registered `ResourceType`:

| ResourceType | Return from `Load()` |
|---|---|
| `Texture` | `Sandbox.Texture` (A.4) |
| `Model`   | `Sandbox.Model` |
| `Material`| `Sandbox.Material` |
| `Sound`   | `Sandbox.Sound` |
| `Scene` / `PrefabFile` | scene / prefab resource |

**`ResourceType`**: `Model, Texture, Material, Sound, Scene, PrefabFile, GameResource`.
**`ResourceFlags`**: `DeveloperOnly`, `NeverRagdoll`, `Effect`.

## A.3 Build & registration
Mounts are **net10.0** classlibs (`LangVersion 14`, `AllowUnsafeBlocks`) referencing
`Sandbox.Engine`, `Sandbox.Filesystem`, `Sandbox.System`, `Sandbox.Mounting`, with a post-build
copy into `game/mount/<ident>/<ident>.dll`. Template csproj:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <LangVersion>14</LangVersion>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\Sandbox.Engine\Sandbox.Engine.csproj" />
    <ProjectReference Include="..\..\Sandbox.Filesystem\Sandbox.Filesystem.csproj" />
    <ProjectReference Include="..\..\Sandbox.System\Sandbox.System.csproj" />
    <ProjectReference Include="..\Sandbox.Mounting\Sandbox.Mounting.csproj" />
  </ItemGroup>
  <Target Name="CopyOutputDll" AfterTargets="Build">
    <Copy SourceFiles="$(OutputPath)\$(AssemblyName).dll"
          DestinationFiles="..\..\..\game\mount\brutal_legend\brutal_legend.dll" />
  </Target>
</Project>
```
**[UNVERIFIED: local-dev discovery path]** — confirm whether the editor loads mounts from
`<engine>/mount/` or an "extensions" folder (check `MountHost.cs`). Building inside `sbox-public`
uses the project refs above; building standalone should reference the compiled
`Sandbox.Mounting.dll` + siblings instead.

## A.4 Loader example (from QuakeTexture.cs) + texture building
```csharp
class QuakeTexture( string pakDir, string fileName ) : ResourceLoader<QuakeMount>
{
    protected override object Load()
    {
        using var br = new BinaryReader( Host.GetFileStream( PakDir, FileName ) );
        var width = br.ReadInt32(); var height = br.ReadInt32();
        var data = br.ReadBytes( width * height );
        var imageData = new byte[ width * height * 4 ];  // expand to RGBA
        // ... fill imageData ...
        return Texture.Create( width, height ).WithData( imageData ).WithMips().Finish();
    }
}
```
`Texture.Create(w,h).WithData(byte[] rgba).WithMips().Finish()` builds a texture from **RGBA8**.
**[UNVERIFIED for compressed DDS]** — Brütal Legend textures are DXT1/DXT5/A8R8G8B8. Either
(a) decompress BC1/BC3 → RGBA8 on load and use `.WithData`, or (b) find a `Texture.Create`
overload taking a compressed format + raw blocks. **Check the `Sandbox.Texture` builder API before
writing a BC decompressor.** (A.4 / Part D #2.)

---

# PART B — Brütal Legend file format (Buddha engine)

Sources of truth: `doublefinetool` (decompiled *Double Fine Tool*, `.~h`/`.~p` reader/writer),
`DoubleFine-Explorer` (Bennyboy, Delphi), `fmt_brutal_mesh` (Noesis, `LordDude/fmt_brutal_mesh`),
`aluigi` QuickBMS `brutal_legend.bms`. See Sources.

## B.0 Install layout **[CONFIRMED]**
```
<Steam>/steamapps/common/BrutalLegend/         (or "Brutal Legend/")
├── BrutalLegend.exe, DFCONFIG
├── Data/                     # shared + localized string config
├── Win/
│   ├── buddha.dll, fmodex.dll, fmod_event.dll, steam_api.dll, binkw32.dll
│   ├── Packs/                # *** PRIMARY ASSETS: ~30-50 paired packs ***
│   │   ├── 00Startup.~h/.~p      # engine core, fonts, startup UI
│   │   ├── Man_Script.~h/.~p     # Lua game logic / missions
│   │   ├── Man_Trivial.~h/.~p    # outfits, props, materials
│   │   ├── RgB_Faction.~h/.~p    # faction models/textures/anims (etc.)
│   │   └── ...
│   └── Movies/ (or Videos/)  # .bik Bink cutscenes
└── OGL/                      # GL shader bytecode (Mac/Linux)
# Saves: %APPDATA%\Doublefine\BrutalLegend\ (screen.dat, prefs.dat, *.sav)
```
>95% of assets are inside the `.~h`/`.~p` packs. Loose: binaries, `DFCONFIG`, `.bik`, some `.fsb`.

## B.1 Container pair `NAME.~h` + `NAME.~p` **[CONFIRMED]**
- `.~h` = header + file-type table + bit-packed file index + filename pool + footer (the FAT).
- `.~p` = one concatenated payload blob; each index entry stores byte `Offset` + `Size`.
- Read a file: look up in `.~h` index → seek `Offset` in `.~p` → read `Size` bytes → inflate if
  compressed → optional 4-byte `HasSizeHeader` strip → content.

## B.2 `.~h` is BIG-ENDIAN **[CONFIRMED]**; asset payloads are LITTLE-ENDIAN **[CONFIRMED]**
The container/index is big-endian (Double Fine quirk). The extracted asset bytes
(mesh/texture/rig) are little-endian x86. Don't mix these up.

## B.3 `.~h` master header — 88 (0x58) bytes, big-endian **[CONFIRMED]**
| Offset | Type | Field | Notes |
|---|---|---|---|
| `0x00` | char[4] | `Magic` | ASCII **`"dfpf"`** (`64 66 70 66`) |
| `0x04` | u8[4] | `Version` | BL = **`02 00 00 00`**; Stacking `05`; Iron Brigade `06`. `Version[0]` picks the index layout (B.5); `Version[1]` picks `.~p` alignment (B.6) |
| `0x08` | u64 | `FileTypeOffset` | → file-type table |
| `0x10` | u64 | `FileNameOffset` | → filename string pool |
| `0x18` | i32 | `FileTypeCount` | # file types |
| `0x1C` | i32 | `FileNameLength` | filename-pool byte length |
| `0x20` | i32 | `FileCount` | # files |
| `0x24` | u8[4] | `Delim1` | sync marker `23 A1 CE AB` |
| `0x28` | u64 | `UnknownOffset` | reserved (often 0) |
| `0x30` | u64 | `DataFooterOffset` | `.~p` offset where data ends / padding begins |
| `0x38` | u64 | `FileIndexOffset` | → 16-byte file-record table |
| `0x40` | u64 | `FooterOffset1` | end of filename pool |
| `0x48` | u64 | `FooterOffset2` | reserved |
| `0x50` | i32 | `UnknownCount` | reserved |
| `0x54` | u8[4] | `Delim2` | sync marker `23 A1 CE AB` |

## B.4 File-type table @ `FileTypeOffset` **[CONFIRMED]**
`FileTypeCount` entries:
```
i32  nameLength      // includes NUL
char name[nameLength]// NUL-terminated ASCII, e.g. "Texture", "MeshSet", "Material"
i32  unknown1; i32 unknown2; i32 unknown3;
```
Resolve `Extension` / `HasSizeHeader` by `Name` (see B.6). If a filename has no extension, the
type name is used as the logical extension.

## B.5 File index entry — 16 bytes, bit-packed **[CONFIRMED]**
Big-endian bit order (MSB first). **Brütal Legend = `Version[0] == 2` → the "Costume Quest"
layout.** (Stacking `Version 5` differs — not needed for BL.)

| Bits | Len | Field | Notes |
|---|---|---|---|
| `0..22` | 23 | `ContentSize` | uncompressed size (max ~8.38 MB) |
| `23..40` | 18 | `UnknownFlag1` | runtime flags |
| `41..62` | 22 | `Size` (CompressedSize) | packed size in `.~p` (max ~4.19 MB) |
| `63..92` | 30 | `Offset` (PayloadOffset) | byte offset into `.~p` |
| `93..95` | 3 | `UnknownFlag2` | flags/alignment |
| `96..116` | 21 | `FileNameOffset` | relative offset into filename pool |
| `117..123` | 7 | `FileTypeIndex` | index into file-type table (0..127) |
| `124..127` | 4 | `CompressFlag` | `0x2`=stored, `0x4`=zlib (BL) |

Sum = 23+18+22+30+3+21+7+4 = **128 bits = 16 bytes**. ✓
Semantics: `ContentSize` = uncompressed; `Size` = on-disk packed; `Offset` = into `.~p`;
`FileNameOffset` = into name pool (relative to header `FileNameOffset`); `FileTypeIndex` = into
file-type table (**for `Version[0]==5` only, `>>= 1` first — NOT for BL**);
`IsCompressed = (CompressFlag & 0x4) != 0` for BL.

**Bit extractor (MSB-first), matches the tool's semantics:**
```csharp
static long BitExtract( byte[] d, int bitOffset, int bitLen )
{
    long v = 0;
    for ( int i = 0; i < bitLen; i++ )
    {
        int bit = bitOffset + i;
        int b = d[bit / 8];
        int m = 7 - (bit % 8);            // MSB-first
        v = (v << 1) | (uint)((b >> m) & 1);
    }
    return v;
}
// Usage per 16-byte record `data`:
// ContentSize    = (int)BitExtract(data, 0,   23);
// UnknownFlag1   = (int)BitExtract(data, 23,  18);
// Size           = (int)BitExtract(data, 41,  22);
// Offset         =       BitExtract(data, 63,  30);
// UnknownFlag2   = (int)BitExtract(data, 93,  3);
// FileNameOffset = (int)BitExtract(data, 96,  21);
// FileTypeIndex  = (int)BitExtract(data, 117, 7);
// CompressFlag   = (int)BitExtract(data, 124, 4);
```

## B.6 Filename pool + `.~p` payload + compression **[CONFIRMED]**
- **Name pool** @ `FileNameOffset`: NUL-terminated ASCII; entry `i` at `FileNameOffset +
  Files[i].FileNameOffset`; padding/terminator = `0xCC`; pool bounded by `FooterOffset1`.
- **Read `.~p` entry**: seek `Offset`, read `Size` bytes. `CompressFlag & 0x4` ⇒ **inflate
  zlib/deflate**; else raw. Result length = `ContentSize`. zlib header `78 DA`/`78 9C`; if that
  fails, retry raw deflate (`windowBits = -15`). (Console `CompressFlag 0x0C` = Xbox XMemCompress
  — ignore for PC.)
- **`HasSizeHeader` types** (Texture, MeshSet, Material, Cutscene, StringTable, + gameplay tables):
  the decompressed blob begins with a **4-byte length prefix**; skip 4 bytes then take
  `ContentSize-4` real bytes. (If `ContentSize < decompressed length`, the trailing excess is a
  per-file `.header` sidecar — only needed when repacking.)
- **`.~p` alignment/padding**: for PC (`Version[1]==1`) entries are padded to **4 bytes**; console
  to 2048. From `DataFooterOffset` to the 64 KiB boundary the file is padded with `0x7A` (`'z'`).
  (Reading is unaffected — you use `Offset`/`Size` — but note when repacking.)

**Read one file (port this):**
```csharp
ps.Seek(entry.Offset, SeekOrigin.Begin);
var packed = new byte[entry.Size]; ps.ReadExactly(packed);
byte[] blob = (entry.CompressFlag & 0x4) != 0 ? ZlibInflate(packed) : packed;
int off = type.HasSizeHeader ? 4 : 0;
var content = blob[off .. entry.ContentSize];   // real bytes (e.g. a DDS / .Mesh)
```

## B.7 Mesh format — `.Mesh.header` (metadata) + `.Mesh` (geometry) **[CONFIRMED]**
Little-endian. Source: `fmt_brutal_mesh.py`. Model = one `.Mesh.header` + paired `.Mesh`, plus a
`.Rig.header` + `.Rig` (B.8) for skeletons.

**`.Mesh.header`:**
- 84-byte (0x54) root header: magic **`"hsem"`** (`6D 65 73 68`, = "mesh" LE), padding, bounding
  sphere (X,Y,Z,Radius @0x08–0x17), bbox min/max (@0x1C–0x33), two 3-float scale/transform rows
  (@0x34–0x4B), two UV mip constants (@0x4C,0x50). (Full table in `fmt_brutal_mesh.py`.)
- **Material table** @ `0x54`: `u32 MatCount`; then `MatCount × { u32 len; char name[len] }`
  (material names like `Textures/Characters/Eddie/Eddie_Body`).
- **Submesh table (SBST)**: `u32 SubMeshCount`; then per submesh:
  | +off | type | field |
  |---|---|---|
  | `+0x00` | u32 | sig **`"tsbs"`** ("sbst" LE) |
  | `+0x04` | u32 | `MatID` (index into material table) |
  | `+0x08` | u32 | pad |
  | `+0x0C` | u32 | sig **`"BVXD"`** ("DXVB" = DX vertex buffer) |
  | `+0x10` | u32 | `VertCount` |
  | `+0x14` | u16 | `VertBuffType` (layout enum, below) |
  | `+0x16` | u32 | `MorphFrameCount` |
  - **Morph block** if `MorphFrameCount>0`: per frame `{ u32 nVerts; nVerts×{ u32 vertID;
    i16 dPos[3]; i16 dNorm[3]; i16 dTangent[4] } }`, then `u16 frameOrder[]`, `u16 finalMorphCount`,
    `u32 nameLen + char name[]`. (Deltas normalized `(x/32767)*2`.)
  - **Index buffer header**: (if `VertBuffType` 768/256 skip 3 bytes align) → u32 sig **`"BIXD"`**
    ("DXIB"), u32 pad, u32 `FaceCount`, u32 `FaceType` (`2`=triangle-list, else triangle-strip),
    `u8 BoneIDCount`, `u8 BoneIDS[BoneIDCount]` (submesh→skeleton bone palette), skip 56 bytes
    (bbox/LOD). If `VertBuffType==1280`, read u16 pad and treat as 1024.

**`.Mesh` payload** — for each submesh in order: `VertCount` interleaved vertices (stride =
layout) then `FaceCount × u16` triangle indices (16-bit).

**Vertex layouts (`VertBuffType`) — all little-endian:**
- **`1024` (0x400) Skinned half-float, stride 44** (default characters):  `BLENDINDICES u8[4]@0x00` · `BLENDWEIGHT u8[4]@0x04` · `POSITION f16[4]@0x08` · `TEXCOORD0 f16[2]@0x10` · `TEXCOORD1 f16[2]@0x14` · `NORMAL f16[4]@0x18` · `TANGENT f16[4]@0x20` · `COLOR0 u8[4] BGRA@0x28` · `COLOR1 u8[4] BGRA@0x2C`
- **`768` (0x300) Skinned full-float, stride 80**:
  `POSITION f32[3]@0x00` · `NORMAL f32[3]@0x0C` · `TANGENT f32[3]@0x18` · `TEXCOORD0 f32[2]@0x24` · `TEXCOORD1 f32[2]@0x2C` · `COLOR0 u8[4]@0x34` · `COLOR1 u8[4]@0x38` · `BLENDINDICES u8[4]@0x3C` · `BLENDWEIGHT f32[4]@0x40`
- **`512` (0x200) Static half-float, stride 40** (props/environment):
  `POSITION f16[4]@0x00` · `NORMAL f16[4]@0x08` · `TANGENT f16[4]@0x10` · `TEXCOORD0 f16[2]@0x18` · `TEXCOORD1 f16[2]@0x1C` · `COLOR0 u8[4]@0x20` · `COLOR1 u8[4]@0x24`
- **`256` (0x100) Static full-float, stride 60**:
  `POSITION f32[3]@0x00` · `NORMAL f32[3]@0x0C` · `TANGENT f32[3]@0x18` · `TEXCOORD0 f32[2]@0x24` · `TEXCOORD1 f32[2]@0x2C` · `COLOR0 u8[4]@0x34` · `COLOR1 u8[4]@0x38`

`BLENDINDICES` index the submesh's `BoneIDS[]` palette (map to skeleton bones via `.Rig`).
`COLOR0/1` are BGRA. Positions/normals may be half-float (decode f16 → f32).

**LODs:** detail levels are separate `Mesh` entries named `<base>_lod1` … `<base>_lod7`; LOD0 is the
base entry. No per-LOD distance data was found in the mesh header. The mount folds them into one
s&box model with `ModelBuilder.AddMesh( mesh, lod )` (verified LOD0–4 on characters and statics) and
does not register the `_lodN` entries as standalone models when their base exists.

## B.8 Rig format — `.Rig.header` + `.Rig` **[CONFIRMED]**
`RigStuff()` in `fmt_brutal_mesh.py`.
- **`.Rig.header`**: `u32 pad`; `f16[4]` scale/bounds; `u32 BoneNameSize`; `u32 BoneCount`;
  `u32 ParentIndicesSize` (= BoneCount×2); `u32 FlexCount`; unknowns; then offsets
  (`OffsetParentIndices` @0x50, `OffsetBoneMatrices` @0x60); sig `"gir."`/`"rig "` @0x6C.
- **`.Rig`**: (1) `BoneCount` NUL-terminated bone names + `FlexCount` flex names; (2) @
  `OffsetParentIndices`: `i16[BoneCount]` parents (`-1`/`0xFFFF` = root); (3) @
  `OffsetBoneMatrices`: `BoneCount` 4×4 `f32` matrices (64 B each) = **inverse bind pose**
  (`model → bone`) in **row-vector/D3D layout**, translation in the **last row** (`m[12..14]`).
  Bind rest transform (verified in `RigReader.cs` / `BudhaModelLoader.cs`):
  - translation: `tx' = -(tx*r00 + ty*r01 + tz*r02)`, `ty' = -(tx*r10 + ty*r11 + tz*r12)`,
    `tz' = -(tx*r20 + ty*r21 + tz*r22)`
  - rotation: the stored 3×3 **as-is** (do not transpose) → `MatrixToRotation(r00,r01,r02, r10,r11,r12, r20,r21,r22)`
  - Note the trap: transposing the translation dot-products yields a spine lying on the floor
    (height goes into the wrong axis); transposing the rotation yields a 90°-off bone.
  - s&box `ModelBuilder.AddBone` wants the **model-space (root-relative)** bind transform, matching
    `Bone.LocalTransform` / `Model.GetBoneTransform()` in cooked models (verified against
    `models/citizen/citizen.vmdl`: pelvis z=31.07", hand_L z=39.25", head z=56.54"). Do **not**
    convert to parent-relative: the skinned mesh still renders (bind is baked from the same
    transforms) but `SkinnedModelRenderer.CreateBoneObjects` collapses bone objects to the origin.
    Verified with `a01_avatar` (212 bones) and `b03_bride` (163 bones): bone objects land at
    `object.WorldPosition + bind`.

## B.9 Texture format — `.texture` / `.dds` **[CONFIRMED]**
Standard DDS. Sometimes wrapped with a **32-byte `TEX ` engine header** before the DDS (strip it
if present; DoubleFine-Explorer `uDFExplorer_Base.pas` handles it).
- Magic `20 53 44 44` `"DDS "`. Standard D3D9 `DDS_HEADER` (124 B): `dwSize=124`,
  `dwHeight@0x0C`, `dwWidth@0x10`, `dwPitchOrLinearSize@0x14`, `dwMipMapCount@0x1C`,
  `DDS_PIXELFORMAT@0x4C` (`dwFlags@0x50`: `0x04`=FourCC, `0x41`=uncompressed RGBA; `dwFourCC@0x54`).
- Formats: `"DXT1"` BC1 (4bpp), `"DXT5"` BC3 (8bpp), `"DXT3"` BC2 (rare), or FourCC `0` =
  `D3DFMT_A8R8G8B8` uncompressed (masks R=`0x00FF0000`, G=`0x0000FF00`, B=`0x000000FF`,
  A=`0xFF000000`). No YCoCg swizzle on PC. Mip chain stored largest → 1×1.

## B.10 Sound format — `.fsb` FMOD Sound Bank **[CONFIRMED]**
FSB4/FSB5. **Encrypted on disk** — decrypt first:
- **Key:** ASCII **`"DFm3t4lFTW"`** = `44 46 6D 33 74 34 6C 46 54 57` (10 bytes).
- **Decrypt per byte at file offset `i`:** `plain[i] = BitReverse8(enc[i]) XOR Key[i % 10]`.
  (`BitReverse8` reverses the 8 bits of the byte.)
- Decrypted **FSB4** header: `"FSB4"`; `u32 NumSamples@0x04`, `SampleHeaderSize@0x08`,
  `DataSize@0x0C`, `Version@0x10`, `HeadMode@0x14`.
- Codecs: **MP3** (`Mode & 0x200`), **PCM16** (`Mode & 0x10`), **IMA ADPCM** (4-bit).
  Extract to `.wav`/`.mp3` for s&box `Sound`.

## B.11 Scripts — `.lua` / `.luac` **[CONFIRMED present]**
Game logic (in `Man_Script` pack) is Lua — plain text or **compiled bytecode (`.luac`)**. A
bytecode **decompiler** (luadec/unluac-style) would be needed to read logic; this is likely the
"decompiler" of interest. Not needed for an *asset* mount, but relevant if you want to inspect or
mod gameplay. **[UNVERIFIED: exact Lua bytecode version.]**

**Type→extension / HasSizeHeader** (from `DoubleFineTool.exe.config`): `Texture → dds`; all other
types use `.<TypeName>`. `HasSizeHeader` = true for: Texture, MeshSet, Material, Cutscene,
StringTable, GibData, Outfit, Audio*, SimulationData, Stance, ComboPose, ComboAnim,
AmbMeshDefinition, Climate, Weather, OceanData, TerrainMaterial, Effect, EffectTable, Story,
LevelData, MusicSet, + gameplay tables. (`Mesh` and `Rig` are NOT in this list → no size header.)

## B.12 Animation — `AnimResource` **[IMPLEMENTED: decoder + model embedding; see HANDOFF.md]**

> **CORRECTION (2026-10-20): animation decoding and embedding are complete.** The channel index lists
> are **transform-track indices**, not bone indices. The outer AnimResource header holds a `u8[R]`
> **track→bone map** at `blob + 0x0E` (`R` = `dnap` u16 at `+0x12`); applying `bone = map[track]` is
> mandatory and was the final source of folded poses. Masks are `L` bits per dynamic track, MSB-first,
> at `maskStart*8 + k*L` (no 12-byte header; `L` = the block's last-frame index, 31 or 63). Static and
> dynamic rotation/scale/position channels are all decoded; 48-bit quats are canonical `(x,y,z,w)`,
> scale/position are IEEE half3. `Editor/Animation/BudhaAnimation.cs` samples each frame, composes in
> Buddha space, and passes **parent-local** transforms to `ModelBuilder.AddAnimation` /
> `AnimationBuilder.AddFrame`. Verified in-engine: `a01_avatar` exposes 340 embedded sequences and
> renders a coherent walk pose; the Unicorn oracle matches every tested walk/gesture frame.

> **CORRECTION (2026-10-19):** the pose table is `N × 8` bytes (not 12), at `chunk+0x48+u32[0x44]`; the
> frame table is at `chunk+0x48+u16[0x48]` with `N` entries / `N−1` intervals; blocks start at `A`
> (`u16[0x0C]`) with sixteen u16 stream lengths at `+0x00..0x1F`, `blockSize` at `+0x20`, `dataOffset` at
> `+0x22`; streams are cumulative and `sum(lengths)==blockSize-dataOffset`. The sampler's 4th argument is a
> **group bitmask** (`0xFFFF` = full pose). The 48-bit quaternion decode is verified. See `HANDOFF.md`,
> section "Animation RE state", for the corrected layout, exact bit math, sampler anchors, and the
> emulation oracle. Prefer that over the older wording below.
Counts: **15,866 `AnimResource`**, 203 `ComboAnim`, 484 `Stance`, 373 `Outfit`, 2 `AnimMap`.
The engine links **Havok Animation**: RTTI strings `hkaAnimation`,
`hkaDeltaCompressedAnimation`, `hkaSplineCompressedAnimation`,
`hkaWaveletCompressedAnimation`, `hkaInterleavedUncompressedAnimation`, and
reflected member names `NumFrames`, `numBlocks`, `maxFramesPerBlock`, `blockOffsets`,
`frameDuration`, `blockDuration`, `numberOfTransformTracks`. All payload values little-endian.

**Container layout (offsets from entry start, verified against `dlc1_3_*`, `a01_avatar`):**
| off | type | field |
|---|---|---|
| `0x00` | u32 | animation type = **1** (read first by the factory; type 0 = other class) |
| `0x04` | u64 | owner/anim-set hash (same for all anims of one character/rig) |
| `0x0C` | u8 | rotation-track count `R` |
| `0x0D` | u8 | position-track count `P` |
| `0x0E` | u8[R] | rotation track **bone indices** (sorted) |
| `0x0E+R` | u8[P] | position track **bone indices** (sorted) |
| … | f32 | duration (seconds) |
| +4 | f32 | 1 / duration |
| +8 | u8[20] | unknown metadata (zero in sampled anims) |
| +28 | u32 | bounds count (usually 0; entries are `{f32 min, f32 max}` ×count) |
| … | 4CC | **`vena`** |
| +4 | u32 | event version = 3 |
| … | char[] | NUL-terminated ASCII **event list**, e.g. `[]` or `[{Key{Event=AnimEvent_SpawnParticles{AttachToJoint=0;UseTransform=1;ParticleSystem=@…;JointName=Root;};Time=0.53333336;}]` |
| … | 4CC | **`mina`** (marker, no body) |
| … | 4CC | **`dnap`** (animation chunk; see below) |

**`dnap` body (offsets from the `dnap` tag):**
| off | type | field |
|---|---|---|
| `0x04` | f32 | duration |
| `0x08` | f32 | frame rate (30) |
| `0x0C` | u16 | first pose-block offset (relative to chunk) |
| `0x0E` | u16 | 0 |
| `0x10` | u16 | frame count (`duration*fps`) |
| `0x12` | u16 | rotation tracks `R` |
| `0x14` | u16 | position tracks `P` |
| `0x16` | u16 | **pose count** `N` |
| `0x18` | u16 | B (624 / 2560 / …) |
| `0x1A` | u16 | C (32 / 64 / …) |
| `0x1C` | u8[4] | masks/quantization (`02 01 01 01`) |
| `0x20` | u32 | 0 |
| `0x24` | u8[16] | constant `5c 7c f1 c5` ×4 (always present) |
| `0x44` | u32 | pose-table offset (relative to `chunk+0x48`) |
| `0x48` | u16 | frame-table offset (relative to `chunk+0x48`) |
| chunk+0x48+off44 | — | pose table: `N` × 8 B `{u32 blockOffset; u32 unused}`; the loader adds the chunk base to the first dword in place |
| chunk+0x48+off48 | — | frame table: `N` × 4 B `{u16 firstFrame; u16 lastFrameIndex}`; `N−1` intervals |
| chunk+blockOffset | — | pose block; `+0x00..0x1F`: 16 u16 stream lengths, `+0x20`: u16 blockSize, `+0x22`: u16 dataOffset |

A **pose block** starts at `A`/`blockOffset`. Its `+0x00..0x1F` are sixteen cumulative stream lengths,
`+0x20` is `blockSize`, `+0x22` is `dataOffset`; the streams begin at `block+dataOffset`. For a
single-rotation-track animation the first stream is 6 bytes = one **Havok 48-bit quaternion**
(e.g. `75 5d 39 58 72 b2`); the next block supplies the second key. Multi-track blocks pack two
control quats per dynamic track into these streams, with per-track mask/knot data in the other streams. Decode as
HavokLib's `Read48Quat`: take three 15-bit components centred at `0x4000`, scale by
`0.000043161`, place them into the quat using `shift = ((u16_1 >> 14) & 2) | ((u16_0 >> 15) & 1)`
(dropped/largest component is the one not stored), `w = ±sqrt(1-x²-y²-z²)` with sign from
`u16_2 & 0x8000`. Pose-block headers carry per-track bit widths / offsets (e.g. `30 00 24 00` =
size 48, dataOffset 36); multi-track packing is the remaining unknown.

**Verified examples:** `clifffaceclosing`: 1 rot track (bone 1), 30 fps, 30 frames, 2 poses;
`bloodplane_lowering`: 1 rot track (bone 0), 20 s, 600 frames, 11 poses, 64-byte block stride;
`gestures_breathe`: 150 rot + 15 pos tracks, 3.3 s, 99 frames, 3 poses.
**Exe RE anchors** (PE32, image base `0x400000`): container deserializer `0x40e2d0`,
serializer `0x40de30`, animation-chunk parser `0x414480` (type 1), factory `0x4131e0`,
4CC reader `0x45f430`. `vena` is read/written as the immediate `0x616e6576`.

---

# PART C — Implementation plan + skeleton

Reuse the **Quake mount** as the template: `QuakeMount.cs`→`BrutalLegendMount.cs`,
`Pak.cs`→`BudhaArchive.cs`, `Resource/*Loader.cs`→`Resource/*Loader.cs`.

**Files to create** (net10.0 classlib, csproj from A.3):
1. `BrutalLegendMount.cs` — `BaseGameMount`, `Ident="brutal_legend"`, `Title="Brütal Legend"`,
   `SteamAppId=225260`. `Initialize`: `IsAppInstalled(225260)`→`GetAppDirectory(225260)`, enumerate
   `Win/Packs/*.~h`, open each `BudhaArchive`, `IsInstalled=true`. `Mount`: walk each archive's
   index, `context.Add(...)` per recognized type, `IsMounted=true`. `Shutdown`: dispose archives.
2. `BudhaArchive.cs` — `.~h` (B.3–B.5) + `.~p` (B.6) reader. API like Quake `Pak`:
   `BudhaArchive(string hPath)`, `IReadOnlyList<Entry> Files`, `byte[] GetFileBytes(Entry)`,
   `Stream GetFileStream(Entry)`, `bool TryGet(string path, out Entry)`, `Dispose()`.
   `Entry { string Path; string TypeName; bool HasSizeHeader; long Offset; int Size; int ContentSize;
   int CompressFlag; bool IsCompressed; }`
3. `Resource/BudhaTextureLoader.cs` — DDS (B.9) → `Texture.Create`.
4. `Resource/BudhaModelLoader.cs` — `.Mesh.header`+`.Mesh` (B.7) + `.Rig` (B.8) → `Model`.
5. `Resource/BudhaMaterialLoader.cs` — Material → `Material` **[format UNVERIFIED]**.
6. `Resource/BudhaSoundLoader.cs` — decrypt FSB (B.10) → `Sound`.

**Register by type** in `Mount` (mirror Quake's `Register` switch): map file-type name/extension →
`ResourceType` + loader: `Texture`→Texture, `MeshSet`/`Mesh`→Model, `Material`→Material,
audio/`.fsb`→Sound.

**`BrutalLegendMount` skeleton:**
```csharp
public partial class BrutalLegendMount : BaseGameMount
{
    public override string Ident => "brutal_legend";
    public override string Title => "Brütal Legend";
    const long AppId = 225260;
    public override long? SteamAppId => AppId;

    readonly List<BudhaArchive> _packs = [];
    string _root;

    protected override void Initialize( InitializeContext context )
    {
        if ( !context.IsAppInstalled( AppId ) ) return;
        _root = context.GetAppDirectory( AppId );
        if ( _root is null ) return;

        var packs = Path.Combine( _root, "Win", "Packs" );
        foreach ( var h in Directory.EnumerateFiles( packs, "*.~h", SearchOption.TopDirectoryOnly ) )
            _packs.Add( new BudhaArchive( h ) );

        IsInstalled = true;
    }

    protected override Task Mount( MountContext context )
    {
        foreach ( var pack in _packs )
        foreach ( var e in pack.Files )
        {
            var ext = e.TypeName.ToLowerInvariant();
            switch ( ext )
            {
                case "texture":  context.Add( ResourceType.Texture, e.Path, new BudhaTextureLoader( this, e ) ); break;
                case "meshset":
                case "mesh":     context.Add( ResourceType.Model,   e.Path, new BudhaModelLoader( this, e ) ); break;
                case "material": context.Add( ResourceType.Material, e.Path, new BudhaMaterialLoader( this, e ) ); break;
                case "fsb":
                case "sound":    context.Add( ResourceType.Sound,   e.Path, new BudhaSoundLoader( this, e ) ); break;
            }
        }
        IsMounted = true;
        return Task.CompletedTask;
    }

    protected override void Shutdown()
    {
        foreach ( var p in _packs ) p.Dispose();
        _packs.Clear();
    }
}
```

**`BudhaArchive` core read (header + index), abbreviated:**
```csharp
public sealed class BudhaArchive : IDisposable
{
    public readonly List<Entry> Files = [];
    FileStream _fs;
    public BudhaArchive( string hPath )
    {
        _fs = File.OpenRead( Path.ChangeExtension( hPath, ".~p" ) );
        using var h = new BinaryReader( File.OpenRead( hPath ) );   // BIG-ENDIAN
        var magic = h.ReadBytes(4);                       // "dfpf"
        var ver   = h.ReadBytes(4);                       // BL: 02 00 00 00
        long fileTypeOffset = ReadU64BE(h), fileNameOffset = ReadU64BE(h);
        int  fileTypeCount = ReadI32BE(h), fileNameLength = ReadI32BE(h), fileCount = ReadI32BE(h);
        h.ReadBytes(4);                                   // Delim1
        ReadU64BE(h);                                     // UnknownOffset
        long dataFooterOffset = ReadU64BE(h), fileIndexOffset = ReadU64BE(h);
        long footerOffset1 = ReadU64BE(h); ReadU64BE(h);  // FooterOffset2
        ReadI32BE(h); h.ReadBytes(4);                     // UnknownCount, Delim2

        // file-type table -> names
        h.BaseStream.Seek( fileTypeOffset, SeekOrigin.Begin );
        var types = new List<string>();
        for ( int i = 0; i < fileTypeCount; i++ )
        {
            int len = ReadI32BE(h); var name = Encoding.ASCII.GetString(h.ReadBytes(len)).TrimEnd('\0');
            h.ReadBytes(12);                              // 3 unknown i32s
            types.Add( name );
        }
        // file index (16-byte bit-packed records) + names
        h.BaseStream.Seek( fileIndexOffset, SeekOrigin.Begin );
        for ( int i = 0; i < fileCount; i++ )
        {
            var d = h.ReadBytes(16);
            var e = new Entry {
                ContentSize   = (int)BitExtract(d, 0, 23),
                Size          = (int)BitExtract(d, 41, 22),
                Offset        =      BitExtract(d, 63, 30),
                FileNameOffset= (int)BitExtract(d, 96, 21),
                FileTypeIndex = (int)BitExtract(d, 117, 7),
                CompressFlag  = (int)BitExtract(d, 124, 4),
            };
            e.TypeName = types[e.FileTypeIndex];
            e.HasSizeHeader = HasSizeHeader.Contains( e.TypeName );
            // resolve name: seek fileNameOffset + e.FileNameOffset, read NUL-terminated string
            Files.Add( e );
        }
    }
    // GetFileBytes(Entry e): seek e.Offset in _fs, read e.Size, inflate if (e.CompressFlag & 4)!=0,
    //   skip 4 if HasSizeHeader, return blob[ .. e.ContentSize ]
}
```
(Add `ReadU64BE`/`ReadI32BE` big-endian helpers, `BitExtract` from B.5, and a zlib inflate via
`System.IO.Compression` — note `.~p` blobs are raw zlib streams; use `ZLibStream`/`DeflateStream`
with the right window, or `ICSharpCode.SharpZipLib` to match the tool.)

---

# PART D — Open questions / next steps

1. **Animation tracks (`.anim` / ComboAnim / Stance / Outfit).** `fmt_brutal_mesh` sets
   `LOADANIM = 0` — keyframe/bone-track layout is **[UNVERIFIED]**. Needed for animated models.
2. **`Material` payload format.** How a Material entry binds textures + shader params — needed to
   attach textures to models. **[UNVERIFIED]** (DoubleFine-Explorer `uDFExplorer_Funcs.pas` may
   hint.)
3. **Compressed texture upload.** Does `Texture.Create` accept BC1/BC3 raw blocks, or must we
   decompress to RGBA8? Inspect `Sandbox.Texture` first (A.4). Avoid writing a BC decoder if the
   API takes compressed data.
4. **Local-dev registration path.** Confirm how `MountHost` discovers mount DLLs on a dev machine
   (`<engine>/mount/` vs editor extensions) — A.3.
5. **`.Mesh` vs `.Meshset`.** Confirm which entries are the model (`.Mesh.header`+`.Mesh`) vs the
   `.MeshSet` container, and how a MeshSet groups meshes + rigs. **[UNVERIFIED.]**
6. **Lua decompiler** (if gameplay/modding is wanted): `.luac` bytecode version → pick
   luadec/unluac. Out of scope for an asset mount.

---

# Sources
- s&box mount docs: https://sbox.game/dev/doc/game-mounts/creating-mounts.md
- s&box mounting source (API + Quake sample): https://github.com/Facepunch/sbox-public/tree/master/engine/Mounting
  (`BaseGameMount.cs`, `MountContext.cs`, `ResourceLoader.cs`; `Sandbox.Mounting.Quake/{QuakeMount,Pak}.cs`,
  `Resource/QuakeTexture.cs`)
- **doublefinetool** (decompiled Double Fine Tool — `.~h`/`.~p` source of truth):
  https://github.com/patrickmollohan/doublefinetool
  (`HFile.cs`, `HFileInfo.cs`, `FileIndex.cs`, `CostumeQuestFileIndex.cs`, `StackingFileIndex.cs`,
  `PFile.cs`, `FileTypeIndex.cs`, `DoubleFineTool.exe.config`)
- **fmt_brutal_mesh** (Noesis plugin — mesh/rig source of truth): https://github.com/LordDude/fmt_brutal_mesh
  (`fmt_brutal_mesh.py`: `RigStuff()`, vertex layouts, `SBST`/`BVXD`/`BIXD`)
- DoubleFine-Explorer (Bennyboy — archive/texture/FSB): https://github.com/bgbennyboy/DoubleFine-Explorer
  (`uDFExplorer_PAKManager.pas`, `uDFExplorer_Base.pas`, `uDFExplorer_FSBManager.pas`, `uDFExplorer_Funcs.pas`)
- QuickBMS script: http://aluigi.altervista.org/bms/brutal_legend.bms ; ZenHAX: http://zenhax.com/viewtopic.php?t=1108
- gdkchan/BLPT: https://github.com/gdkchan/BLPT ; vg-resource mesh thread: https://www.vg-resource.com/thread-32259.html
- PCGamingWiki: https://www.pcgamingwiki.com/wiki/Brütal_Legend ; SteamDB 225260: https://steamdb.info/app/225260/
- Steam modding guide: https://steamcommunity.com/sharedfiles/filedetails/?id=1135737120
