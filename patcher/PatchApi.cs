using System.Diagnostics;
using System.Security.Cryptography;
using Mono.Cecil;

/// result of one PatchApi.Patch call. Output is non-null only when Reason is "ok", which also
/// means the verifier ran on exactly these bytes and Failed is 0.
public sealed class PatchResult
{
    public byte[]? Output { get; init; }
    /// "ok" | "already_patched" | "not_moonlighter" | "verify_failed" | "error"
    public string Reason { get; init; } = "error";
    public string Log { get; init; } = "";
    public string VerifyLog { get; init; } = "";
    public int Passed { get; init; }
    public int Failed { get; init; }
    public string[] Failures { get; init; } = Array.Empty<string>();
    public long PatchMillis { get; init; }
    public long VerifyMillis { get; init; }
    public string? Sha256 { get; init; }
    public bool Ok => Reason == "ok";
}

/// bytes in, verified bytes out. No file or console i/o, so the CLI and the browser (wasm) share it.
public static class PatchApi
{
    public const string Version = "v1.0";
    private static readonly object Gate = new();

    public static PatchResult Patch(byte[] input, PatchOptions? opt = null)
    {
        opt ??= new PatchOptions();
        lock (Gate)
        {
            var log = new StringWriter();
            PatchResult Fail(string reason, string verifyLog = "", int passed = 0, int failed = 0, string[]? failures = null) =>
                new() { Reason = reason, Log = log.ToString(), VerifyLog = verifyLog, Passed = passed, Failed = failed, Failures = failures ?? Array.Empty<string>() };
            try
            {
                if (input == null || input.Length == 0) { log.WriteLine("[!] input is empty"); return Fail("error"); }
                log.WriteLine($"[+] patching assembly ({input.Length} bytes)");
                var clock = Stopwatch.StartNew();
                byte[] patched;
                AssemblyDefinition asm;
                try { asm = AssemblyDefinition.ReadAssembly(new MemoryStream(input), Rd.Params()); }
                catch (Exception ex) { log.WriteLine($"[!] input is not a readable .net assembly ({ex.GetType().Name}: {ex.Message})"); return Fail("not_moonlighter"); }
                using (asm)
                {
                    var gm = asm.MainModule.GetType("GameManager");
                    if (gm == null || asm.MainModule.GetType(MoonPatch.ChromaType) == null)
                    { log.WriteLine("[!] this is not Moonlighter's Assembly-CSharp.dll (no GameManager / UnityNativeChromaSDK type)"); return Fail("not_moonlighter"); }
                    bool anyCall = gm.Methods.Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
                        .Any(i => i.Operand is MethodReference mr && mr.DeclaringType.FullName == MoonPatch.ChromaType);
                    if (!anyCall) { log.WriteLine("[!] this assembly is already patched (GameManager has no Razer Chroma calls). patch the original Assembly-CSharp.dll"); return Fail("already_patched"); }
                    MoonPatch.Apply(asm, opt, log);
                    var ms = new MemoryStream();
                    asm.Write(ms);
                    patched = ms.ToArray();
                }
                long patchMs = clock.ElapsedMilliseconds;
                clock.Restart();
                var vlog = new StringWriter();
                var (passed, failed) = MoonVerify.Run(input, patched, opt, vlog);
                long verifyMs = clock.ElapsedMilliseconds;
                var failures = vlog.ToString().Split('\n').Select(l => l.TrimEnd()).Where(l => l.StartsWith("FAIL ")).Select(l => l.Substring(5)).ToArray();
                if (failed != 0) return new PatchResult { Reason = "verify_failed", Log = log.ToString(), VerifyLog = vlog.ToString(), Passed = passed, Failed = failed, Failures = failures, PatchMillis = patchMs, VerifyMillis = verifyMs };
                return new PatchResult
                {
                    Output = patched, Reason = "ok", Log = log.ToString(), VerifyLog = vlog.ToString(), Passed = passed, Failed = 0,
                    PatchMillis = patchMs, VerifyMillis = verifyMs, Sha256 = Convert.ToHexString(SHA256.HashData(patched)).ToLowerInvariant(),
                };
            }
            catch (Exception ex)
            {
                log.WriteLine($"[!] {ex.GetType().Name}: {ex.Message}");
                log.WriteLine(ex.StackTrace);
                return Fail("error");
            }
        }
    }
}
