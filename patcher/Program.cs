// Moonlighter 4:3 patcher CLI. See Patcher.cs for what the patch does.
// usage: patcher <in Assembly-CSharp.dll> <out Assembly-CSharp.dll> [--no-steam] [--probe]
// --no-steam and --probe are test-only (see Patcher.cs); the default is the shipped patch.
var pos = args.Where(a => !a.StartsWith("--")).ToArray();
if (pos.Length != 2) { Console.Error.WriteLine("usage: patcher <in> <out> [--no-steam] [--probe]"); return 2; }
var opt = new PatchOptions { NoSteam = args.Contains("--no-steam"), Probe = args.Contains("--probe") };
var r = PatchApi.Patch(File.ReadAllBytes(pos[0]), opt);
Console.Write(r.Log); Console.Write(r.VerifyLog);
if (!r.Ok) { Console.Error.WriteLine($"FAILED: {r.Reason}"); return 1; }
File.WriteAllBytes(pos[1], r.Output!);
Console.WriteLine($"ok: {r.Passed} checks passed, sha256 {r.Sha256}");
return 0;
