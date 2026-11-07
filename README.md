# Moonlighter 4:3 Aspect Ratio Patch

**Moonlighter** on the **Retroid Pocket Nova** / DroidDeck (Proton) did not start at all; this patch fixes that, and the game then fills a 4:3 screen on its own. In dungeon rooms the camera pans sideways with the player, so the side walls and doors are visible at full scale. Aimed at the Nova (4.5", 1280x960) and other 4:3 displays. Part of the [Nova 4:3 collection](https://jevonlipsey.github.io/nova-43aspect/).

**Web patcher: https://jevonlipsey.github.io/moonlighter-43aspect/** (runs in your browser; your file is never uploaded).

The patcher ships only our own patch logic and modifies your own copy of the game. **No game code is redistributed.**

> Unofficial fan project, not affiliated with or endorsed by Digital Sun and 11 bit studios. Moonlighter and its files belong to them. The code here is MIT licensed ([LICENSE](LICENSE)).

## Install (players)

1. Open the [web patcher](https://jevonlipsey.github.io/moonlighter-43aspect/) and pick your `Moonlighter_Data/Managed/Assembly-CSharp.dll` (the Windows build, as Steam installs it).
2. Rename the original to `Assembly-CSharp.dll.bak` and put the patched file in its place.
3. Launch the game through Steam as usual. To undo, put the `.bak` back.

On DroidDeck the SD-card path is `/mnt/droiddeck-sd/steamapps/common/Moonlighter/Moonlighter_Data/Managed/`. Set DroidDeck's Resolution to **Match screen (1280x960)** and 16:9 game fit to **Stretch** (or off). Details in [docs/INSTALL.md](docs/INSTALL.md).

## What the patch changes (all in `Assembly-CSharp.dll`)

- **Boot fix.** `GameManager.Awake` threw under Proton because the Razer Chroma plugin (`UnityNativeChromaSDK.dll`) needs `mfc140u.dll`, so all game init was skipped and the game sat on a black screen. The patch stubs every `UnityNativeChromaSDK` call in `GameManager`.
- **Dungeon door camera.** The camera is fixed at the centre of each dungeon room, and at 4:3 the side walls and doors fall off the screen. The patch makes the camera follow the player sideways inside the room, using the game's own follow-and-clamp code (the one boss rooms and the town use). It never shows more than the game shows at 16:9, and at 16:9 or wider nothing changes. Boss rooms keep their own camera.
- **Not touched:** Steam, saves, Steam Cloud, achievements.

## Verification

The patcher writes nothing unless its verifier passes (196 checks): branch and handler targets of every changed method, assembly and type references unchanged from the original (so nothing binds to the patcher's own .NET and fails under Mono), Platform and Steam code untouched, no Chroma calls left in `GameManager`, and the door-camera call sites and pan range. The browser build and the CLI produce byte-identical output.

## Build from source

```bash
dotnet run --project patcher -c Release -- <in Assembly-CSharp.dll> <out Assembly-CSharp.dll>   # patch + verify
dotnet run --project verify  -c Release -- <original dll> <patched dll>                          # re-check independently
bash tools/build_web.sh && node web/test/parity.mjs                                              # browser build, parity with the CLI
```
