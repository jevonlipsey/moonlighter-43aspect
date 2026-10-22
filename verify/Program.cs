// usage: verify <original> <patched> [--no-steam] [--probe]
var pos = args.Where(a => !a.StartsWith("--")).ToArray();
if (pos.Length != 2) { Console.Error.WriteLine("usage: verify <original> <patched> [--no-steam] [--probe]"); return 2; }
var opt = new PatchOptions { NoSteam = args.Contains("--no-steam"), Probe = args.Contains("--probe") };
var (passed, failed) = MoonVerify.Run(File.ReadAllBytes(pos[0]), File.ReadAllBytes(pos[1]), opt, Console.Out);
Console.WriteLine(failed == 0 ? $"ALL PASS ({passed})" : $"{failed} FAILED");
return failed == 0 ? 0 : 1;
