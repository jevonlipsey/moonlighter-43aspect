# moonlighter-43aspect

Native 4:3 for *Moonlighter* (Digital Sun and 11 bit studios, Steam appid 606150), part of the nova-43aspect collection.

## Status
- See "Status (updated)" at the end. The engine facts below were confirmed (Unity 2019.2.7f2, Mono).

## Engine facts
- Unity, Mono scripting backend (`MonoBleedingEdge/`, `Moonlighter_Data/Managed/`). Version file `1.15.8.txt`; a `dlc/` folder.
- Did not boot on the Nova (black screen at 60+ fps); fixed, see Status.

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

## Status (updated)
- **Boot fixed and verified on the Nova.** Two causes, in order: (1) `GameManager.Awake` threw because the Razer plugin `UnityNativeChromaSDK.dll` needs `mfc140u.dll` under Proton, so all game init was skipped; the patch stubs every `UnityNativeChromaSDK` call in `GameManager`. (2) With no Steam client ("Start without Steam"), `SteamManager.Awake` calls `Application.Quit` when `SteamAPI.Init` fails.
- The **shipped patch** (`patcher <in> <out>`, v1.1, sha256 `c9c25d1d...`, 196 checks; signed off by the user 2026-11-06) is the Chroma stub plus the dungeon door camera: in normal rooms the camera follows the player in x only, clamped to the room, so the side walls and doors come into view (never wider than the 16:9 view). It leaves Steam, saves and achievements alone. `--no-doorcam` rebuilds the old boot-only v1.0 (`45e6f2ca...`).
- `--no-steam` (test only, never shipped) makes `SteamManager.Awake` return and rewires `Platform.Init` to the game's own DRM-free controllers (`DRMFreeManager`, `DRMFreeCloudSaveController`, `BaseAchievementController`, `DRMFreeStoreController`, `DRMFreeTestDLCController`). Saves then go to `LocalLow/11BitStudios/Moonlighter/` in the prefix, not Steam Cloud. `--probe` logs camera numbers (ortho, pixel size, aspect, room size in pixels) from `CameraManager.Start` and every `MoveCamera` room change.
- The game already fills 1280x960 on its own (`PixelBoy` derives its height from the window aspect).

## Commands
- `dotnet run --project patcher -c Release -- original/Assembly-CSharp.dll out/Assembly-CSharp.dll` (patch and verify in one go)
- `dotnet run --project verify -c Release -- original/Assembly-CSharp.dll out/Assembly-CSharp.dll [--no-steam] [--probe]`
- `tools/deploy.sh shipped|doors|doors-nosteam|doors-probe|shipped-v1|nosteam-v1|pristine` / `--rollback` (device deploy over root adb: lock, save backup, hash checks; `--dry-run` prints the plan). Variants are built into `out/<variant>/`.
- `bash tools/build_web.sh && node web/test/parity.mjs` (browser build, byte-identical to the CLI)
- `Player.log` lands at `<prefix>/drive_c/ml_player.log` when launched with `-logFile C:ml_player.log`.
