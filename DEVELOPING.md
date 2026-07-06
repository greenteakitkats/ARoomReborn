# Developing Homekeeper

*(The repo is still named HousingHistory, only the in-game display name changed.)*

Maintainer/developer notes. Users don't need any of this, see the
[README](README.md).

## How it works

- A few times a second, on the framework thread, it snapshots the indoor furniture
  set via `HousingManager → GetFurnitureManager() → FurnitureMemory`, keyed by each
  object's stable `HousingFurniture.Index`. Per item it records id, position,
  rotation, and stain.
- `HousingFurnitureManager.LastUpdate` is checked first; if furniture hasn't changed
  since the last processed snapshot, the diff is skipped.
- `LayoutDiffer` (pure, no Dalamud deps) compares two snapshots and emits changes:
  - index appears → **Placed**; index gone → **Removed**
  - same index, position changed → **Moved**; rotation only → **Rotated**;
    stain only → **Redyed**
  - same index, different furnishing id → slot reuse → Removed + Placed
- Live drags/turns coalesce into one row within 5s. The read loop is wrapped so a
  struct mismatch after a patch fails soft (logs once, pauses) instead of crashing.
- On entering a house the furniture list streams in, so the baseline is only
  established once the item count has been stable for a couple of polls
  (`SettleReads`), otherwise the load-in reads as a flood of placements.
- The last-known layout is persisted per house (`layouts.json`); on returning to a
  known house it diffs against that to surface "since last visit" changes. History
  is persisted to `history.json`.

All ClientStructs / raw-memory access lives in `HousingMonitor`, one file to fix
when housing structs shift.

## Building

> ⚠️ Dalamud is a **Windows** toolchain. You need the .NET 9 SDK **and** the Dalamud
> dev libraries, which ship with a XIVLauncher/Dalamud install.

### Windows
1. Install the .NET 9 SDK and XIVLauncher (run the game once with Dalamud so the dev
   libraries exist at `%AppData%\XIVLauncher\addon\Hooks\dev`).
2. `dotnet build` in this folder.
3. In-game: `/xlsettings` → Experimental → add the built `HousingHistory.dll` under
   "Dev Plugin Locations", then load it from `/xlplugins` (Dev Tools).

### macOS / Linux
FFXIV + Dalamud only run via XIV-on-Mac (Wine). Edit anywhere; build/test on Windows
or set `DALAMUD_HOME` to the `dev` lib folder inside the Wine prefix. In practice CI
(below) is the build path.

### Logic tests
`tests/LogicTests` is a plain, Dalamud-free console that exercises `LayoutDiffer`. It
runs anywhere with the .NET SDK:
```
dotnet run --project tests/LogicTests/LogicTests.csproj
```

## CI / releasing

- `.github/workflows/build.yml`, builds against the latest Dalamud and runs the
  logic tests on every push, on PRs, and weekly (cron). The weekly run is the early
  warning: GitHub emails on failure when a Dalamud/ClientStructs update breaks it.
- `.github/workflows/release.yml`, on a `v*` tag, builds and publishes a GitHub
  Release with `latest.zip`.

The custom-repo manifest is **not** in this repo, it lives in
[greenteakitkats/DalamudPlugins](https://github.com/greenteakitkats/DalamudPlugins),
whose `update.yml` reads this plugin's latest release (version + `DalamudApiLevel`
from the manifest inside `latest.zip`) and regenerates its `repo.json`.

To ship a release:
```
# bump <Version> in HousingHistory.csproj, commit, then:
git tag v0.9.3
git push --tags
```
That workflow runs on a schedule; to refresh the custom repo immediately after a
release, trigger it: `gh workflow run "Update repo.json" -R greenteakitkats/DalamudPlugins`.
`DownloadLink*` always point at `releases/latest/download/latest.zip`.

## Surviving Dalamud/game updates

Plugins must be recompiled each API bump or they're auto-disabled, can't be made
update-proof. Strategy:

- **Small struct surface**, all raw access in `HousingMonitor`.
- **Fail soft**, try/catch + NaN guard in the poll.
- **CI early warning**, weekly build vs latest Dalamud.
- **Diagnose fast**, `/houselog dump` verifies reads in-game after a patch.
- **Recompile usually suffices**, ClientStructs ships bundled with Dalamud, so many
  patches need only a rebuild + re-release, no code change. Only renamed/changed
  members need edits (e.g. `TerritoryType` became `uint`).

## Things to verify in-game (first run after a build)

`/houselog dump` prints most of this to `/xllog` in one shot.

1. **`FurnitureMemory` accessor** resolves (generated accessor for `_furnitureMemory`).
2. **Names** appear instead of `Furnishing #N` (else adjust the id key in
   `NameResolver.cs`; `HousingFurniture.Id` is documented as `0x20000 | Id` indoors).
3. **`Index` stability**, a move shows as **Moved**, not Removed+Placed.
4. **Rotation units**, angles look right (shown as degrees assuming radians).
5. **Dye swatch colors** aren't channel-swapped (`Stain.Color` packing).
6. **Persistence**, `history.json` / `layouts.json` round-trip (System.Text.Json
   with `IncludeFields` and numeric dict keys).
7. **Auto-open** fires, keys off addon names `HousingGoods` / `HousingLayout`; if it
   doesn't open, those strings need adjusting in `Plugin.cs`.
8. **Apply mode (the only write path)**, `LayoutWorld.Instance()` + 0x40 =
   `HousingStructure` (Mode @0x0, ActiveItem @0x18, per BDTH); writes
   `ActiveItem->Transform.Translation`. Verify the applied position lands where the
   stored coordinate says (the furniture-array position and the layout-instance
   translation should share a coordinate space). Only writes in Rotate mode with an
   item selected; off by default.

## Roadmap

- Named layout snapshots + compare ("save a look, see what changed")
- Per-house filter in the UI (house id is already stored)
- Right-click row → link item in chat / copy
- Optional removal notification
- Click-to-highlight an item in the world
- Outdoor / ward support
