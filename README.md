# A Room Reborn

*(The project and repo are still named HousingHistory, but the plugin goes by
A Room Reborn in-game and in the docs.)*

A Room Reborn quietly keeps track of everything you do while decorating your
FFXIV house, indoors and out in the yard. Whenever you place, remove, move,
rotate, or dye a furnishing, it writes it down with a timestamp and the exact
coordinates, so you can look back at what you just did and put things right if
you change your mind. Think of it as the memory that goes with Burning Down
the House. BDTH handles the precise placement, and this one remembers how
everything used to be.

> By default it just watches and never touches your house.

## Features

- Logs every placement, removal, move, rotation, and dye change, each with a timestamp.
- Shows coordinates for everything. Click one to copy it, then paste it back into
  BDTH to undo a move.
- Apply mode (optional): click a coordinate and the item you have selected in
  layout mode jumps right to it.
- Undo button on every move, snaps the selected item straight back to where it
  was, position and facing both, no copying or pasting needed.
- Item icons and dye color swatches so the log is easy to skim.
- Search, filters for each kind of change, and a quick "today" summary.
- When you come back to a house, it shows you what changed while you were away.
- Tracks the yard too, tagged separately from indoor furnishings.
- Remembers your history between sessions.
- Opens itself when the housing menu shows up (you can turn this off).
- Keeps separate history for each of your houses.

## Install

1. In-game, open `/xlsettings`, go to **Experimental**, then **Custom Plugin Repositories**.
2. Add this URL and enable it:
   ```
   https://raw.githubusercontent.com/greenteakitkats/DalamudPlugins/main/repo.json
   ```
3. Open `/xlplugins`, search for **A Room Reborn**, and click **Install**.

> The repository manifest lives in
> [greenteakitkats/DalamudPlugins](https://github.com/greenteakitkats/DalamudPlugins),
> which is shared across all of these plugins and updates itself from each new release.

## Usage

- Type `/houselog` to open the log.
- Each row shows the time, the action, the item, and its coordinates (or the dye
  change for recolors).
- Click any coordinate to copy it. On a move, the greyed "from" line is the value
  you paste back into BDTH to put something back where it was.
- Use the checkboxes to choose which changes to show, and the search box to find a
  specific item.
- Turn on Apply mode if you'd rather click a coordinate and have the selected item
  jump straight there.
- On any move, select the item in layout mode and hit its Undo button to send it
  straight back, no need to copy coordinates at all.
- Type `/houselog dump` to print a quick diagnostics snapshot to `/xllog` (handy
  right after a game patch).

## Good to know

- Tracks furnishings both indoors and out in the yard, but not exterior fixtures
  (roof, walls, fence, door, chimney) yet, since those work differently under the hood.
- It can tell you what changed, but not who did it. The game doesn't share that
  with plugins.
- Out of the box it only watches and never changes anything, which keeps it in line
  with Dalamud's plugin rules. Apply mode and the Undo button are the one exception,
  and even then they only nudge the item you already have selected, exactly the way
  Burning Down the House does.

## Building and contributing

See [DEVELOPING.md](DEVELOPING.md).

## Credits

- Made and maintained by [@greenteakitkats](https://github.com/greenteakitkats).
- Furniture name lookup follows [ReMakePlace](https://github.com/RemakePlace/plugin).
- Built to work alongside [Burning Down the House](https://github.com/LeonBlade/BDTHPlugin).

## License

AGPL-3.0-or-later
