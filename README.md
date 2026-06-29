# Housing History

A Dalamud plugin that keeps a timestamped, **read-only** log of furnishings you
**place**, **remove**, **move**, and **rotate** inside your house — with
coordinates — so when you forget what you just changed (or move something and
want to put it back), you can check. Complementary to *Burning Down the House*;
it does not move or write anything, it only watches.

Open the log in-game with `/houselog`. Run `/houselog dump` to log a
diagnostics snapshot (handy after a patch).

## Features

- **Place / Remove / Move / Rotate / Dye** tracking, with coordinates (and dye
  names for recolors).
- **Click any coordinate to copy** `X Y Z` to the clipboard — the greyed "from"
  line of a move is the value you paste back into BDTH to undo it.
- **Search** box to filter by item name.
- **Persists across sessions** (stored in the plugin config dir), so yesterday's
  changes are still there.
- **Multi-house aware** — entries are tagged with the house they happened in,
  and the baseline reseeds when you move to a different house.
- **"Since last visit"** — on entering a house you've seen before, it diffs the
  current layout against the last-known one and logs what changed while you were
  away (tagged `(away)`). Can't say *who* changed it — the game exposes no editor
  info — but it catches placements, removals, moves, and dyes you missed.

## How it works

- A few times a second (throttled), on the framework thread, it snapshots the
  indoor furniture set via `HousingManager → GetFurnitureManager() →
  FurnitureMemory`, keyed by each object's stable `HousingFurniture.Index`. Per
  item it records id, position, rotation, and stain.
- `HousingFurnitureManager.LastUpdate` is checked first; if furniture hasn't
  changed since the last processed snapshot, the diff is skipped entirely.
- It diffs against the previous snapshot:
  - index appears → **Placed**; index gone → **Removed**
  - same index, position changed → **Moved**; rotation only → **Rotated**
  - same index, different furnishing id → slot reuse → Removed + Placed
- Dragging/turning fires many tiny updates; successive moves of the same object
  within 5s **coalesce** into one row (a Rotated that then moves upgrades to
  Moved), so you see the net change.
- On your **first ever visit** to a house the layout is seeded silently; on
  later visits it's diffed against the last-known layout (persisted to
  `layouts.json`) to surface away-changes, then live tracking continues.
- The read loop is wrapped so a struct mismatch after a patch fails soft (logs
  once, pauses) instead of crashing or spamming.

## Status / things to verify on first run

Compiles against **Dalamud.NET.Sdk 15.0.0** and current FFXIVClientStructs field
names. Confirm after the first build:

1. **`FurnitureMemory` accessor name** — generated accessor for the internal
   `_furnitureMemory` fixed array; if the build can't find it, check the name in
   your ClientStructs version.
2. **Name resolution** — `HousingFurniture.Id` is documented as `(0x20000 | Id)`
   indoors. We try the raw id against the `HousingFurniture` Excel sheet and fall
   back to `Furnishing #N`. Lots of `Furnishing #N`? Adjust the key in
   `NameResolver.cs`.
3. **`Index` stability** — move tracking assumes `HousingFurniture.Index` is a
   stable per-object id within a session. If moves log as Removed+Placed pairs,
   that assumption is wrong on your version.
4. **Rotation units** — shown as degrees assuming radians; fix `Format()` in
   `MainWindow.cs` if angles look wrong.
5. **Persistence** — `history.json` and `layouts.json` use `System.Text.Json`
   with `IncludeFields` (for `Vector3`) and numeric dictionary keys
   (`Dictionary<ulong, Dictionary<int, …>>`). If either doesn't round-trip on
   your runtime, switch to explicit DTOs / string keys.

`/houselog dump` prints all of this state to `/xllog` in one shot — use it first.

## Building

> ⚠️ Dalamud is a **Windows** toolchain. You need the .NET SDK **and** the
> Dalamud dev libraries (which ship with a XIVLauncher/Dalamud install).

### On Windows (easiest)
1. Install the .NET 9 SDK and XIVLauncher (run the game once with Dalamud so the
   dev libraries exist at `%AppData%\XIVLauncher\addon\Hooks\dev`).
2. `dotnet build` in this folder.
3. In-game: `/xlsettings` → Experimental → add the built `HousingHistory.dll`
   under "Dev Plugin Locations", then load it from `/xlplugins` (Dev Tools).

### On your Mac
No .NET SDK installed, and FFXIV+Dalamud only runs on Mac via **XIV-on-Mac**
(Wine). Edit here, build/test on a Windows PC or VM (recommended); or go all-Mac
with `brew install dotnet-sdk` + `DALAMUD_HOME` pointing into the Wine prefix.

## Maintenance — surviving Dalamud/game updates

You can't freeze a Dalamud plugin: each API bump requires a recompile or the
plugin is auto-disabled. The goal is cheap updates and fast detection.

- **CI early warning** — `.github/workflows/build.yml` builds against the latest
  Dalamud on every push and weekly. It tells you the moment an update breaks the
  build, before users do. (Point it at `stg/latest.zip` to track staging.)
- **Small struct surface** — all ClientStructs access lives in `HousingMonitor`;
  one file to fix when housing structs shift.
- **Fail soft** — the poll is wrapped in try/catch with a NaN guard, so a bad
  read pauses gracefully instead of crashing.
- **Diagnose fast** — `/houselog dump` verifies reads in-game after a patch.
- **Test on staging** — run against Dalamud's staging branch right after a game
  patch to catch runtime breakage early.
- **Watch** the XIVLauncher & Dalamud Discord `#dev` and FFXIVClientStructs commits.

## Roadmap (later)
- Per-house filter in the UI (house id is already stored)
- Per-item **undo** via BDTH-style position writing (leaves the read-only safe zone)
- Outdoor / ward support
