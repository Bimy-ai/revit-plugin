# Native Revit API import — design & plan

Status: implemented alongside this document. The plugin's primary pull path is
now **native Revit element creation from a parametric JSON payload**, with the
IFC pipeline retained as the automatic fallback for projects whose model is an
imported IFC file (where Revit's own IFC importer remains the honest converter).

## Why move off IFC for drawn projects

Revit's IFC importer produces *correct* elements but not *native-feeling* ones:
imported types multiply, walls lose their location-line semantics, doors arrive
as baked families with no shared types, compound structures flatten, and the
project browser fills with import artifacts. For buildings **drawn in BIMy** we
hold perfect parametric source data — wall centrelines with baselines and
layered types, hosted openings with sizes and operations, slab rings, roof
shapes with pitch, derived rooms — exactly the inputs `Wall.Create`,
`Floor.Create`, `FootPrintRoof` and friends want. Converting parametric →
STEP → heuristic re-parametrisation throws that away.

An earlier native importer (deleted in `6bed0aa`) died of **drift**: it walked
raw `userObjects` in C# and re-derived geometry itself, so every app feature
(doors, materials, ceilings) had to be ported twice. The 2400 lines were a
second authority on what a building is.

## The architecture that avoids both failure modes

**The app stays the single authority.** The frontend's generator graph — the
same `enumerateDrawn` / `enumeratePlaced` / `buildProjectModel` streams that
feed `ifcGenerate.js` — gains a *second serializer*, `revitModel.js`, which
emits a normalized parametric JSON (`bimy-revit-model/1`) instead of STEP text.
It performs **no geometry derivation of its own**: every number it writes is
read from the exact resolved element stream the IFC writer consumes, so a new
app feature lands in both outputs from the same enumeration (and a parity test
pins that the two outputs agree on element counts).

The serializer is exported through the same headless facade
(`headlessRevit.js → generateRevitModel`) and rides the same committed rolldown
bundle into the API (`npm run sync:revit-gen`).

The plugin maps that JSON ~1:1 onto Revit API calls. No geometry solving in
C# — Revit itself does wall joins, hosted-opening cuts, roof skeletons and room
boundaries, which is precisely what makes the result native.

```
 bim-frontend                      bim-api                          revit-plugin
 ┌────────────────────┐   bundle   ┌───────────────────────┐  GET   ┌─────────────────────┐
 │ enumerateDrawn ──┬─►│ ifcGen    │ /export/revit-ifc/:id │◄──────►│ RevitIfcImporter    │
 │ enumeratePlaced  │  │ (STEP)    │  (imported models,    │ fallbk │  (fallback path)    │
 │ projectModel     │  │           │   snapshots)          │        │                     │
 │ elementTypes     └─►│ revitModel│ /export/revit-model/  │◄──────►│ NativeModelImporter │
 │ materialBridge      │ (JSON)    │   :id  (drawn models) │  JSON  │  levels/walls/doors │
 └────────────────────┘            └───────────────────────┘        │  floors/roofs/rooms │
                                                                    └─────────────────────┘
```

## Which path serves which project

Mirrors the IFC route's own resolution rule:

| Project state                                  | `/revit-model/:id` answers      | Plugin does |
| ---------------------------------------------- | ------------------------------- | ----------- |
| Drawn/sketched (no stored source IFC)          | the parametric JSON, ETag `"genm-<stamp>"` | **native build** |
| Imported model (stored IFC), mixed, snapshots  | `{ "fallback": "revit-ifc" }`   | existing IFC pull |
| Nothing drawn or imported                      | 404 `nothing_to_load`           | "nothing to load" dialog |

The plugin always calls `/revit-model` first with its cached ETag; a `304`
reuses the local `.rvt` exactly as today. Any native-path failure (payload,
build, or save) is caught and offered the IFC pipeline as a fallback, so no
project that pulled before can stop pulling.

## The payload (`bimy-revit-model/1`)

Units: **millimetres**, angles in degrees. Plan coordinates `[x, y]` with
`x = world x`, `y = −world z` (the IFC plan frame); elevations are Z-up. All
identity is the app's durable element guids.

| Section | Carries | Revit mapping |
| --- | --- | --- |
| `storeys` | name, elevation, height | `Level.Create` + floor/ceiling plans; one extra top level |
| `grids` | named axes, segment per axis | `Grid.Create` + name |
| `materials` | id, **Revit template name** (via `materialBridge`), color, category | `Material.Create`, color + solid fill patterns |
| `elementTypes` | construction cats: layer stack `{function, materialId, thicknessMm}`; opening cats: width/height/sill/operation | `WallType`/`FloorType`/`CeilingType`/`RoofType` duplicates with `CompoundStructure`; door/window symbol duplicates with size params |
| `walls` | per-storey segments `a→b`, true `arc` (chord chains collapsed), thickness, height, baseOffset, baseline −1/0/+1, typeId, materialId, structural kind | `Wall.Create` (Line/Arc), `WALL_KEY_REF_PARAM` from baseline, base offset, top constraint to the matching level, structural usage |
| `openings` | hostWallId, kind door/window/opening, **world center point**, width/height/sill, operation, flips, unfilled | hosted `NewFamilyInstance` on the wall (sill via instance param, flips applied); `unfilled` → `doc.Create.NewOpening` |
| `floors`, `ceilings` | ring + holes (+arc bulges), zOffset, thickness, slope | `Floor.Create` / `Ceiling.Create` with CurveLoops; slope-arrow overload when sloped |
| `roofs` | shape flat/shed/gable/hip, footprint (overhang applied), pitch, per-edge `slopedEdges`, thickness, exact `mesh` fallback | `NewFootPrintRoof` + `set_DefinesSlope`/`set_SlopeAngle` per edge; DirectShape mesh fallback if footprint rejected |
| `solidElements` | stairs/ramps/columns/foundations/piles/railings/balcony guards as extrusions + cylinders, category tag | `DirectShape` (real extruded solids via `GeometryCreationUtilities`) in the right category (`OST_Stairs`, `OST_StructuralFoundation`, …) |
| `furnitureTypes` + `furniture` | one tessellated mesh per distinct catalog kind; instances by point/rotation/scale | `DirectShapeType` + `DirectShape` instances (shared geometry, right category) |
| `rooms` | name, number, **guaranteed-interior point**, height | `doc.Create.NewRoom` + name/number/upper limit |
| `location` | lat/lng/address | `SiteLocation` + project address |
| `stats`, `unhandled` | per-kind counts; anything the serializer saw but could not express | summary dialog parity check; honest reporting |

Balcony slabs are emitted into `floors` (they are walked on); balcony guards
into `solidElements` as railing-category solids.

## Plugin pipeline

`LoadFromBimyCommand` → `BimyImportCoordinator`:

1. Conditional `GET /revit-model` (ETag from `PullCache`); 304 → the existing
   "up to date" dialog.
2. Fallback marker → `RevitIfcImporter.Run` (unchanged).
3. Payload → `TargetPath.Resolve` first (same no-clobber rules), then
   `NativeModelBuilder`:
   - fresh `NewProjectDocument` from the user's default template (metric
     fallback), so door/window families and type seeds come from *their*
     template;
   - phases, each its own transaction with the warning-swallowing
     preprocessor: levels+plans+grids → materials+types → walls → openings →
     slabs+roofs → solids+furniture → rooms → finishing (project name, 3D view,
     ghost-level cleanup);
   - per-element try/catch — one bad element degrades to a logged warning and
     the summary, never a failed pull;
   - door/window symbols resolved template-first, then the Revit content
     library on disk, and degrade to a plain wall opening (counted and
     reported) when a machine has neither.
4. `SaveAs` → deliver (open/activate or link) → summary with per-category
   counts vs the payload's own `stats`.

Every created element carries `Comments = "BIMy import <projectId>"` and
`Mark = <element guid>` so re-imports and audits can find them.

## Compatibility

The add-in keeps loading on Revit 2022–2030: every API used exists in 2022
(`Floor.Create`/`Ceiling.Create` with CurveLoops, `NewFootPrintRoof`,
`DirectShape`, `UnitTypeId`). CI still builds against the pinned year with
`UseLocalRevitApi=false`.

## Drift guards (the lesson from `6bed0aa`)

- `bim-frontend/test/revitModel.test.mjs` — fixture building (walls incl. arcs
  & graph walls, door, window, slabs, roof, rooms, furniture, types, custom
  material) snapshots the payload shape AND asserts `unhandled` is empty.
- `bim-api/test/revitModelRoute.test.mjs` — same fixtures through the committed
  bundle: parity of element counts between `generateRevitIfc` and
  `generateRevitModel`, determinism for the stamp-based ETag, and the
  fallback marker for stored-IFC projects.
- The serializer consumes the enumeration streams *only* — any new `shape` it
  does not recognise lands in `unhandled`, which the tests refuse.

## Rollout

1. Frontend serializer + facade export (`generateRevitModel`).
2. API: `npm run sync:revit-gen` (new bundle committed), `generateProjectRevitModel`
   beside `generateProjectIfc`, route `GET /api/export/revit-model/:projectId`.
3. Plugin: coordinator + builders; IFC path untouched as fallback.
4. Prod note: bimy.app picks the API change up only after the usual manual
   `infra/build.sh --stage=production`; until then the plugin's fallback keeps
   every pull working against old deployments (404 on `/revit-model` → IFC).
