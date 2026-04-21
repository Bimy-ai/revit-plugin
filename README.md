# RevitWallsPlugin

A Revit 2025 / 2026 add-in that turns a remote JSON building definition into native, editable Revit walls. It is paired with an API endpoint that returns the raw project objects; all conversion from the source data into Revit geometry happens inside the plug-in.

The add-in ships two commands: **Import Walls From URL** (prompt for a URL, remember it) and **Refresh Walls** (re-use the remembered URL, one click, no dialog).

---

## Table of contents

- [What it does](#what-it-does)
- [Commands](#commands)
- [Project layout](#project-layout)
- [Data contract](#data-contract)
- [Import pipeline](#import-pipeline)
- [Wall types, thickness, and color](#wall-types-thickness-and-color)
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

1. Fetches JSON from a URL you supply. The endpoint is expected to return a raw `userObjects` payload (the same shape used by the BIMy frontend project editor). See [Data contract](#data-contract).
2. Converts those objects (in meters, polygon rings) into a flat list of wall segments in millimetres, centered so the bounding-box midpoint of the whole building is at Revit's project origin.
3. Inside a single Revit transaction:
   - **Deletes every existing wall** in the active document (clean slate).
   - **Creates missing levels** parsed from names like `L3` → elevation `(3-1) × 3000 mm`, and auto-creates a floor plan view for each.
   - **Duplicates the project's default Basic Wall type** once per unique color referenced, setting the structural layer's material to a material in that color. The wall's original compound structure (finishes, substrate, thermal layer) is preserved so the result looks and behaves exactly like a wall drawn with Revit's Walls tool.
   - **Creates the walls** using `Wall.Create`.
   - **Turns off the crop box** on the active plan view so walls near the edge don't clip.
   - **Suppresses non-blocking warnings** (overlaps, unjoined endpoints) so the import doesn't stall behind a modal dialog.
4. After commit, zooms every open view to fit — the building is on screen immediately.

Every step is logged to `RevitWallsPlugin.log` next to the DLL.

---

## Commands

Both appear under **Add-Ins → External Tools** once the add-in is loaded.

### Import Walls From URL

Opens a small WPF dialog titled "Import Walls From URL" with a **Provide url** field, **OK**, and **Cancel**. The text box is pre-filled with the last URL you used (or a placeholder on first run). On OK the URL is persisted and the import pipeline runs.

### Refresh Walls

No dialog. Reads the last-used URL from disk and runs the same pipeline. If nothing has been used yet, it tells you to run Import Walls From URL first.

Both commands end by calling `Services.ImportRunner.Run()` — so any fix or enhancement to the pipeline benefits both.

---

## Project layout

```
RevitWallsPlugin/
├── RevitWallsPlugin.csproj          # net8.0-windows, x64, references Revit DLLs
├── RevitWallsPlugin.addin           # Revit add-in manifest (two commands)
├── install.sh                       # Build + copy to %AppData%\Autodesk\Revit\Addins\<year>\
├── sample.json                      # Legacy example of the pre-userObjects contract
│
├── Commands/
│   ├── ImportWallsFromUrlCommand.cs # Prompts, saves, calls ImportRunner
│   └── RefreshWallsCommand.cs       # Reads saved URL, calls ImportRunner
│
├── Models/
│   ├── ProjectDtos.cs               # UserObjectsPayload / UserObjectDto / FloorTypeDto
│   └── WallDtos.cs                  # Internal WallDto used by the pipeline
│
├── Services/
│   ├── ImportRunner.cs              # Orchestrates the full import pipeline
│   ├── JsonFetcher.cs               # Generic HTTP → deserialised T
│   ├── ProjectBuilder.cs            # userObjects → flat List<WallDto> + centering
│   ├── WallBuilder.cs               # Deletes old walls, creates new ones
│   ├── WallTypeProvider.cs          # Per-color wall type + material factory
│   ├── RevitLookup.cs               # Level lookups + EnsureLevels (+auto plan views)
│   ├── SuppressWarningsPreprocessor.cs  # IFailuresPreprocessor for the transaction
│   ├── UrlState.cs                  # Persists the last-used URL next to the DLL
│   └── Log.cs                       # Thread-safe append-only file logger
│
└── UI/
    └── UrlInputDialog.cs            # WPF URL-input dialog, parented to Revit
```

---

## Data contract

The plug-in expects a GET endpoint that returns:

```jsonc
{
  "userObjects": [
    {
      "floors":        [0, 0, 1, 0],       // per-floor index into `types`. Default [0].
      "polygonPoints": /* see below */,    // outer polygon of the object
      "types": [
        {
          "name":      "Generic",          // used as wall-type prefix
          "height":    3,                  // floor height in METERS
          "thickness": 0.2,                // optional wall thickness in METERS (default 0.2)
          "color":     "#a0c4ff",          // optional hex color for the wall's structural material
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
| `[[{x,y}, {x,y}, ...], ...]` | array of simple rings (one ring per polygon) |
| `[[[{x,y}, ...], [{x,y}, ...], ...], ...]` | array of `[outer, hole1, hole2, …]` polygons; only the outer ring is used |

### Units

- `polygonPoints.x`, `y` are in **meters** (matches the upstream editor, which computes perimeter in meters).
- `types[].height`, `types[].thickness` are in **meters**.
- The plug-in multiplies all inputs by 1000 to produce its internal millimetre representation and then converts to Revit internal units (feet) at wall-creation time.

### Why raw userObjects, not pre-processed walls

An earlier iteration had the API return `{ units, walls: [...] }` already expanded. That made unit bugs hard to trace (when the frontend labeled meters as `mm`) and meant every change to building logic required shipping an API. Moving the conversion into the plug-in:

- Gives the plug-in full knowledge of the source, so fallback and lookup decisions are driven by the real intent, not a lossy intermediate form.
- Keeps the API boundary minimal (one `project.userObjects` read).
- Means any plug-in update can evolve the mapping without a backend deploy.

---

## Import pipeline

The single source of truth is `Services/ImportRunner.Run(UIApplication, string url, ref string message)`.

```
┌─────────────────────────────────────────────────────────────────────────────┐
│ 1.  JsonFetcher.FetchAsync<UserObjectsPayload>(url)                          │
│      → HTTP GET, 30s timeout, deserialise (case-insensitive)                │
├─────────────────────────────────────────────────────────────────────────────┤
│ 2.  ProjectBuilder.BuildWalls(payload)                                      │
│      → iterate userObjects × floors                                         │
│      → normalise polygons, convert m → mm                                   │
│      → centre by bounding-box midpoint                                      │
│      → produce List<WallDto>                                                │
├─────────────────────────────────────────────────────────────────────────────┤
│ 3.  Transaction("Import Walls From URL")                                    │
│     │                                                                       │
│     │  SuppressWarningsPreprocessor   # auto-dismiss non-error warnings    │
│     │                                                                       │
│     │  WallBuilder.CreateWalls(doc, walls)                                  │
│     │   ├─ DeleteAllWalls                                                   │
│     │   ├─ RevitLookup.EnsureLevels      # auto-create L3..L10 + plan views │
│     │   ├─ new WallTypeProvider(doc)     # per-color wall-type factory     │
│     │   └─ for each WallDto:                                                │
│     │       ├─ typeProvider.Get(colorHex)                                   │
│     │       ├─ RevitLookup.ResolveLevel                                     │
│     │       └─ Wall.Create(line, typeId, levelId, heightFeet, …)            │
│     │                                                                       │
│     │  DisableActiveCrop(doc)                                               │
│     │                                                                       │
│     │  tx.Commit()                                                          │
├─────────────────────────────────────────────────────────────────────────────┤
│ 4.  ZoomOpenViewsToFit(uiDoc)                                               │
├─────────────────────────────────────────────────────────────────────────────┤
│ 5.  Summary dialog + log                                                    │
└─────────────────────────────────────────────────────────────────────────────┘
```

Notes:

- **HTTP happens before the transaction opens.** If the fetch is slow or fails, Revit state is untouched.
- **One transaction encapsulates deletion + level/view creation + wall creation + crop toggle.** If anything throws, the entire import rolls back — no half-imported building.
- **`SuppressWarningsPreprocessor`** is essential. Without it, a single overlap warning pops a modal dialog mid-import and hangs the whole command.
- **Zoom-to-fit is outside the transaction.** It's a UI-only operation on `UIView.ZoomToFit()`; no document change.

---

## Wall types, thickness, and color

`Services/WallTypeProvider` decides which `WallType` each imported wall uses.

- **Base type**: `Document.GetDefaultElementTypeId(ElementTypeGroup.WallType)` if it's a `WallKind.Basic`, else the first basic wall type in the document. This guarantees the walls look like walls drawn with the built-in tool.
- **No color requested** → the base wall type is used as-is. No duplication, no project mutation.
- **Color requested** (`type.color` is set) →
  1. Look for an existing wall type named `RWP #a0c4ff`. If present, reuse it.
  2. Otherwise, `baseType.Duplicate("RWP #a0c4ff")` to produce a new wall type. **Its compound structure is preserved** — the finishes, substrate, thermal layer, etc. carry over.
  3. Only the **structural** layer's material is swapped to a new `Material` named `RWP #a0c4ff` in that color.

Thickness is taken from the base wall type; per-wall thickness is not currently customised because the source data doesn't carry it per-type. The `WallDto.ThicknessMm` field is wired through for future use.

Cleanup: the RWP wall types and materials persist in the project after the import. On the next import, they are reused (matched by name) rather than duplicated again.

---

## Levels

`Services/RevitLookup.EnsureLevels(doc, names)` is called inside the transaction with every distinct level name referenced by the payload (e.g. `L1, L2, …, L10`).

For each name that doesn't match an existing level (case-insensitive):

1. Extract the first run of digits from the name (`"L3"` → `3`).
2. Set elevation to `(N − 1) × 3000 mm` (i.e. `L3` → 6 m).
3. For names with no digits, stack above the current highest level by 3 m.
4. `Level.Create(doc, elevationFeet)` and rename to the requested name.
5. Create a floor plan view (`ViewPlan.Create`) for the new level, so it appears under **Project Browser → Floor Plans** immediately.

Levels that already exist are reused, never moved. Levels that are no longer referenced are left alone — the plug-in's philosophy is "delete walls, keep organisational state".

---

## Viewport handling

Each import run, inside the transaction:

- If the active view is a `ViewPlan` with `CropBoxActive == true`, the crop box is switched off. The crop box clips elements outside it, so leaving it on with a fresh building makes walls vanish silently.

After commit, outside the transaction:

- `uiDoc.GetOpenUIViews()` → `v.ZoomToFit()` is called on every open view. The building fills whatever viewport is currently showing (floor plan, 3D, section, etc.).

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

The csproj references `RevitAPI` / `RevitAPIUI` via `HintPath` with `<Private>false</Private>` so the Revit-shipped copies load at runtime. Target framework is `net8.0-windows`, platform `x64`, `UseWPF=true`.

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

1. Verifies `C:\Program Files\Autodesk\Revit <year>\RevitAPI.dll` exists. Bails with a readable message if not.
2. Detects whether Revit is running; if so, `taskkill Revit.exe` and waits up to 10 s for it to exit before building (the installed DLL is locked while Revit is open).
3. `dotnet build -c Release -p:RevitVersion=<year>` (uses `-p:` form — `/p:` is rewritten by Git Bash MSYS path translation).
4. Copies `RevitWallsPlugin.dll` and `RevitWallsPlugin.addin` to `%AppData%\Autodesk\Revit\Addins\<year>\`.
5. Relaunches Revit if it was running when the script started.

Manual install: copy the two files from `bin\Release\` to `%AppData%\Autodesk\Revit\Addins\2025\`, then launch Revit. On first load Revit shows a security prompt for unsigned add-ins — click **Always Load**.

---

## Usage

1. Open any Revit project (a Metric Architectural template is a good default, but fallbacks handle almost anything).
2. **Add-Ins → External Tools → Import Walls From URL**.
3. Paste the API URL in the **Provide url** field, click OK.
4. After the import, check:
   - The summary dialog — tells you walls deleted/created, auto-created levels, auto-created wall types.
   - The Project Browser — **Floor Plans** should have `L1 … L<N>` for every floor.
5. To re-sync after editing the building upstream, run **Refresh Walls** — no dialog, one click, same URL.

---

## State and persistence

Two small files live next to the DLL (`%AppData%\Autodesk\Revit\Addins\2025\`):

| File | Purpose |
| --- | --- |
| `RevitWallsPlugin.lasturl.txt` | Last URL used. Loaded on startup of the URL dialog and by the Refresh command. |
| `RevitWallsPlugin.log` | Append-only run log. Never auto-truncated. |

Both are best-effort — `Services/UrlState` and `Services/Log` swallow I/O exceptions so logging / persistence failures can never crash the command.

---

## Logging

`Services/Log` appends lines like:

```
2026-04-21 18:55:12.318 [INFO ] ---- Import run invoked ----
2026-04-21 18:55:14.227 [INFO ] Fetching JSON from: https://staging.bimy.dev/api/…/export
2026-04-21 18:55:14.527 [INFO ] Fetched 3 userObject(s).
2026-04-21 18:55:14.551 [INFO ] Built 60 wall definition(s) from 3 userObject(s).
2026-04-21 18:55:14.551 [INFO ] Creating walls…
2026-04-21 18:55:15.173 [INFO ] Build pass finished. Created(in-memory)=60, Skipped=0. Committing…
2026-04-21 18:55:15.326 [INFO ] Transaction committed. Deleted=60, Created=60, Skipped=0.
2026-04-21 18:55:15.327 [INFO ] Auto-created levels: L3, L4, L5, L6, L7, L8, L9, L10
2026-04-21 18:55:15.327 [INFO ] Auto-created wall types: RWP #a0c4ff
2026-04-21 18:55:15.342 [INFO ] Disabled crop box on active view 'Level 1'.
2026-04-21 18:55:15.420 [INFO ] Zoomed open views to fit.
```

Tail live from PowerShell:

```powershell
Get-Content "$env:APPDATA\Autodesk\Revit\Addins\2025\RevitWallsPlugin.log" -Wait -Tail 50
```

---

## Troubleshooting

**Walls are created but invisible.**
Run `ZF` (Zoom to Fit) in the active view. Check that the active view is a level that was in the payload (e.g. `L1` plan view). In 3D views, walls always show.

**"No URL remembered yet" from Refresh Walls.**
Run Import Walls From URL at least once to seed `RevitWallsPlugin.lasturl.txt`.

**Import hangs mid-run.**
Usually a Revit modal warning that `SuppressWarningsPreprocessor` didn't catch. Check the log for a "Transaction failed" line. If not present, Revit might be showing a dialog behind another window — Alt+Tab to surface it.

**`ssh-add`-style permission / locked DLL errors during install.**
Revit must be closed while the DLL is overwritten. `install.sh` handles this, but manual copies don't — close Revit first.

**URL returns old `{ units, walls }` shape.**
The API hasn't been reloaded after the `exportProject.ts` change. Restart the API server; hit the URL in a browser to confirm it now returns `{ userObjects: [...] }`.

**MSBuild warning MSB3277 about version conflicts.**
Expected and benign. Every Revit assembly drags in the full Revit DLL graph and MSBuild notices the multi-version references.

---

## Extending the plug-in

Common additions, and where they'd hook in:

| Need | Change |
| --- | --- |
| Support a new JSON shape | Extend DTOs in `Models/ProjectDtos.cs` and parsing in `Services/ProjectBuilder.cs`. |
| Add doors/windows/slabs | New DTO + new `Services/Xxx Builder.cs`; wire from `WallBuilder.CreateWalls` or a new runner stage. |
| Per-wall thickness | Data already flows through `WallDto.ThicknessMm`; extend `WallTypeProvider.Get(color, thickness)` to key on both and pass thickness through to the compound structure's structural layer width. |
| Don't delete existing walls | Remove the `DeleteAllWalls(doc)` call in `WallBuilder.CreateWalls`. Consider adding a UI toggle. |
| Don't auto-create plan views | Remove the `ViewPlan.Create` call in `RevitLookup.EnsureLevels`. |
| A ribbon button instead of External Tools menu | Add an `IExternalApplication` with `OnStartup` building a `RibbonPanel` via `UIControlledApplication.CreateRibbonPanel`. Register it in the `.addin` with `<AddIn Type="Application">`. |

Each `Services/*.cs` file is scoped to one responsibility (fetching, building, creating, looking up, logging, persisting) so most extensions touch one or two files.
