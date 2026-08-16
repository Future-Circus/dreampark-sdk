# Asset Browser

`DreamPark → Asset Browser`

Browse everything Dreamie has generated and pull models straight into a content
folder, imported and ready to use — DreamPark import settings, textures sized
and colour-spaced for Quest, materials on `DreamPark-UniversalShader`.

---

## The shape of it

| File | Role |
|---|---|
| `DreamieCatalog.cs` | DTOs. No Unity types — parse and hold, nothing else. |
| `DreamieCatalogApi.cs` | `/api/assets` — list, facets, jobs, favourites. |
| `ThumbnailCache.cs` | Remote grid previews. Disk-cached, budgeted, destroyed on close. |
| `AssetBrowserWindow.cs` | The IMGUI window. |
| `DreamieFolders.cs` | Where things land, what they are called, what we already have. |
| `DreamieDownloader.cs` | Network → staging → project, in two phases. |
| `DreamieProvenance.cs` | Which library asset a model came from. |
| `ModelImportProfile.cs` | The `ModelImporter` settings table. |
| `DreamieImportPass.cs` | The five passes, in order. |
| `DreamieTextureSetup.cs` | Texture roles, sizes, colour space. |
| `RoughnessToSmoothness.cs` | One pixel op, for one shader quirk. |
| `DreamieMaterialWiring.cs` | `MaterialConverter` handoff plus the gaps it leaves. |
| `AssetBrowserMenu.cs` | Right-click re-apply, and two maintenance items. |

---

## Five things worth knowing before you change anything

**1. The importer is an explicit pass, not an `AssetPostprocessor`.**
A postprocessor fires for every model in the project and is scoped only by a
path predicate you have to keep correct forever — the failure mode is silently
rewriting a creator's own art. It would also force a full reimport of every FBX
in every project whenever this settings table changed, and it would revert any
setting a creator adjusted by hand. Import settings serialize into the `.meta`
either way, so nothing is lost by doing it explicitly. Hand-dragged files are
covered by the right-click item.

**2. `_rougnessMap` on the DreamPark shader is consumed as SMOOTHNESS.**
There is no invert node anywhere in `DreamPark-UniversalShader.shadergraph`,
and `MaterialConverter` maps `_RoughnessMap` straight into it. AI generators
ship true roughness (glTF's convention), so binding it directly renders every
surface inside out — matte reads glossy. `RoughnessToSmoothness` bakes an
inverted copy at import and binds that; the original stays on disk, unbound.

The real fix is a OneMinus node in the shader. That file must stay
byte-identical to dreampark-core, so it is a core-side decision — this is the
workaround that does not touch a synced file.

**3. Provenance lives in `AssetImporter.userData`, and it has to.**
`ThirdPartySyncTool` moves referenced files from `ThirdPartyLocal/` to
`ThirdParty/` on Compile & Upload. A sidecar JSON would stay behind — so the
first time a creator actually *uses* a mesh, it would part company with its
record. `userData` rides in the `.meta` and moves with the asset.

`.dreamie-index.json` is a cache, regenerable from the `Dreamie` asset label
(`DreamPark → Troubleshooting → Rebuild Dreamie Asset Index`). Deleting it is
always safe.

**4. Filenames carry a unique stem, deliberately.**
`ContentProcessor` addresses models as `{gameId}/Models/{filename}` and throws
the folder structure away, so two `shrimp.fbx` anywhere in one content package
collide silently. Every asset is named `{slug}__{first 8 of assetId}` — folder,
FBX, materials and textures all share it.

**5. Downloads stage outside `Assets/` first.**
A partially written FBX inside `Assets/` gets imported the moment focus returns,
producing a broken asset with a real GUID. Staging under `Library/` makes a
failed download a no-op. It is also what lets the whole install phase be
synchronous, which is what lets it sit inside
`ContentProcessor.ExecuteWithWatchdogPaused` — otherwise thirty new files wake
the folder watchdog thirty times.

---

## Where the numbers come from

Not guesses — Dreamie's own generation config (`dreamie/config.js`,
`dreamie/providers/tripo.js`):

- **`face_limit` 5000, triangle topology.** These are already budget meshes,
  which is why mesh compression is `Off` rather than `Low`: at 5k triangles
  quantisation saves a rounding error and risks shading seams.
- **`texture_size` 4096, PNG.** Right for a library master, far too large for a
  Quest build. Textures are capped at 1024 (colour) / 512 (data) on import.
  This is the single biggest thing the pipeline fixes.
- **`auto_size` true** — "scale the mesh to real-world metres... does not change
  geometry, only the transform." So `useFileScale` is meaningful and the bounds
  check should normally pass.
- **`pivot_to_center_bottom` true** — props arrive with their origin at the base.

Colour space matters here: the project renders Linear, so base colour and
emissive must be sRGB and metallic / roughness / occlusion must not be. The
Texture Optimizer deliberately never writes those flags — it only reads them —
so nothing else in the SDK was setting them.

---

## If something looks wrong

| Symptom | Look at |
|---|---|
| Everything looks wet / matte reads glossy | The smoothness bake failed — check the Console, and whether a `*__Smoothness.png` exists next to the model |
| Normal map appears to do nothing | `_nrmStrength` defaults to **0** on the shader. `DreamieMaterialWiring.Fixup` sets it to 1 when a normal map is bound |
| Metal renders as plastic | Same shape: `_metallicness` defaults to 0 and multiplies the map |
| Model is enormous or invisibly small | The import report warns above 20 m / below 1 cm. Check Scale Factor, and Bake Axis Conversion if it is also on its side |
| Model missing from the Attractions browser | Not this tool — check the prefab root has the right template component |
| Grid is empty but the web library has assets | A composite index may still be building; the window says so. Otherwise check the type filter — it defaults to 3D Models |

Re-run the pipeline on anything already in the project with
**right-click → DreamPark → Apply DreamPark Model Import Settings**. It works on
a selection or a whole folder, and preserves provenance.
