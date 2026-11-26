# Brütal Legend s&box Mount Development Roadmap

This checklist outlines the sequential steps and missing pieces you need to reverse-engineer and implement to build a functional on-the-fly engine mount for *Brütal Legend* in Source 2 (s&box).

---

## Phase 1: Environment & Boilerplate Setup
Before processing any game assets, you must configure s&box to recognize the game and hook into the file system.

- [ ] **1. Identify the AppID and Registry Path**
  * Target AppID: `22560` (*Brütal Legend*).

* [ ] **2. Inherit from `BaseGameMount`**
  * Stub out your primary C# mount class inheriting from s&box's mounting API.
  * Implement the runtime initialization loop (`IsInstalled`, `MountPath`).
* [ ] **3. Intercept `mount://` Virtual Paths**
  * Define your virtual path schema (e.g., `mount://brutal_legend/models/...`).
  * Ensure requests to these paths trigger your custom file streaming handler instead of failing.

---

## Phase 2: Virtual File System & Decompression (The Archives)
*Brütal Legend* does not store files loose on disk. It packs them into paired `.~h` and `.~p` archives. Your mount must read these directly into memory.

- [ ] **4. Port the 24-bit Integer Reader**
  * The Double Fine format uses unusual 24-bit alignment configurations for some size headers. Write or find a C# utility to parse custom `Int24` or `UInt24` data from a raw byte stream.
- [ ] **5. Translate the Archive Headers (`.~h`)**
  * Reverse-engineer or port the file allocation table reader from `doublefinetool`.
  * Map how the header file references the file names, CRC hashes, sizes, and starting byte offsets inside the payload archive.
- [ ] **6. Implement On-Demand Streaming from the Payload (`.~p`)**
  * Set up a `BinaryReader` stream that targets the massive `.~p` archive.
  * Ensure when s&box requests an asset, your code seeks directly to the target byte offset, decompresses or extracts it, and hands the raw byte buffer back to the engine.

---

## Phase 3: Asset Decoding & Source 2 Mapping
Once you can extract raw bytes for a file, you have to map the Buddha Engine formats directly into Source 2 memory blocks.

- [ ] **7. Map Texture Channels (`.texture` $\to$ `.vtex` / DXGI)**
  * Parse the `.texture` header bytes to extract the width, height, mipmap count, and compression format (likely DXT1/BC1, DXT5/BC3, or raw ARGB).
  * Inject these dimensions and raw pixel arrays directly into the s&box `Texture.Create()` API.
- [ ] **8. Parse Mesh Geometry (`.mesh` / `.meshset` $\to$ `Mesh`)**
  * Adapt the vertex parsing loops from `fmt_brutal_mesh`.
  * Determine the exact interleaved vertex declaration stride (e.g., Position floats, Normal floats, and UV texture coordinates).
  * Feed these arrays sequentially into a Source 2 `Mesh` stream layout so s&box can render the static polygons.
- [x] **9. Decode Rigging and Bone Hierarchies (`.rig` $\to$ `Model`)**
  * Map how the `.rig` file stores the skeletal bones, names, and relative parent-child matrix transforms.
  * Rebuild this hierarchy inside s&box's ModelDoc runtime API so character models can support physics and custom poses.
  * **DONE (verified 2026-10-19).** Matrices are inverse-bind, row-vector with translation in the last
    row. `AddBone` takes model-space (root-relative) bind transforms; see `HANDOFF.md` → "Rig / bind
    transforms" for the exact inversion formulas and the parent-relative trap.

---

## Phase 4: Validation & Distribution
- [ ] **10. Local Sandbox Mode Testing**
  * Drop your mount project directly into your local s&box Editor extensions folder.
  * Open the spawn menu and verify that your mount routes correctly and that models load without crashing the game engine.
- [ ] **11. Exclude Copyrighted Assets from GitHub**
  * Verify that your repository contains **strictly C# source code**.
  * Ensure no game assets (`.mesh`, `.texture`, etc.) are packaged or saved into the repository to align with Facepunch distribution policies.