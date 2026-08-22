# BIMy for Revit

[![build](https://github.com/Bimy-ai/revit-plugin/actions/workflows/build.yml/badge.svg)](https://github.com/Bimy-ai/revit-plugin/actions/workflows/build.yml)
[![latest release](https://img.shields.io/github/v/release/Bimy-ai/revit-plugin?label=download&sort=semver)](https://github.com/Bimy-ai/revit-plugin/releases/latest)
[![license](https://img.shields.io/github/license/Bimy-ai/revit-plugin)](LICENSE)
[![Revit 2022–2030](https://img.shields.io/badge/Revit-2022%E2%80%932030-005f9e)](#requirements)

A Revit 2022–2030 add-in that pulls a building you designed in [BIMy](https://bimy.app) into Revit as a **native, editable Revit model** — walls, floors, ceilings, roofs, doors, windows, openings, rooms/spaces, materials and property sets, all as real Revit elements in their real Revit categories.

It adds a **BIMy** panel to the **Add-Ins** ribbon tab:

```
┌──────────────┬───────────────────┐
│              │  Set API token…   │
│  Load from   │  Disconnect       │
│    BIMy      │  Status & log     │
└──────────────┴───────────────────┘
```

> **Public beta.** The add-in works end to end and is in daily use, but the
> installer is not yet code-signed and the API surface it depends on may still
> move. Please [open an issue](https://github.com/Bimy-ai/revit-plugin/issues)
> for anything that surprises you.

---

## Download

**[⬇ Download BIMy-for-Revit-Setup.exe](https://github.com/Bimy-ai/revit-plugin/releases/latest/download/BIMy-for-Revit-Setup.exe)** — always the latest release.

Run it and click through; it takes a few seconds. There is no admin prompt: the
installer writes per-user by default and deploys into **every** Revit year it
finds on the machine at once. Then restart Revit and look for **BIMy** on the
**Add-Ins** tab. Every published version, with release notes, is on the
[releases page](https://github.com/Bimy-ai/revit-plugin/releases).

Two things to expect on a first install, both explained under
[Troubleshooting](#troubleshooting): Windows SmartScreen warns about the
unsigned Setup.exe, and Revit asks once whether to load an unsigned add-in.

To uninstall: **Start → BIMy for Revit → Uninstall**, or Add/Remove Programs.

### Requirements

| | |
| --- | --- |
| Revit | 2022 – 2030, 64-bit (the installer detects which years you have) |
| Windows | 10 or 11, x64 or ARM64 |
| .NET | None to install — Revit 2025+ already hosts the .NET 8 desktop runtime |
| Account | A [BIMy](https://bimy.app) account and an API token (**Settings → API tokens**) |

---

## Table of contents

- [Download](#download)
- [How it works](#how-it-works)
- [The round trip](#the-round-trip)
- [Commands](#commands)
- [Pulling a model](#pulling-a-model)
- [Project layout](#project-layout)
- [API contract](#api-contract)
- [Building and installing](#building-and-installing)
- [Releasing](#releasing)
- [State and persistence](#state-and-persistence)
- [Troubleshooting](#troubleshooting)
- [Extending](#extending)
- [Contributing](#contributing)
- [License](#license)

---

## How it works

**The app writes the building; Revit reads it.** There is deliberately no
per-element creation code in this add-in.

BIMy's own IFC generator already knows exactly what the building is — it is the same code path that powers the app's
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
├── BimyRevit.csproj             # net8.0-windows, x64, references the Revit API
├── BimyRevit.sln
├── dev-reinstall.ps1            # build → reinstall → relaunch Revit (see below)
├── build-installer.ps1          # build → package Setup.exe → upload to GCS
├── installer/
│   ├── BIMy.iss                 # Inno Setup script (multi-year deployment)
│   └── BIMy.addin.template      # shipped manifest (bundled BIMy\ layout)
├── .github/workflows/
│   ├── build.yml                # CI: build + package on every push and PR
│   └── release.yml              # v* tag → Setup.exe attached to a release
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

Both ends of this contract — the export route and the client-side IFC
generator that feeds it — live in the BIMy web app, which is a separate,
closed-source repository. This add-in only consumes the four calls above, so
nothing here needs that source to build or run.

---

## Building and installing

*If you only want to use the add-in, take the
[installer](#download) — none of this is needed.*

Prerequisites:

| | |
| --- | --- |
| [.NET SDK 8.x](https://dotnet.microsoft.com/download/dotnet/8.0) | Required. |
| A Revit install, 2022–2030 | Optional. Used for `RevitAPI.dll` when present; otherwise the build falls back to the [published reference assemblies on NuGet](https://www.nuget.org/packages/Revit_All_Main_Versions_API_x64), so the project compiles on a machine with no Revit at all — which is how CI builds it. |
| [Inno Setup 6](https://jrsoftware.org/isdl.php) | Only for building a `Setup.exe`. `winget install JRSoftware.InnoSetup`. |
| PowerShell 7+ | Only for `build-installer.ps1`, which signs the GCS upload JWT with `RSA.ImportFromPem` (.NET 5+, absent from Windows PowerShell 5.1). The script relaunches itself under `pwsh` if you start it from 5.1. |

The whole build is one command:

```powershell
git clone https://github.com/Bimy-ai/revit-plugin.git
cd revit-plugin
dotnet build -c Release                      # against the newest Revit you have
dotnet build -c Release -p:RevitVersion=2026 # or pin the API year
dotnet build -c Release -p:UseLocalRevitApi=false   # force the NuGet fallback
```

The output is a single `bin\Release\BimyRevit.dll`. Which API year you compile
against only decides the surface the compiler checks — the resulting add-in
loads in every Revit 2022–2030.

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

### Packaging a Setup.exe locally

```powershell
pwsh -File build-installer.ps1 -SkipUpload   # build + package
pwsh -File build-installer.ps1               # ...and upload to GCS
```

The result lands in `installer\Output\`. The upload step pushes the same
Setup.exe to BIMy's `bimy-common-assets` bucket and needs a service-account key
(`$GCP_KEY_FILE`, or `~\bimy\infra\gcp.json`); without one it says so and skips,
so contributors outside BIMy can still build and package.

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

## Releasing

Two GitHub Actions workflows, both on `windows-latest`, both building the exact
same way you would locally.

### `build.yml` — every push and pull request

Builds Release and compiles the installer, then attaches the `Setup.exe` to the
run as an artifact. Nothing is published; this is proof the tree still packages,
and it gives a reviewer a build of the PR they can actually install. The runner
has no Revit, so it passes `-p:UseLocalRevitApi=false` to make the NuGet
reference-assembly path explicit rather than incidental.

### `release.yml` — on a `v*` tag

```powershell
git tag v1.3.0
git push origin v1.3.0
```

That is the entire release process. The tag is the single source of truth for
the version: it is passed to Inno Setup as `/DAppVersion`, so the Setup.exe, its
Add/Remove Programs entry and the GitHub release name cannot drift apart. (The
literal `AppVersion` in `installer\BIMy.iss` is only a fallback for local
builds, wrapped in `#ifndef`.) The workflow rejects a tag that isn't a numeric
dotted version *before* building, because `VersionInfoVersion` would otherwise
fail at the very end of the compile.

Each release gets two copies of the same binary:

| Asset | Why |
| --- | --- |
| `BIMy-for-Revit-Setup-1.3.0.exe` | Archival — one per version, permanently addressable. |
| `BIMy-for-Revit-Setup.exe` | Stable name, so `/releases/latest/download/BIMy-for-Revit-Setup.exe` always resolves and the README download link never needs editing. |

Release notes are generated from the commits since the previous tag, so a
readable commit log is the release changelog. `workflow_dispatch` can also be
used to re-cut a version by hand from the Actions tab.

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

The **Status & log** button answers most of these — it shows who is connected,
to which environment, the add-in and Revit versions, and opens the log. That log
is the first thing to attach to a bug report.

**Windows SmartScreen says "Windows protected your PC" when I run Setup.exe.**
Expected: the installer isn't code-signed yet, and SmartScreen distrusts any
executable it hasn't seen often. **More info → Run anyway**. Code signing is on
the list for the 1.x release.

**No BIMy panel after installing.**
Restart Revit — the ribbon is built at startup, so an already-running Revit
won't pick up a fresh install. If it's still missing, confirm the manifest
landed: `%AppData%\Autodesk\Revit\Addins\<year>\BIMy.addin` should exist next to
a `BIMy\` folder. If the year folder isn't there at all, the installer didn't
detect that Revit — it probes for `C:\Program Files\Autodesk\Revit <year>\Revit.exe`,
so a non-default install location is the usual cause.

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
More than one `.addin` manifest in the same year folder points at a BIMy
assembly — typically a hand-copied one left over from an older build sitting
beside the installer's `BIMy.addin`. Delete the extra manifest from
`%AppData%\Autodesk\Revit\Addins\<year>\`, or uninstall and reinstall.

**MSBuild warning MSB3277 about version conflicts.**
Expected and benign — Revit's DLL graph drags in multi-version references.

**The installer says no Revit was detected, but Revit is installed.**
It looks for `Revit.exe` under `C:\Program Files\Autodesk\Revit <year>\` for
years 2022–2030. A Revit installed elsewhere, or a year outside that range, is
invisible to it. Open an issue with your install path.

---

## Extending

| Need | Where |
| --- | --- |
| A new element kind in Revit | **Not here.** Teach BIMy's IFC generator to write it; Revit's importer picks it up with no plug-in change at all. |
| Better Revit material names | Also upstream — the canonical → Revit template name map lives with the generator. |
| Import options (link vs open, intent, auto-join) | `Services/RevitIfcImporter.ConvertToRevit`. |
| Different save behaviour | `Services/TargetPath`. |
| More columns / filters in the picker | `UI/ProjectPickerDialog` + `Models/ProjectDtos`. |
| A new endpoint | `Models/BimyEnvironment` (URL) + `Services/BimyApi` (call). |

The rule that keeps this small: **the app decides what a building is; the
plug-in decides how it arrives.** Anything geometric belongs upstream.

---

## Contributing

Issues and pull requests are welcome — bug reports especially, since the
add-in's failure modes are mostly environmental (a Revit year we didn't
anticipate, an install path we don't probe, a model that trips the IFC
importer). A good report includes the Revit year, the add-in version and the
tail of `%LOCALAPPDATA%\BIMy\bimy.log`, all of which **Status & log** hands you.

A few conventions worth knowing before you send a patch:

- **Nothing geometric belongs in this repo.** See [Extending](#extending) — if
  the fix is "Revit should also receive X", the change is upstream in BIMy's
  IFC generator, and this add-in gets it for free.
- **CI must stay Revit-free.** Anything that only compiles against a real
  `RevitAPI.dll` on disk breaks the build for every contributor without Revit.
  Check with `dotnet build -c Release -p:UseLocalRevitApi=false`.
- **Don't touch the DPAPI entropy string or the legacy session file name** in
  `Services/Session.cs`. They are on-disk format, not identifiers; changing
  either silently signs every existing user out.
- The `.gitattributes` pins CRLF for `.ps1`/`.iss`/`.cmd` — these are launched
  by double-click as often as from a shell.

---

## License

[MIT](LICENSE). © 2026 BIMy.ai

"Revit" and "Autodesk" are trademarks of Autodesk, Inc. This add-in is not
affiliated with or endorsed by Autodesk.
