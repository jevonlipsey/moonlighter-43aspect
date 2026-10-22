# Installing the Moonlighter patch

1. Patch your own `Moonlighter_Data/Managed/Assembly-CSharp.dll` with the web patcher (or `dotnet run --project patcher -- <in> <out>`).
2. Rename the original to `Assembly-CSharp.dll.bak` and put the patched file in its place.
3. Launch the game. To undo, put the `.bak` back.

On DroidDeck the SD-card path is `/mnt/droiddeck-sd/steamapps/common/Moonlighter/Moonlighter_Data/Managed/`.

The patch only stubs the Razer Chroma calls in `GameManager`. It does not touch Steam, saves, Steam Cloud or achievements.
