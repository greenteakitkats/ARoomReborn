# Housing History

A read-only Dalamud plugin that keeps a timestamped log of every change you make
to your FFXIV house — furnishings **placed, removed, moved, rotated, and dyed** —
with coordinates, so you can see what you just did and undo mistakes. A companion
to *Burning Down the House*: BDTH does precise placement, this remembers what you
changed.

> It only watches — it never edits your house.

## Features

- Logs **placements, removals, moves, rotations, and dye changes**, with timestamps
- **Coordinates** for everything — **click a coordinate to copy** `X Y Z` to your
  clipboard (paste it back into BDTH to undo a move)
- **Item icons** and **dye color swatches** so the log is easy to scan
- **Search**, per-action **filters**, and a **"today" summary**
- **"Since last visit"** — when you return to a house, see what changed while you
  were away
- **Remembers history across sessions**
- **Auto-opens** when the housing menu appears (toggleable)
- **Multi-house aware**

## Install

1. In-game, open `/xlsettings` → **Experimental** → **Custom Plugin Repositories**.
2. Add this URL and enable it:
   ```
   https://raw.githubusercontent.com/greenteakitkats/DalamudPlugins/main/repo.json
   ```
3. Open `/xlplugins`, search **Housing History**, and click **Install**.

> The custom-repo manifest lives in
> [greenteakitkats/DalamudPlugins](https://github.com/greenteakitkats/DalamudPlugins)
> (shared across all these plugins), which regenerates itself from each latest release.

## Usage

- `/houselog` — open the log.
- Each row shows **time · action · item · coordinates** (or the dye change for recolors).
- **Click any coordinate** to copy it to your clipboard. The greyed "from" line of a
  move is the value to paste back into BDTH to put something back.
- Use the checkboxes to filter which actions show, and the search box to find an item.
- `/houselog dump` — print a diagnostics snapshot to `/xllog` (handy after a game patch).

## Good to know

- **Indoor furnishings only** for now.
- It logs *what* changed, never *who* — the game doesn't expose who edited a house.
- It's read-only and gives no gameplay advantage, in line with Dalamud's plugin policy.

## Building / contributing

See [DEVELOPING.md](DEVELOPING.md).

## Credits

- Created and maintained by [@greenteakitkats](https://github.com/greenteakitkats).
- Furniture data resolution references [ReMakePlace](https://github.com/RemakePlace/plugin).
- Built to complement [Burning Down the House](https://github.com/LeonBlade/BDTHPlugin).

## License

AGPL-3.0-or-later
