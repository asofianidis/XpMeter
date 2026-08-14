# Nevaris XP Meter (BepInEx 6 / IL2CPP)

Client-side base and job XP meter for SpiritVale with an in-game Mod Configuration Menu page. It reads the authoritative XP totals synchronized to the owning character and groups positive gains by nearby, locally relevant monster deaths. Quest, exploration, and awards that cannot be matched safely remain visible under a configurable **Other** row.

## Build

From this directory:

```powershell
dotnet build -c Release
```

The DLL is written to `bin/Release/net6.0/NevarisXpMeter.dll`.

The project uses `F:\SteamLibrary\steamapps\common\SpiritVale` by default. Override the game directory when needed:

```powershell
dotnet build -c Release -p:GameDir="D:\Games\SpiritVale"
```

## Install

1. Install the IL2CPP build of BepInEx 6 and launch SpiritVale once.
2. Install Mod Configuration Menu.
3. Copy only `NevarisXpMeter.dll` to `<game>\BepInEx\plugins\XpMeter\`.
4. Start the game. Press **F10** to toggle and **F11** to reset by default.
5. Drag the title bar to move the meter. Drag the bottom-right grip to resize it.

For local updates, build Release and run `Install-XpMeter.ps1`. Before replacement, it archives the installed DLL and current configuration under `<game>\BepInEx\mod-backups\XpMeter\`.

Configuration is generated at `<game>\BepInEx\config\alex.nevaris.xpmeter.cfg`, but normal configuration should be done through **ESC > Settings > MODS > XP Meter**.

## MCM options

- Start visibility and reset behavior across world/map changes
- Optional idle delay that starts a fresh XP session
- Monster-to-award matching window
- Grouping by localized monster name or distinct monster type ID
- Sorting by combined XP, base XP, job XP, kills, recency, or name
- Row limit and configurable label/visibility for unattributed XP
- Base XP, job XP, kill-count, share, and XP/hour columns
- Elapsed-session or between-awards rate clock
- Compact or full number formatting
- Font-aware source-name truncation that keeps long and localized names out of numeric columns
- Monster labels keep only the first-line name and omit the game's appended TMP sprite markup
- Multi-key toggle and reset shortcuts when supported by the installed MCM
- Live meter scale, session reset, and layout reset

All changes save immediately. Layout position and size are persisted automatically.

## Capture and attribution behavior

The meter polls the local player's synchronized `CharacterData.Exp` and `CharacterData.JobExp` totals from the same world-gated update used by Damage Meter. It also patches FishNet's generated client TargetRpc logic for `ExpCoinsChanged_T` when that managed path is available. Either route records actual total deltas and does not estimate rewards from monster level.

The XP payload contains no monster identity. To group combat XP, the mod correlates it with a client-observed monster death whose killing source resolves to the local player, a current party member, or one of their summons/clones. Matching is bidirectional within the configured window, which covers either network message order. Each death is consumed once. An award without a safe match remains under **Other** rather than being assigned speculatively.

If normal monster awards repeatedly appear under **Other**, increase **Monster match window** in MCM in small steps. A larger window can make attribution less precise when many unrelated events occur together.

The meter only creates UI and processes hotkeys after the owned character is network-spawned, active, and loaded. It destroys the canvas when the character leaves the world and hides automatically while menus, popups, loading screens, chat input, social panels, or context menus have focus.

All plugin-authored log messages begin with `[XpMeter]`.
