# RevitWallsPlugin

A Revit 2025 / 2026 add-in that turns a remote BIMy project into native, editable Revit walls. It is paired with the BIMy API; all conversion from the source data into Revit geometry happens inside the plug-in.

The add-in registers a **BIMy** ribbon panel under the **Add-Ins** tab with two controls: a **Connect BIMy** pulldown (Set API token… / Disconnect) and a **Load from BIMy** button. The Load button is disabled until a valid session has been established.

---

## Table of contents

- [What it does](#what-it-does)
- [Commands](#commands)
- [Project layout](#project-layout)
- [Data contract](#data-contract)
- [Import pipeline](#import-pipeline)
- [Wall types, thickness, and color](#wall-types-thickness-and-color)
- [Wall orientation and polygon winding](#wall-orientation-and-polygon-winding)
- [Levels](#levels)
- [Viewport handling](#viewport-handling)
- [Building](#building)
- [Installing into Revit](#installing-into-revit)
- [Usage](#usage)
- [State and persistence](#state-and-persistence)
- [Logging](#logging)
- [Troubleshooting](#troubleshooting)
- [Extending the plug-in](#extending-the-plug-in)

---

## What it does

1. Prompts for a **BIMy Project ID** (24-char hex). Accepts a bare ID or a full URL to paste-extract the ID from. The request URL is built on the fly from the saved environment: `{baseUrl}/api/data/{projectId}?model=Project`.
2. Fetches the project from that endpoint using the stored bearer token. The payload's `userObjects` are converted (meters → mm) into a flat list of wall segments, centered so the bounding-box midpoint lands at Revit's project origin.
3. Polygons are cleaned up before wall creation: consecutive coincident / collinear points are merged, sub-mm edges are dropped, and edges shared between two polygons of the same (level, type, thickness, colour) are deduped.
4. Inside a single Revit transaction:
   - **Deletes only walls previously imported by the plug-in** (tagged with `BIMy import` in instance Comments). User-drawn walls and walls from other add-ins are left alone.
   - **Creates missing levels** from the payload's per-floor heights, stacking each floor on top of the previous one, and auto-creates a floor plan view named `Plan - Level <N>` per level.
   - **Resolves wall types** keyed by `(typeName, thicknessMm, colorHex)`. Each unique combination becomes a single-layer `Basic Wall` type of the requested thickness and colour. Imported types are branded via Type Comments = `BIMy import` so users can spot / schedule them.
   - **Creates the walls** with `Wall.Create`, sets `Location Line = Finish Face: Exterior` so the polygon edge matches the visible face (not the wall centerline), and where geometry fits sets the top constraint to the next level up so multi-story stacks clean up when levels move.
   - **Tags every created wall** with instance Comments = `BIMy import <projectId>`.
   - **Un-crops newly created floor plans** and the active plan view so the imported building is immediately visible.
   - **Suppresses non-blocking warnings** (overlaps, unjoined endpoints) so the import doesn't stall behind a modal dialog.
5. After commit, zooms the active view to fit.

Every step is logged to `RevitWallsPlugin.log` next to the DLL.

---

## Commands

The add-in adds a **BIMy** ribbon panel on the **Add-Ins** tab.

### Connect BIMy ▾ → Set API token…

Opens a WPF dialog with an **Environment** dropdown, an **API token** field, and the helper line "Workspace admin can issue API keys." Picking a value and clicking **Connect** calls `GET <env>/api/auth` with `Authorization: Bearer <token>`; on success the token is stored locally (DPAPI-encrypted, current Windows user only) along with the chosen environment. The environment is **locked** on subsequent edits — disconnect to switch environments.

Supported environments:

| Name | Base URL |
| --- | --- |
| Production *(default)* | `https://bimy.app` |
| Sandbox | `https://sandbox.bimy.dev` |
| Staging | `https://staging.bimy.dev` |
| Demo | `https://demo.bimy.app` |

### Connect BIMy ▾ → Disconnect

Confirms, deletes the saved session file, and clears in-memory state. The Load button greys out again.

### Load from BIMy

Disabled until a session has been verified. Opens a **Project ID** prompt (prefilled with the last ID used for the currently-connected environment). The ID may be pasted as:

- a bare 24-character hex string (standard MongoDB ObjectId), or
- any BIMy URL containing `…/api/data/<projectId>` — the ID is extracted automatically.

The plug-in builds `{envBaseUrl}/api/data/{projectId}?model=Project` and sends the request with the saved bearer token. Everything past the fetch (transaction, walls, levels, summary) goes through `Services/ImportRunner`.

---

## Project layout

```
RevitWallsPlugin/
├── RevitWallsPlugin.csproj          # net8.0-windows, x64, references Revit DLLs
├── RevitWallsPlugin.addin           # Revit add-in manifest (1 application + 3 commands)
├── install.sh                       # Build + copy to %AppData%\Autodesk\Revit\Addins\<year>\
├── sample.json                      # Legacy example of the pre-userObjects contract
│
├── Commands/
│   ├── BimyApplication.cs           # IExternalApplication: builds the ribbon, warms up session
│   ├── SetApiTokenCommand.cs        # Dialog → BimyApi.VerifyAsync → SessionStore.Save
│   ├── DisconnectCommand.cs         # Confirms then clears persisted + in-memory session
│   ├── LoadFromBimyCommand.cs       # Project-ID prompt → ImportRunner with bearer token
│   └── LoadFromBimyAvailability.cs  # IExternalCommandAvailability for the Load button
│
├── Models/
│   ├── BimyEnvironment.cs           # Environment enum + base URLs + ProjectDataUrl()
│   ├── AuthDtos.cs                  # AuthResponse / BimyUser
│   ├── ProjectDtos.cs               # UserObjectsPayload / UserObjectDto / FloorTypeDto
│   └── WallDtos.cs                  # Internal WallDto used by the pipeline
│
├── Services/
│   ├── ImportRunner.cs              # Orchestrates the full import pipeline (projectId-aware)
│   ├── JsonFetcher.cs               # Generic HTTP → deserialised T (bearer optional)
│   ├── BimyApi.cs                   # VerifyAsync + FetchUserObjectsAsync (bearer required)
│   ├── Session.cs                   # SessionStore (DPAPI-encrypted JSON next to DLL)
│   ├── SessionState.cs              # In-process cache + RefreshAsync()
│   ├── ProjectBuilder.cs            # userObjects → flat List<WallDto>: winding, merge, dedupe, centering
│   ├── WallBuilder.cs               # Deletes imported walls, creates new ones, tags + top-constraints them
│   ├── WallTypeProvider.cs          # Per-(name, thickness, color) single-layer wall-type factory
│   ├── RevitLookup.cs               # Level lookups + EnsureLevels (+auto-named plan views)
│   ├── SuppressWarningsPreprocessor.cs  # IFailuresPreprocessor for the transaction
│   ├── ProjectIdState.cs            # Persists last Project ID per environment, next to the DLL
│   └── Log.cs                       # Thread-safe append-only file logger
│
└── UI/
    ├── BimyRibbon.cs                # Builds the BIMy panel, pulldown, and Load button
    ├── SetApiTokenDialog.cs         # WPF env+token dialog
    └── ProjectIdDialog.cs           # WPF Project-ID dialog, parented to Revit; accepts bare id or URL
```

---

## Data contract

The plug-in reads the BIMy data endpoint:

```
GET {baseUrl}/api/data/{projectId}?model=Project
Authorization: Bearer <token>
```

Response shape (only the `userObjects` field is consumed):

```jsonc
{
  "_id": "…",
  "userObjects": [
    {
      "floors":        [0, 0, 1, 0],       // per-floor index into `types`. Default [0].
      "polygonPoints": /* see below */,    // outer polygon of the object (+ optional holes)
      "types": [
        {
          "name":      "Generic",          // shown as the wall-type name root
          "height":    3,                  // floor height in METERS
          "thickness": 0.2,                // optional wall thickness in METERS (default 0.2)
          "color":     "#a0c4ff",          // optional hex color for the wall
          "walls":     /* optional per-type polygon overriding polygonPoints */
        }
      ]
    }
  ]
}
```

### Polygon shape

`polygonPoints` (and `types[].walls`) accept any of the following shapes — see `Services/ProjectBuilder.cs`:

| Shape | Meaning |
| --- | --- |
| `[]` or `null` | nothing |
| `[{x,y}, {x,y}, ...]` | a single flat ring (legacy format) |
| `[[{x,y}, {x,y}, ...], ...]` | array of simple rings (one outer ring per polygon) |
| `[[[{x,y}, ...], [{x,y}, ...], ...], ...]` | array of `[outer, hole1, hole2, …]` polygons — holes **are** materialised as walls |

### Units

- `polygonPoints.x`, `y` are in **meters** (matches the upstream editor).
- `types[].height`, `types[].thickness` are in **meters**.
- The plug-in multiplies all inputs by 1000 to produce its internal millimetre representation and then converts to Revit internal units (feet) at wall-creation time.

### Why raw userObjects, not pre-processed walls

An earlier iteration had the API return `{ units, walls: [...] }` already expanded. Moving the conversion into the plug-in means any plug-in update can evolve the mapping without a backend deploy, and keeps the API boundary minimal.

---

## Import pipeline

The single source of truth is `Services/ImportRunner.Run(UIApplication, BimyEnvironment, projectId, url, token, ref message)`.

```
┌─────────────────────────────────────────────────────────────────────────────┐
│ 1.  JsonFetcher.FetchAsync<UserObjectsPayload>(url, token)                   │
│      → HTTP GET, 30 s timeout, deserialise (case-insensitive)                │
├─────────────────────────────────────────────────────────────────────────────┤
│ 2.  ProjectBuilder.BuildWalls(payload)                                      │
│      → iterate userObjects × floors                                         │
│      → parse polygons, normalise winding (outer CW, holes CCW)              │
│      → merge collinear/coincident points, drop sub-mm edges                 │
│      → emit one WallDto per edge, convert m → mm                            │
│      → dedupe edges shared between rings (same level/type/thickness/colour) │
│      → centre by bounding-box midpoint                                      │
├─────────────────────────────────────────────────────────────────────────────┤
│ 3.  Transaction("Load from BIMy")                                           │
│     │                                                                       │
│     │  SuppressWarningsPreprocessor   # auto-dismiss non-error warnings    │
│     │                                                                       │
│     │  WallBuilder.CreateWalls(doc, walls, elevations, "BIMy import …")     │
│     │   ├─ DeleteImportedWalls            # filters on instance Comments   │
│     │   ├─ RevitLookup.EnsureLevels       # auto-create missing + plans    │
│     │   ├─ new WallTypeProvider(doc)      # pre-caches existing BIMy types │
│     │   └─ for each WallDto:                                                │
│     │       ├─ typeProvider.Get(name, thickness, hex)                       │
│     │       ├─ RevitLookup.ResolveLevel                                     │
│     │       ├─ Wall.Create(line, typeId, levelId, heightFeet, …)            │
│     │       ├─ set Location Line = Finish Face: Exterior                    │
│     │       ├─ if fits: set top constraint to the next level up             │
│     │       └─ set instance Comments = "BIMy import <projectId>"            │
│     │                                                                       │
│     │  UncropNewlyCreatedPlans(doc)                                         │
│     │  DisableActiveCrop(doc)                                               │
│     │                                                                       │
│     │  tx.Commit()                                                          │
├─────────────────────────────────────────────────────────────────────────────┤
│ 4.  ZoomActiveViewToFit(uiDoc)                                              │
├─────────────────────────────────────────────────────────────────────────────┤
│ 5.  Summary dialog + log                                                    │
└─────────────────────────────────────────────────────────────────────────────┘
```

Notes:

- **HTTP happens before the transaction opens.** If the fetch is slow or fails, Revit state is untouched.
- **One transaction encapsulates deletion + level/view creation + wall creation + crop toggle.** If anything throws, the entire import rolls back — no half-imported building.
- **Delete step is scoped.** Only walls whose instance Comments start with `BIMy import` are removed. Walls drawn by the user or other add-ins survive re-imports.
- **Zoom-to-fit is outside the transaction** and targets only the active view (no longer re-frames every open viewport).

---

## Wall types, thickness, and color

`Services/WallTypeProvider` materialises one Revit `WallType` per unique `(typeName, thicknessMm)` combo encountered during the import. Color is carried on the type's material rather than encoded in its name.

- **Single-layer.** Each type is built with `CompoundStructure.CreateSingleLayerCompoundStructure` so the layer's thickness exactly matches the source `thickness`. Earlier behaviour inherited the default Basic Wall's compound structure, which quietly ignored `thickness`.
- **Naming.** `"<TypeName> <Thickness>mm"`. Deterministic — repeated imports reuse the same type. The first color seen for a given `(typeName, thickness)` wins; later walls with the same name+thickness share it.
- **No `RWP` prefix.** Types carry the original user-facing name. They're identified programmatically via `Type Comments = "BIMy import"` so schedules stay readable.
- **Color parsing.** `types[].color` may be supplied as `#rrggbb`, bare `rrggbb`, shorthand `#rgb`, or `rgb(r,g,b)` / `rgba(r,g,b,a)` (alpha is dropped, percent components supported). Unparseable values yield a wall with no assigned material rather than a silent gray fallback.
- **Materials.** When a `color` is supplied, a Material named `BIMy <#hex>` is created (or reused) with:
  - `Color` set to the hex
  - `UseRenderAppearanceForShading = false` so shaded views reflect the colour
  - Surface foreground and Cut foreground pattern = solid fill, coloured to the hex, so plan / section / hidden-line views also render the colour
- **Cache.** The `WallTypeProvider` constructor pre-populates its cache from types and materials already in the project (matching the naming convention above), so re-runs don't rebuild them.

Per-import state: new types and materials persist in the project. On the next import they are reused by (name, thickness) and, if the cached type is re-encountered, its material is re-applied from the first color seen that run.

---

## Wall orientation and polygon winding

Revit's wall orientation with `flip=false` is `Z × curveDirection`, which puts the exterior face on the **left** of the curve direction. To make the polygon edge match the visible wall face:

1. `ProjectBuilder` normalises each outer ring to **clockwise** and each hole to **counter-clockwise** (standard shoelace signed-area check).
2. `WallBuilder` creates every wall with `flip=false` and `WALL_KEY_REF_PARAM = FinishFaceExterior`.

The net effect:

- **Outer rings** — the source's outer polygon edge = the outside face of the wall. Walls grow inward, into the building footprint.
- **Holes (courtyards / lightwells)** — the source's inner polygon edge = the courtyard-facing face of the wall. Walls grow outward into the surrounding building material.

Source polygons may be supplied in either winding direction; the normalisation step makes the result consistent either way.

---

## Levels

`RevitLookup.EnsureLevels(doc, referencedNames, elevationsMm)` runs inside the transaction.

1. Builds a map of existing levels by name (case-insensitive).
2. For each `Level <N>` referenced by the import, creates the level at the cumulative elevation computed by `ProjectBuilder.ComputeLevelElevations` (tallest floor per index wins when user objects disagree).
3. Existing `Level <N>` levels are **re-elevated** to the computed value, so imports stay geometrically consistent when floor heights change.
4. Any other referenced names with no elevation are stacked above the current highest level with a 3 m default spacing.
5. Each newly-created level gets a floor plan view named `Plan - <levelName>`.

Non-`Level <N>` level names encountered on existing levels are **not** re-elevated — the plug-in assumes those were placed by the user.

---

## Viewport handling

Each import run, inside the transaction:

- Floor plans for every level created in this run have their crop box switched off.
- If the active view is a `ViewPlan` with `CropBoxActive == true`, it's switched off too.

After commit, outside the transaction:

- Only the active view is `ZoomToFit`-ed. Other open viewports keep their framing.

---

## Building

Prerequisites:

- .NET SDK 8.x (`dotnet --version`).
- Revit 2025 installed at `C:\Program Files\Autodesk\Revit 2025\` (for `RevitAPI.dll`, `RevitAPIUI.dll`).

```bash
# Revit 2025 (default)
dotnet build -c Release

# Revit 2026
dotnet build -c Release -p:RevitVersion=2026

# Pointing at a non-standard install path
dotnet build -c Release '-p:RevitInstallDir=D:\CustomPath\Revit 2025\'
```

Target framework is `net8.0-windows`, platform `x64`, `UseWPF=true`.

Output ends up in `bin/Release/` as `RevitWallsPlugin.dll` plus the `.addin` manifest (copied via `CopyToOutputDirectory`).

---

## Installing into Revit

Run `install.sh` (Git Bash):

```bash
./install.sh           # Revit 2025 (default)
./install.sh 2026      # a different year
./install.sh 2025 --no-restart   # don't relaunch Revit afterwards
```

What it does:

1. Verifies `C:\Program Files\Autodesk\Revit <year>\RevitAPI.dll` exists.
2. Detects whether Revit is running; if so, `taskkill Revit.exe` and waits up to 10 s.
3. `dotnet build -c Release -p:RevitVersion=<year>`.
4. Copies `RevitWallsPlugin.dll` and `RevitWallsPlugin.addin` to `%AppData%\Autodesk\Revit\Addins\<year>\`.
5. Relaunches Revit if it was running when the script started.

Manual install: copy the two files from `bin\Release\` to `%AppData%\Autodesk\Revit\Addins\2025\`, then launch Revit. On first load Revit shows a security prompt for unsigned add-ins — click **Always Load**.

---

## Usage

1. Open any Revit project (a Metric Architectural template works well).
2. **Add-Ins → BIMy → Connect BIMy → Set API token…**. Pick the environment, paste the API token, click **Connect**.
3. **Add-Ins → BIMy → Load from BIMy**. Paste the Project ID (or a full URL — the ID is extracted) and click OK.
4. After the import:
   - The summary dialog reports walls created, walls replaced, auto-created levels, auto-created wall types.
   - **Project Browser → Floor Plans** has `Plan - Level 1 … Plan - Level <N>` for every floor.
5. Re-running **Load from BIMy** for the same project ID replaces only the previously-imported walls; user-drawn walls survive.

---

## State and persistence

Files live next to the DLL (`%AppData%\Autodesk\Revit\Addins\2025\`):

| File | Purpose |
| --- | --- |
| `RevitWallsPlugin.session.json` | Saved environment + DPAPI-encrypted API token. Encrypted with `DataProtectionScope.CurrentUser` — only the same Windows user can decrypt it. |
| `RevitWallsPlugin.lastProjectId.<env>.txt` | Last Project ID used, kept per environment so switching env doesn't surface a stale ID. |
| `RevitWallsPlugin.log` | Append-only run log. Never auto-truncated. |

All are best-effort — `Services/SessionStore`, `Services/ProjectIdState`, and `Services/Log` swallow I/O exceptions so persistence failures can never crash the command.

---

## Logging

`Services/Log` appends lines like:

```
2026-04-22 18:55:12.318 [INFO ] ---- Import run invoked ----
2026-04-22 18:55:14.227 [INFO ] Fetching userObjects from Staging · project 65f… (https://staging.bimy.dev/api/data/65f…?model=Project)
2026-04-22 18:55:14.527 [INFO ] Fetched 3 userObject(s).
2026-04-22 18:55:14.551 [INFO ] Built 60 wall definition(s) across 10 level(s) from 3 userObject(s).
2026-04-22 18:55:14.551 [INFO ] Creating walls…
2026-04-22 18:55:15.173 [INFO ] Build pass finished. Created(in-memory)=60, Skipped=0. Committing…
2026-04-22 18:55:15.326 [INFO ] Transaction committed. Deleted=60, Created=60, Skipped=0.
2026-04-22 18:55:15.327 [INFO ] Auto-created levels: Level 3, Level 4, Level 5, Level 6, Level 7, Level 8, Level 9, Level 10
2026-04-22 18:55:15.327 [INFO ] Auto-created wall types: Generic 200mm
2026-04-22 18:55:15.342 [INFO ] Disabled crop box on active view 'Plan - Level 1'.
2026-04-22 18:55:15.420 [INFO ] Zoomed active view to fit.
```

Tail live from PowerShell:

```powershell
Get-Content "$env:APPDATA\Autodesk\Revit\Addins\2025\RevitWallsPlugin.log" -Wait -Tail 50
```

---

## Troubleshooting

**Walls are created but invisible.**
Run `ZF` (Zoom to Fit) in the active view. Check that the active view is a plan view for a level that was in the payload (e.g. `Plan - Level 1`). In 3D views walls always show.

**Load from BIMy is greyed out.**
No verified session. Run **Connect BIMy → Set API token…**. Tail the log to see verify errors.

**"Please enter a 24-character hex project ID" in the Load dialog.**
The pasted string isn't recognised as an ID or a BIMy URL containing one. Valid forms: a bare 24-char hex, or any URL with `/api/data/<24hex>` in it.

**"Token rejected" after Set API token.**
Token wrong or wrong environment. Workspace admin can issue a new key. Use Disconnect to switch environments.

**Walls face the wrong way.**
The polygon may be wound in an unexpected direction; the plug-in normalises outer rings to CW and holes to CCW, but if the source mixes conventions inconsistently, individual walls can flip. Select the wall and press space bar to flip its orientation.

**Wall thickness is wrong after import.**
`types[].thickness` is in meters. A value of `200` produces 200 m walls. The plug-in now honours this field (previously ignored); check the source data.

**Import hangs mid-run.**
Usually a Revit modal warning that `SuppressWarningsPreprocessor` didn't catch. Check the log for "Transaction failed". Alt+Tab to surface any hidden dialog.

**MSBuild warning MSB3277 about version conflicts.**
Expected and benign — Revit's DLL graph drags in multi-version references.

---

## Extending the plug-in

Common additions, and where they'd hook in:

| Need | Change |
| --- | --- |
| Support a new JSON shape | Extend DTOs in `Models/ProjectDtos.cs` and parsing in `Services/ProjectBuilder.cs`. |
| Add doors / windows / slabs | New DTO + new `Services/Xxx Builder.cs`; wire from `WallBuilder.CreateWalls` or a new runner stage. |
| Multi-layer wall types | Replace `ApplySingleLayer` in `Services/WallTypeProvider.cs` with a `CompoundStructure.CreateCompoundStructure(layers)` call. |
| Don't delete previously-imported walls | Skip the `DeleteImportedWalls(doc)` call in `WallBuilder.CreateWalls`. |
| Don't auto-create plan views | Remove the `ViewPlan.Create` call in `RevitLookup.EnsureLevels`. |
| Import from a non-BIMy source | Replace `BimyApi.FetchUserObjectsAsync` with another fetcher; keep the same `UserObjectsPayload` shape so the rest of the pipeline works unchanged. |

Each `Services/*.cs` file is scoped to one responsibility (fetching, building, creating, looking up, logging, persisting) so most extensions touch one or two files.
