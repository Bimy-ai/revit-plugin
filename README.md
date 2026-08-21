# BIMy for Revit

A Revit 2022–2030 add-in that pulls a building you designed in [BIMy](https://bimy.app) into Revit as a **native, editable Revit model** — walls, floors, ceilings, roofs, doors, windows, openings, rooms/spaces, materials and property sets, all as real Revit elements in their real Revit categories.

It adds a **BIMy** panel to the **Add-Ins** ribbon tab:

```
┌──────────────┬───────────────────┐
│              │  Set API token…   │
│  Load from   │  Disconnect       │
│    BIMy      │  Status & log     │
└──────────────┴───────────────────┘
```

---

## Table of contents

- [How it works](#how-it-works)
- [The round trip](#the-round-trip)
- [Commands](#commands)
- [Pulling a model](#pulling-a-model)
- [Project layout](#project-layout)
- [API contract](#api-contract)
- [Building and installing](#building-and-installing)
- [State and persistence](#state-and-persistence)
- [Troubleshooting](#troubleshooting)
- [Extending](#extending)

---

## How it works

**The app writes the building; Revit reads it.** There is deliberately no
per-element creation code in this add-in.

BIMy's own IFC generator (`frontend/src/lib/ifc/ifcGenerate.js`) already knows
exactly what the building is — it is the same code path that powers the app's
IFC export, complete with element types, material layers, openings, derived
rooms and property sets. Revit's native IFC importer already knows exactly how
to turn every one of those into a first-class Revit element.

An earlier version of this plug-in sat between them: it fetched BIMy's raw
`userObjects` and rebuilt the geometry with `Wall.Create`, `Floor.Create`,
compound structures, level stacks and so on — around 2 400 lines of it. That is
a *second* authority for what a building is, and it drifted from the first with
every feature the app shipped: doors and windows never arrived, materials were
dropped, ceilings were approximations. Deleting it in favour of the IFC the app
already generates removed the drift permanently. Anything BIMy learns to draw
arrives in Revit with no plug-in release.

What this add-in owns is everything *around* that conversion, which is where a
pull actually goes wrong in practice:

- finding the right project without making you copy a 24-character id,
- not re-downloading and re-converting a model nobody republished,
- never silently overwriting a `.rvt` you have since edited in Revit,
- putting the result somewhere you can find it again,
- and saying plainly what came across.

---

## The round trip

```
   BIMy web app                    BIMy API                     Revit
 ┌──────────────┐          ┌──────────────────────┐      ┌──────────────────┐
 │ Export to    │  PUT     │ /api/export/         │      │ Load from BIMy   │
 │ Revit        ├─────────►│   revit-ifc/:project ├─────►│                  │
 │              │  IFC4    │                      │ GET  │  ├ download      │
 │ ifcGenerate  │  STEP    │  GridFS: revitExport │ IFC  │  ├ OpenIFCDocument│
 │ .js          │          │  one blob / project  │      │  ├ SaveAs .rvt   │
 └──────────────┘          └──────────────────────┘      │  └ open or link  │
                                                          └──────────────────┘
```

1. In BIMy: **Export to Revit** (command palette, or the Project menu). The
   client generates the IFC and `PUT`s it to the API, which stores one blob per
   project in a dedicated `revitExport` GridFS bucket — a published snapshot,
   last-write-wins.
2. In Revit: **Load from BIMy**, pick the project, and the add-in `GET`s those
   bytes, runs them through Revit's IFC importer with parametric intent, and
   saves the result as a native `.rvt`.

Re-exporting in BIMy and re-pulling in Revit is the update path. The pull is
conditional (`If-None-Match`), so pulling a project that hasn't been re-exported
costs one round trip and offers you the copy you already have.

---

## Commands

### Load from BIMy

Disabled until a session has been verified. Opens the **project picker**:

- your workspace's projects, by name and emoji, newest first;
- a **READY** / **NOT EXPORTED** badge per project, from the publish index, so
  you can see which models are actually pullable before clicking;
- when each was exported, and when this machine last pulled it;
- a search box, and **Open in BIMy ↗** for the selected project;
- a paste field for a project id or any BIMy URL, for projects the list can't
  cover;
- a choice of **Open as a new Revit project** or **Link into the open project**
  (the latter enabled only when a document is open).

### Set API token…

Environment dropdown + token field. Verifies against `GET {env}/api/auth` with
`Authorization: Bearer <token>`; on success the token is stored DPAPI-encrypted
for the current Windows account only. The environment is locked while a session
exists — disconnect to change it.

| Environment | Base URL |
| --- | --- |
| Production *(default)* | `https://bimy.app` |
| Sandbox | `https://sandbox.bimy.dev` |
| Staging | `https://staging.bimy.dev` |
| Demo | `https://demo.bimy.app` |

Generate tokens in BIMy under **Settings → API tokens**.

### Disconnect

Confirms, deletes the saved session, clears in-memory state. Load greys out.

### Status & log

Who is connected, to which environment, the add-in version, the Revit version,
and the log path — plus buttons to open the log file or the data folder. This is
the first thing to ask for when a user reports a problem.

---

## Pulling a model

1. **Add-Ins → BIMy → Set API token…** (once).
2. In the BIMy web app, open the project and run **Export to Revit**.
3. **Add-Ins → BIMy → Load from BIMy**, pick the project, click **Load**.

What happens then:

| Situation | What the add-in does |
| --- | --- |
| Model was republished since your last pull | Downloads it (with a progress window), converts, asks where to save if a file is already there. |
| Nothing has changed since your last pull | Offers to open the copy you already have, or re-import from scratch. |
| Project has never been exported to Revit | Explains that, with a link that opens the project in BIMy. |
| You already have a `.rvt` for this project | Asks: replace it, save alongside it as `Name (2).rvt`, or choose a location. |
| That `.rvt` is open in Revit right now | Doesn't try to replace it — saves alongside and says so. |
| You chose **Link** | Links the converted `.rvt` into the open document in one transaction. |

Models are saved to `Documents\BIMy Models\` by default, and re-pulls reuse
whatever location you picked last for that project.

---

## Project layout

```
revit-plugin/
├── RevitWallsPlugin.csproj      # net8.0-windows, x64, references Revit DLLs
├── RevitWallsPlugin.addin       # dev manifest (flat Assembly path)
├── dev-reinstall.ps1            # build → reinstall → relaunch Revit (see below)
├── build-installer.ps1          # build → package → upload Setup.exe to GCS
├── installer/
│   ├── BIMy.iss                 # Inno Setup script (multi-year deployment)
│   └── BIMy.addin.template      # shipped manifest (bundled BIMy\ layout)
│
├── Commands/
│   ├── BimyApplication.cs       # IExternalApplication: ribbon + session warm-up
│   ├── LoadFromBimyCommand.cs   # list projects → picker → importer
│   ├── LoadFromBimyAvailability.cs
│   ├── SetApiTokenCommand.cs    # dialog → BimyApi.VerifyAsync → SessionStore
│   ├── DisconnectCommand.cs
│   ├── DisconnectAvailability.cs
│   └── BimyStatusCommand.cs     # connection / version / log diagnostics
│
├── Models/
│   ├── BimyEnvironment.cs       # environments + every URL the add-in calls
│   ├── AuthDtos.cs              # BimyUser
│   └── ProjectDtos.cs           # BimyProject, BimyPublishedModel
│
├── Services/
│   ├── RevitIfcImporter.cs      # the pull: download → convert → open or link
│   ├── TargetPath.cs            # where the .rvt lands, without clobbering work
│   ├── PullCache.cs             # per-project ETag + local .rvt + last-pulled
│   ├── BimyApi.cs               # auth verify, project list, publish index
│   ├── JsonFetcher.cs           # HTTP: JSON fetch + conditional file download
│   ├── BimyFetchException.cs    # non-2xx with the server's own message
│   ├── BimyPaths.cs             # %LOCALAPPDATA%\BIMy — everything we write
│   ├── BimyId.cs                # project id out of whatever was pasted
│   ├── Session.cs               # SessionStore (DPAPI-encrypted)
│   ├── SessionState.cs          # in-process session + RefreshAsync()
│   └── Log.cs                   # thread-safe append-only file logger
│
└── UI/
    ├── BimyRibbon.cs            # panel: large Load button + stacked session items
    ├── ProjectPickerDialog.cs   # the project list, search, badges, mode
    ├── ProgressWindow.cs        # modal progress for the network phase
    └── SetApiTokenDialog.cs     # environment + token
```

---

## API contract

Everything the add-in calls, all with `Authorization: Bearer <token>`:

| Call | Purpose | Required? |
| --- | --- | --- |
| `GET /api/auth` | Verify the token, identify the account. | Yes |
| `GET /api/data?model=Project&sort=-_id&limit=200` | Fill the project picker. | No — falls back to the paste field |
| `GET /api/export/revit-ifc` | Publish index: `[{ projectId, name, updatedAt, size }]`. | No — falls back to no badges |
| `GET /api/export/revit-ifc/:projectId` | The published IFC (STEP bytes). | Yes |

The pull's response headers:

| Header | Use |
| --- | --- |
| `ETag` | Stored per project; replayed as `If-None-Match` so an unchanged model answers `304`. |
| `x-ifc-name` | Suggested file name — becomes the `.rvt` name when the picker didn't supply one. |
| `x-ifc-updated` | When the model was published. |

A `404` on the pull means "not exported to Revit yet" and is a normal state, not
an error — the add-in says so and offers to open the project in BIMy.

Server side lives in `api/src/export/routes/revitIfc.ts`; the publish side lives
in `frontend/src/lib/ifc/export.js` (`exportToRevit`) and
`frontend/src/api/revit.js`.

---

## Building and installing

Prerequisites: .NET SDK 8.x, a Revit install (2022–2030) for `RevitAPI.dll`, and
— for installer builds — [Inno Setup 6](https://jrsoftware.org/isdl.php)
(`winget install JRSoftware.InnoSetup`).

### Development loop

```powershell
# Build and copy straight into the Addins folder, then relaunch Revit. Seconds.
pwsh -File dev-reinstall.ps1 -Fast

# Build, compile Setup.exe, install it silently, relaunch Revit.
pwsh -File dev-reinstall.ps1

# Same, bumping AppVersion in installer\BIMy.iss first.
pwsh -File dev-reinstall.ps1 -Bump
```

`dev-reinstall.ps1` closes Revit first (it locks the DLL — this is the failure
that silently leaves you testing the previous build), asks before doing so
unless `-Force`, verifies what actually landed on disk, and relaunches Revit if
it was running. It never uploads anything.

Other flags: `-RevitVersion 2025`, `-AllUsers`, `-NoRestart`,
`-Configuration Debug` (with `-Fast`).

### Release

```powershell
pwsh -File build-installer.ps1        # build + package + upload to GCS
pwsh -File build-installer.ps1 -SkipUpload
```

Or build by hand:

```powershell
dotnet build -c Release -p:RevitVersion=2026
```

### What the installer does

Deploys the bundled layout Autodesk recommends, into **every** detected Revit
year at once:

```
%AppData%\Autodesk\Revit\Addins\<year>\BIMy.addin        <- manifest
%AppData%\Autodesk\Revit\Addins\<year>\BIMy\*.dll        <- payload
```

Per-user by default (no admin, no UAC); run it elevated and it also writes the
machine-wide `%ProgramData%` copy. Uninstall from Start → BIMy for Revit, or
Add/Remove Programs.

---

## State and persistence

Everything the add-in writes lives in **`%LOCALAPPDATA%\BIMy\`** — *not* next to
the DLL, because a machine-wide install puts the DLL somewhere the user cannot
write, and because a plug-in update replaces that folder (which used to sign
people out).

| Path | Purpose |
| --- | --- |
| `session.json` | Environment + DPAPI-encrypted API token (current Windows user only). Migrated automatically from the old next-to-the-DLL location. |
| `pulls.json` | Per environment + project: last ETag, publish time, the `.rvt` path, and when it was pulled. |
| `models\<projectId>\model.ifc` | The downloaded IFC. Scratch — safe to delete. |
| `bimy.log` | Append-only run log. Never auto-truncated. |

Pulled Revit files go to `Documents\BIMy Models\` unless you choose otherwise.

Tail the log:

```powershell
Get-Content "$env:LOCALAPPDATA\BIMy\bimy.log" -Wait -Tail 50
```

---

## Troubleshooting

**"Load from BIMy" is greyed out.**
No verified session. **Set API token…**, then check **Status & log**.

**"This project hasn't been exported to Revit yet".**
Nobody has run **Export to Revit** in the web app for that project. The dialog
offers a link straight to it.

**"Your BIMy session was rejected".**
The token is wrong, revoked, or belongs to a different environment. Generate a
fresh one in **Settings → API tokens** and re-connect, matching the environment
to the host you use in the browser.

**The project list is empty.**
The token's workspace has no projects, or `/api/data` refused it. Paste the
project id instead — the pull itself doesn't depend on the list.

**The picker shows no READY / NOT EXPORTED badges.**
The deployment predates `GET /api/export/revit-ifc`. Harmless; pulls still work.

**Revit asks about unsigned add-ins on first load.**
Expected until the installer is code-signed. Click **Always Load**.

**Two BIMy panels on the ribbon.**
Both a flat `RevitWallsPlugin.addin` (from the legacy `install.sh`) and the
installer's `BIMy.addin` are registered in the same year folder. Delete the flat
one, or uninstall and re-run `dev-reinstall.ps1`.

**MSBuild warning MSB3277 about version conflicts.**
Expected and benign — Revit's DLL graph drags in multi-version references.

---

## Extending

| Need | Where |
| --- | --- |
| A new element kind in Revit | **Not here.** Teach `frontend/src/lib/ifc/ifcGenerate.js` to write it; the importer picks it up with no plug-in change. |
| Better Revit material names | `frontend/src/lib/ifc/revitBridge.js` — the canonical → Revit template name map. |
| Import options (link vs open, intent, auto-join) | `Services/RevitIfcImporter.ConvertToRevit`. |
| Different save behaviour | `Services/TargetPath`. |
| More columns / filters in the picker | `UI/ProjectPickerDialog` + `Models/ProjectDtos`. |
| A new endpoint | `Models/BimyEnvironment` (URL) + `Services/BimyApi` (call). |

The rule that keeps this small: **the app decides what a building is; the
plug-in decides how it arrives.** Anything geometric belongs upstream.
