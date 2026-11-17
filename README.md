# DiskLens

A small local disk analyser and cleaner for Windows and macOS. A real desktop
app: one window, no server, no browser tab. The page in `ui.html` runs inside
the OS webview (WebView2 on Windows, WKWebView on macOS) and talks to the C#
scanner over the webview's message channel.

```powershell
winget install Microsoft.DotNet.SDK.8          # once (macOS: brew install dotnet-sdk)
dotnet publish app -c Release -r win-x64       # -> app/bin/Release/net8.0/win-x64/publish/DiskLens.exe
dotnet publish app -c Release -r osx-arm64     # -> .../osx-arm64/publish/DiskLens
dotnet run --project app -- --selftest         # verdicts, deny list, walk, rollup, dupes, routes
pwsh -File installer/build.ps1                 # Windows: DiskLens-Setup.exe with the app embedded
```

`DiskLens.exe --drive D:` (or `DiskLens --drive /Volumes/Ext`) starts on another
volume. Nothing leaves the machine. Deletions go to the Recycle Bin on Windows
and the Finder Trash on macOS, never a hard delete.

## Updates

On launch the app asks GitHub for the latest release of this repo, once, quietly.
If its tag is newer than the running version, a banner offers **Update now**:
the app downloads that release's `DiskLens-Setup.exe`, the installer waits for
the app to close, swaps the files and reopens it. Offline or rate-limited is
fine, nothing happens. Only an asset from this repo's own releases is ever run.

To ship a version: `pwsh -File release.ps1 -Version 2.2.0 -Notes "what changed"`.
That bumps the version in the csproj, installer and mac bundle, builds, commits,
tags, pushes and publishes the release with the installer attached.

## How it works

One pass over the drive builds an in-memory map of every directory's own bytes,
then rolls those up into totals. The treemap, the hotspot list and the cleaner
all read that one map, so everything after the walk is instant. Reparse points
are skipped, so junctions and symlinks are not counted twice.

The walk fans out across the root's branches (`Parallel.ForEach`, oversubscribed
because metadata reads are latency-bound, not CPU-bound) and builds into fresh
maps that are published by reference at the end — so a rescan never shows you a
half-built index, and readers keep serving the old one while it runs.

## The index is saved, so a rescan is the exception

After a walk the index is written to
`%LOCALAPPDATA%\DiskLens\<drive>.idx` as `(path, ownBytes)` pairs — about 56 MB
for 3.2M files. Child links and totals are *derived* on load, so there is no
second source of truth to drift. Measured on this machine:

| | Before | Now |
| --- | --- | --- |
| Start with a saved index | 96 s (full walk) | **3 s** |
| Cold walk, no index | 96 s | **48 s** |
| After deleting a folder | 96 s (full rescan) | **0.3 s** (index patched) |
| Re-read one folder | not possible | **~1–3 s** |

- **Delete** — the server removes the subtree from the index and settles every
  ancestor's total. No rescan, and the Clean list and stat blocks update at once.
- **Refresh** (inspector button) re-reads a single folder and splices the new
  numbers in. Use it when something changed outside the app.
- **Rescan** (toolbar) is the "I don't trust this any more" escape hatch.

Nothing auto-detects outside changes. A folder's mtime moves when entries are
added, removed or renamed but **not** when a file inside it merely grows, so an
mtime sweep would silently miss the commonest change. The honest options are an
explicit Refresh (what we do) or the NTFS USN journal, which needs an elevated
process. The header therefore always says how old the index is — "indexed 2m
ago" — rather than implying the numbers are live.

The headline file count belongs to the last full walk; a refresh deliberately
does not touch it. The folder count is derived from the index, so it stays exact.

Each folder gets a verdict:

| Verdict | Meaning |
| --- | --- |
| `CACHE` | Build or app cache. Deleting it costs one slow rebuild. |
| `MODELS` | Model weights. Re-downloadable, but slow. |
| `HEAVY` | Container or VM disks. Shrink from Docker/WSL/VirtualBox, don't delete. |
| `DATA` | Your own documents, pictures, videos. |
| `SYSTEM` | Windows or an installed program owns it. |
| `UNKNOWN` | Unrecognised — look inside before acting. |
| `FILES` | The loose files sitting directly in a folder, with no subfolder of their own. That row is how a 36 GB `pagefile.sys` stops being invisible. |

## Keep, review or drop

Every row carries a call next to its verdict, built from the only two honest
pieces of evidence on disk: what kind of folder it is, and when anything inside
it was last written (the walk records that per folder, rolled up as a max, and
it costs nothing because the walk already holds every `FileInfo`).

| Call | When |
| --- | --- |
| **KEEP** | Protected by the deny list, or your own documents/pictures/videos. |
| **REVIEW** | A cache or model still being written to, a VM disk, or anything the rules can't place. |
| **DROP** | A cache cold for 30+ days, a model untouched for 90+, or an unrecognised folder over 1 GB that nothing has written to in a year. |

The chip always states its reason — *"cache, untouched 2 months"*, *"unused
model, untouched 1.7 years"* — so it can be argued with rather than trusted.
Nothing is ever deleted on the strength of it: the Clean view simply sorts
drops first and offers **Select the N marked drop**, and everything still goes
to the Recycle Bin. The rules live in `advice()` in `ui.html` and are pinned by
tests in `check-ui.js`; the verdicts and deny list live in `app/Api.cs`.

Selection: tick rows one at a time, **shift-click** a tick to take a range,
or use *Select the N marked drop* / *Select every unprotected row* / *Clear*.

## Views

- **Map** — treemap of the current folder. Enter or double-click a tile to go
  deeper, Space selects it, breadcrumbs or Backspace come back up. The biggest
  60 folders get their own tile and the tail folds into one *others* tile.
  A folder with no subfolders shows its actual files as blocks, plus one block
  for the remainder, instead of a single useless "files here" slab.
- **Table** — the same numbers as a sortable table. A treemap on its own is a
  poor experience with a screen reader or without a mouse, so this is the
  equal-footing alternative, not an afterthought.
- **Hotspots** — the biggest folders anywhere on the drive, with containers
  whose children are already listed filtered out.
- **Clean** — only caches and model folders that passed the deny list.
- **Leftovers** — cold projects and what inside them regrows. Finds `node_modules`,
  `.venv`, `.next`, `.tox`, caches, and `target`/`dist`/`build`/`bin`/`obj` (only
  beside a real project marker such as `package.json` or a `.csproj`), 20 MB and up,
  grouped by project. Each is judged by the source *around* it, not its own files,
  so a build that touched `dist` yesterday does not make a dead project look alive.
  Throwaway-named folders (`test2`, `my-app`, `untitled`, `copy of …`) are listed as
  whole projects; the chip says whether `.git` exists, because without it the folder
  may be the only copy. The cross-mark hides a project for good (`ignore.txt` next to
  the index); *show again* restores them. Deletes show a running "this session" total.
- **Copies** — the same bytes kept in more than one place. Files are bucketed by
  size first (free), and only the collisions are read, so a whole drive takes a
  minute or two: 3.2M files seen, 462 actually hashed, 157 groups, 31 GB held by
  duplicates. The first path in each group is marked **keep**; tick the rest, or
  use *Select every copy but the first*, and they go to the Recycle Bin like
  anything else.

  The signature is the file's size plus a SHA-256 of its first and last 1 MB,
  not the whole file — fast enough to run over a drive, and nothing is removed
  without you reading the paths first.

## Themes

Ivory or dark, toggled from the header and remembered in `localStorage`; with
nothing stored it follows the OS. The light theme is deliberately ivory rather
than paper-white, because saturated blocks on #fff glare. Two colours change
with the theme and `check-ui.js` enforces why: acid lime sits at 1.16:1 against
ivory (invisible block edges), and the neutral DATA/UNKNOWN fills are near-white
in the dark theme, which would vanish on ivory. Every verdict pair is checked
for 4.5:1 text contrast *and* 1.25:1 separation from its own canvas, in both
themes.

## Drives

The drive picker in the header lists every fixed drive with its free space.
It is a custom menu, not a native `<select>` - the OS dropdown paints its own
white panel and blue highlight, which has nothing to do with the rest of the UI.
Each drive keeps its own saved index, so switching to one you have indexed
before is instant (2.7 s for C: here) and a new one is walked once (D:,
1.0M files, 5 s) and then remembered too.

The view and the current folder live in the URL (`#view=map&path=…`), so a
reload or the browser's back button keeps your place.

Keys: `1`–`4` switch view · `Backspace` up one folder · `Space` select a
tile · `r` rescan · `Esc` close the dialog or the detail sheet.

## Interface notes

Bento-grid maximalism: a dark canvas carrying saturated blocks, oversized
tabular numerals, uppercase micro-labels, and Space Grotesk 500–700 with a
system fallback when the font can't load. Stat blocks (free / used / indexed /
reclaimable) sit above a hero card that swaps between the four views, with the
inspector, keys and legend in the right rail.

The loudness is not allowed to cost anything: each verdict colour ships with
the text colour drawn on it and `check-ui.js` fails the build if any pair drops
under 4.5:1 (worst today is HEAVY at 5.15:1). 38px hit targets, visible focus
rings, SVG icons and no emoji, empty states that offer an action, motion only
on `transform`/`opacity`, and `prefers-reduced-motion` and `prefers-contrast`
honoured. Under 980px the rail stacks below the hero; under 620px the stat grid
goes two-up and the selection tray spans the width.

## Live index, restore, monthly nudge

- **Live index (Windows, opt-in).** System view > *Live index* reads the NTFS change
  journal every ~20 s and re-reads only the folders that changed, so a file that
  merely grew no longer leaves the index stale. Opening the volume needs
  administrator rights: the button restarts DiskLens elevated (one Windows prompt)
  and it resumes by itself next time. From an admin prompt,
  `DiskLens.exe --livetest` proves the journal feeds the index end to end.
  If the journal wrapped while the app was closed it says so and asks for one Rescan.
- **Restore.** Everything under *Removed by DiskLens* (System view) has a Restore
  button that puts it back from the Recycle Bin / Trash and patches the index.
  Only items DiskLens itself removed can be restored through it.
- **Free space.** After a clean the toast shows the drive's free space before and
  after, plus a running session total.
- **Monthly nudge.** At launch, at most once every 30 days, if 5 GB or more sits in
  cold projects' rebuildable folders a banner offers to open Leftovers. Nothing
  runs in the background; the app has to be open to notice.

## Safety

Deletions go to the **Recycle Bin**, never a hard delete, and always behind one
confirmation listing exactly what goes. The server refuses, regardless of what
the UI asks:

- any drive root or first-level folder (`C:\Windows`, `C:\ProgramData`, …)
- `C:\Program Files*`, `C:\ProgramData`, `C:\Windows` and everything under them,
  except `Windows\Temp`, `Windows\Prefetch` and `SoftwareDistribution\Download`
- a user profile root, `AppData` and its three roots
- the shell folders (Documents, Pictures, Videos, Music, Desktop, Downloads, …)
- anything classified `SYSTEM`

## Checks

```powershell
dotnet run --project DiskLens/app -- --selftest   # verdicts, deny list, walk, rollup, dupes, routes
node DiskLens/check.js                            # treemap layout
node DiskLens/check-ui.js                         # contrast and accessibility contract
```
