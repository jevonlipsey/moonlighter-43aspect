# moonlighter-43aspect

Native 4:3 for *Moonlighter* (Digital Sun and 11 bit studios, Steam appid 606150), part of the nova-43aspect collection.

## Status
- **Planned.** Scaffold only, nothing verified yet. Engine facts below are first impressions from the install folder; confirm each one before relying on it.

## Engine facts (unverified)
- Unity, Mono scripting backend (`MonoBleedingEdge/`, `Moonlighter_Data/Managed/`). Version file `1.15.8.txt`; a `dlc/` folder.
- **Does not boot on the Nova:** black screen while running at 60+ fps.

## Plan
**Phase 1 is boot.** Nothing else matters until it reaches the title screen.

1. Find the Unity `Player.log` (prefix `drive_c/users/steamuser/AppData/LocalLow/<studio>/Moonlighter/`) after a failed launch and read it.
2. Common causes of a black screen at full fps under Proton: an intro video the game can't decode (Unity VideoPlayer / Media Foundation), the graphics API, or a missing first-run config. A patch that skips the intro is cheap if that's the cause.
3. Time-box it. If the cause is deep, write up what was found and park the game.
4. **Phase 2, 4:3:** pixel-art camera and the HUD, as a Cecil patch like chefrpg.

## Conventions
- Follow the nova-43aspect skill (device access, test workflow, repo conventions, web patcher design contract).
- Patch only the Windows build pulled from the device into `original/` (gitignored, read-only).
- Never commit game files, decompiler output, logs or screenshots.
