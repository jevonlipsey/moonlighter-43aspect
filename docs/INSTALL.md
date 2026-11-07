# Installing the Moonlighter patch

1. Patch your own `Moonlighter_Data/Managed/Assembly-CSharp.dll` with the [web patcher](https://jevonlipsey.github.io/moonlighter-43aspect/) (or `dotnet run --project patcher -c Release -- <in> <out>`). Use the Windows build, as Steam installs it.
2. Rename the original to `Assembly-CSharp.dll.bak` and put the patched file in its place.
3. Launch the game through Steam as usual. To undo, put the `.bak` back (or verify the game files in Steam).

| platform | `Managed` folder |
|---|---|
| DroidDeck, SD card | `/mnt/droiddeck-sd/steamapps/common/Moonlighter/Moonlighter_Data/Managed/` |
| DroidDeck, internal storage | `/root/.local/share/Steam/steamapps/common/Moonlighter/Moonlighter_Data/Managed/` |
| Windows, Linux, Steam Deck | `<Steam library>/steamapps/common/Moonlighter/Moonlighter_Data/Managed/` |

## DroidDeck (Retroid Pocket Nova)

- Copy the file in with DroidDeck's desktop-mode file manager or terminal. The patcher page also works in the handheld's own browser; the downloaded file lands in Android's `Download` folder, which DroidDeck's Linux side sees as `/root/Storage/Download` while a session is running.
- Display settings (the settings cog): Resolution **Match screen (1280x960)**, 16:9 game fit **Stretch** (or off). In the game's Settings, choose **Fullscreen**.
- Without the patch, Moonlighter stops at a black screen under DroidDeck's Proton.

## What the patch changes

The patch does two things. It stubs the Razer Chroma calls in `GameManager`, which crash the game at boot under Proton. And in normal dungeon rooms the camera pans sideways with you, so the side walls and doors come into view at 4:3 without zooming out; it never shows more than the game shows at 16:9, and at 16:9 or wider nothing changes. It does not touch Steam, saves, Steam Cloud or achievements.
