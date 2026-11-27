# POE2Radar

An external, mostly read-only **map/radar overlay for Path of Exile 2**.

It attaches to the PoE2 client, reads game state directly out of process memory (no injection, no
hooks), and draws a terrain + entity overlay on top of the game's map — plus an optional auto-flask
quality-of-life feature.

> ⚠️ **Use at your own risk.** This reads another process's memory and can send keystrokes to the
> game. Automating input may violate Path of Exile's Terms of Service and could put your account at
> risk. This is a personal/educational tool — you are responsible for how you use it.

## About this fork

This repository is a fork of [Sikaka/POE2Radar](https://github.com/Sikaka/POE2Radar). All credit
for the original overlay goes to its authors; this fork keeps the upstream history and tracks it as
the `upstream` remote. Releases linked below are this fork's own builds (starting at v0.19.0) and
include the changes listed here; the in-app update check also points at this fork. Everything added
stays read-only: nothing in this fork writes to game memory.

- Built-in **Map Boss** marker: endgame maps mark their boss arena tile as soon as the map loads (toggle in the dashboard).

Fixes and additions so far:

- **Island rumour filter** — dashboard Atlas → Expeditions → Rumours: tick which Expedition island rumours show (labels and boat → island lines); unticked ones are hidden.
- **Rumour arrows + island-aware map rules** — the ➤ toggle on a rumour rings every unrevealed island with that rumour and points an off-screen arrow at it (off by default). Expedition islands are told apart from same-named normal maps (by the island rumour on their EndgameMaps row): plain map-name rules now match normal maps only; target islands with `Island:<name>` (Maps table rows "… (island)") or the `Expedition Island` kind.

- **Offsets for the 2026-11-18 patch** — recovered and re-validated (`Game/Poe2Offsets.cs`),
  including the atlas node's EndgameMaps row and its rumour field.
- **Fogged atlas nodes** — labels the map/content of nodes still hidden under the atlas fog
  (dashboard toggle *Fog labels*).
- **Expedition island rumours** — shows each ocean island's rumour (e.g. "Somethin' fishy",
  "Cold as ice") on the atlas before it is revealed (dashboard toggle *Island rumours*).
- **Expedition boat → islands** — from each usable Expedition logbook boat on the atlas, draws a
  teal line to every unrevealed island it would reveal (the boat's 16×16 grid block), labelled with
  the island's rumour (dashboard toggle *Boat → islands*; boats are also listed in `/api/atlas`).
  Off-screen atlas node positions are unreliable (the game briefly overwrites them), so links are
  only drawn while the boat and the island are on screen, and are hidden near the screen edges.
- **Player hideouts** — finds other players' public hideouts on the atlas (the "Player Hideout"
  nodes from the 0.5.5 supporter-pack beacon) by their reward: each matching node gets a magenta
  ring with an "Owner · Reward" label (e.g. "Kinto#2375 · Chaos Orb"), also while fogged, plus an
  off-screen arrow. Dashboard Atlas tab: toggle *Player hideouts*, a *Hideout reward filter*
  (comma-separated, case-insensitive, matched against reward, reward id and owner, e.g.
  `divine, exalted`; empty = all) and a live table of every player hideout (`/api/atlas-hideouts`,
  also `playerHideouts` in `/api/atlas`). The hideout's mechanic modifier is not read yet.
- **Atlas nav fix** — the atlas graph was read once and went stale as the atlas grew, so routes to
  fogged targets were missing. The graph is now rebuilt whenever the edge vector changes, and the
  new `Game/AtlasNav.cs` does a multi-source BFS from every accessible node to **every** matching
  target (e.g. all fogged *Matriarch Halls*, not just a completed one). Nav rules also match the
  map Id. `/api/atlas` now reports `graphVersion` and the nav plan.
- **Research probes** (`POE2Radar.Research`): `--atlas-fogdump`, `--ui-strings-dump` /
  `--ui-strings-diff`, `--atlas-nav-debug "<tag>"` (explains why a target is or isn't routed) and
  `--boat-dump` (Expedition logbook boats — region action buttons and the 16×16 grid block each one
  reveals) and `--player-hideouts` (owner/reward of every public player hideout node).
- **Toggle all display rules** — one *All rules enabled* switch on the dashboard Rules tab turns every
  display rule on or off at once (saved like any rule edit).
- **Navigation respects the display rules** — an entity or landmark the rules hide (rule disabled, Hide
  rule, junk) is no longer an auto-path / F6 / nav-menu target, and an existing route to it is dropped
  as soon as its rule is turned off (e.g. disable the Breach rule → no more path to the Breach).
- **Atlas "Nearest only"** — Atlas tab toggle that auto-routes only to the single nearest tracked map
  (fewest hops) instead of to every match. The routed target's ring and off-screen arrow are drawn
  larger with a white halo (arrow labelled "ROUTE: …"); the other Nav targets are dimmed.
- **Atlas sub-tabs** — the dashboard Atlas tab is split into *Maps*, *Expeditions* and *Player
  Hideouts* (the chosen sub-tab is remembered). The hideout table no longer jumps back to the top on
  its 3-second refresh (it only re-renders when the data changes and keeps scroll and focus).
- **Price unit** — *Value unit* (Auto / Exalted / Divine) in the dashboard Item Value → On Hover card,
  applied to every price chip (hover, loot tags, ground labels, ritual, runeforge). Auto keeps the old
  behaviour (div from 1 div up, else ex). **F11** in-game cycles the unit with a short on-screen notice
  (`hoverPrice.unitHotkey` in the config, 0 = off).
- **No duplicate Ritual value** — the hover price chip is hidden while hovering a Ritual tribute tile
  that already shows its own value chip (*Hide on Ritual tiles*, on by default).
- **Atlas fog radius** — *Fog radius (hops)* limits fog labels, island rumours and fogged content icons
  to fogged maps within N hops of a map you can run now (0 = every fogged map, the old behaviour).
- **Player hideout picker** — the *Player hideouts* toggle now fully controls hideout markers (ring,
  label, off-screen arrow and any route; a Nav/Arrow rule can't bring them back while it's off). The
  hideout table is only shown while the toggle is on and has a checkbox per hideout (plus all/none):
  unchecked hideouts get no ring, label, arrow or route. Picks are saved per owner; new hideouts start
  checked when they match the reward filter.
- **Always mark rares & bosses** — rare and unique monsters stay on the map even when no rule draws
  them (falling back to the Monster · Rare / Unique icon); uniques get a double boss ring, and a rare
  restyled by another rule (e.g. a mechanic rule) gets a ring. Settings → Radar Display toggle
  *Always mark rares & bosses* (on by default; a Hide rule still wins).

## Features

- **Map overlay** — when the in-game map is open, draws the walkable-terrain mask + entity dots,
  projected player-centered onto the game's map.
- **Entity radar** — alive enemies (red), NPCs, chests, area transitions, other players, and
  **POIs** (anything the game flags with a minimap icon) shown with a ring. Optional world-space
  **HP bars** over monsters.
- **Tile landmarks** — static features pulled from terrain tile data (boss arenas, area
  transitions, …), shown the moment you enter an area, with community-curated friendly names.
- **Atlas overlay** — on the open Atlas, highlights and labels nodes by content/map type, draws
  off-screen arrows to tracked maps, and auto-routes: shortest-hop route lines from where you are
  to every tracked tile, with hop counts. Track boss tiers (Deadly, Twinned, …), special maps
  (Citadels, Towers, Unique maps), and any content type; biome-coloured borders on tracked labels.
- **Loot values** — prices dropped items from poe.ninja and draws the value on the drop / its loot
  tag, including revealing what **unidentified uniques** are. Filter by category and value floor.
- **League reward values** — see what a reward is worth before you commit: value chips on every
  **Ritual** tribute-shop tile, a **Runeshape monolith's** best reward and rune count shown on the
  map *before* you open it, and prices on the **Runeforge** (Runeshape Combinations) panel.
- **Monster threat detection** — flags dangerous rare/magic monster mods and auras.
- **Navigation** — pick any landmark, POI, or entity as a destination and the overlay draws a
  smoothed A* route to it: on the in-game map when it's open, or as waypoints on the world ground
  when it's closed. Multi-select (each route its own color). Auto-nav patterns (e.g. the expedition
  encounter) re-acquire their target automatically in each new zone.
- **Customizable icons & display rules** — per-rule icon shape/color/size/opacity, editable live in
  the dashboard; drop your own `*.svg` into the `icons/` folder next to the exe to add or override
  any icon.
- **Auto-flask** (opt-in input) — presses the life/mana flask key below a Life, Energy Shield, or
  mana threshold (selectable). Hard-gated: only when PoE2 is the foreground window, with cooldowns
  and an **F8 kill-switch**.
- **Web dashboard** (`http://localhost:7777`, or **F12** in-game) — a local control panel: a
  searchable list of every entity/landmark you can click to navigate to, plus settings tabs (radar
  display + icon styling, monster HP bars, atlas tracking, loot-value pricing/league, monster-mod
  rules, auto-flask tuning). Served same-origin only; setting/navigation writes are loopback-gated.
  Read endpoints: `GET /state`, `/entities`, `/landmarks`, `/api/icons`.

## Download (no build required)

Grab the latest **`POE2Radar-vX.Y.Z-win-x64.zip`** from the
[Releases page](https://github.com/Polatto/POE2Radar/releases), unzip, and run `POE2Radar.Overlay.exe`
**as Administrator** (reading another process's memory requires it) with PoE2 already running.
The build is self-contained — no .NET install needed.

Notes:
- Windows SmartScreen may warn about an unsigned exe (expected for a community tool) — "More info →
  Run anyway".
- Antivirus may flag it because it reads game memory and (optionally) sends keystrokes; that's
  inherent to what the tool does.

## Build from source

Requires the **.NET 10 SDK**, Windows x64.

```
dotnet build POE2Radar.slnx
# launch with PoE2 already running and you in a zone:
src\POE2Radar.Overlay\bin\Debug\net10.0-windows\POE2Radar.Overlay.exe
```

Reading another process generally requires running the overlay **as Administrator**.

To **exit**: right-click the **POE2Radar system-tray icon → Exit**, or press **F9** (or close the
console window).

Hotkeys: **F8** toggles auto-flask; **F9** quits; **F12** opens the web dashboard; **F6** routes to
the nearest landmark/POI and **F7** clears routes; **F10** (with the Atlas open) inspects the
hovered tile and sets a route start/end. All other settings live in the dashboard (no calibration
hotkeys, to avoid accidental presses).

## Diagnostics / reporting a bug

Every session is logged automatically to `logs\radar-<date>-<time>.log` next to the exe (timestamped
console output plus crash stack traces; the last 10 sessions are kept, 20 MB cap each).

To report a problem, open the dashboard (**F12**) → **Diagnostics** tab:
1. While reproducing the issue, type what you see and press **Add note** (adds a marker to the log).
2. Press **Snapshot** at the problematic moment (saves the current state, atlas data, settings and
   attach health to `logs\snapshots\`).
3. Press **Export logs (.zip)** — the browser downloads `POE2Radar-diag-<version>-<time>.zip` (a copy is
   also saved in `exports\`). Send that zip to the developer.

The zip contains only the kept logs, snapshots, `config\radar_settings.json`, `config\display_rules.json`
and a `system-info.txt` (app/OS/.NET version, game process + window size) — no price or other caches.
Everything in it is **redacted**: your user-profile path becomes `%USERPROFILE%`, your Windows user name
`<user>`, the PC name `<machine>`, your character name `<character>`, and e-mail addresses `<email>`.

## Architecture

Three projects:

- `src/POE2Radar.Core` — memory plumbing (`OpenProcess` + `ReadProcessMemory`), the PoE2 offset
  table (`Game/Poe2Offsets.cs`), and the live read layer (`Game/Poe2Live.cs`).
- `src/POE2Radar.Overlay` — the radar `.exe`: attaches, AOB-resolves the game roots, runs the tick
  loop, renders the Direct2D overlay, serves the API, and (opt-in) drives auto-flask input.
- `src/POE2Radar.Research` — dev-time offset discovery/validation tools (AOB scan, HP value-scan,
  entity/tile/UI probes, an area-change watcher). Never linked into the overlay binary.

## Offsets & patches

PoE2 memory offsets drift with game patches. Validated offsets live in `Game/Poe2Offsets.cs`
(each marked `✓` when confirmed live). After a patch that breaks reads, use the `POE2Radar.Research`
probes to re-discover them. There is no live oracle for PoE2, so validation is value-scan / manual.

## Credits

Memory-layout research and the AOB approach draw heavily on the open-source **GameHelper2** project
(its `GameOffsets` were the starting reference for PoE2's struct shapes). GameHelper2 is not
redistributed here; only independently re-validated offset values are recorded in this repo.

## License

MIT — see [LICENSE](LICENSE).
