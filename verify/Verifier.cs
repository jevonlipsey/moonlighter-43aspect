// Verifier: a patched Assembly-CSharp.dll against the original it came from. Used by the CLI, the
// patcher (it gates its own output) and the browser build. Reads nothing from disk, resolves nothing.
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

public sealed class PatchOptions
{
    /// test only: drop the Steam client dependency (see Program.cs header)
    public bool NoSteam;
    /// test only: log camera numbers to Player.log
    public bool Probe;
}

public static class MoonVerify
{
    public static (int passed, int failed) Run(byte[] original, byte[] patched, PatchOptions opt, TextWriter w)
    {
        using var o = AssemblyDefinition.ReadAssembly(new MemoryStream(original), Rd.Params());
        using var p = AssemblyDefinition.ReadAssembly(new MemoryStream(patched), Rd.Params());
        int fail = 0, pass = 0;
        void Check(bool ok, string what) { w.WriteLine((ok ? "PASS " : "FAIL ") + what); if (ok) pass++; else fail++; }

        Check(o.MainModule.AssemblyReferences.Select(r => r.FullName).SequenceEqual(p.MainModule.AssemblyReferences.Select(r => r.FullName)), "assembly references unchanged (Mono-safe: nothing resolved against the patcher's .NET)");
        Check(o.MainModule.Types.Count == p.MainModule.Types.Count, "type count unchanged");
        string Consts(ModuleDefinition mod) => string.Join(";", mod.GetTypes().SelectMany(t => t.Methods).SelectMany(m => m.Parameters.Select(q => q.HasConstant ? q.Constant?.GetType().Name + "=" + q.Constant : "-")));
        Check(Consts(o.MainModule) == Consts(p.MainModule), "every parameter default value unchanged");

        var changed = new List<string>();
        foreach (var t in o.MainModule.GetTypes())
        {
            var pt = p.MainModule.GetType(t.FullName);
            if (pt == null) { changed.Add("missing " + t.FullName); continue; }
            for (int mi = 0; mi < t.Methods.Count; mi++)
            {
                var m = t.Methods[mi];
                var pm = mi < pt.Methods.Count && pt.Methods[mi].FullName == m.FullName ? pt.Methods[mi] : null;
                if (pm == null) { changed.Add("missing " + m.FullName); continue; }
                if (m.HasBody != pm.HasBody) { changed.Add(m.FullName); continue; }
                if (!m.HasBody) continue;
                string Dump(MethodDefinition d) { d.Body.SimplifyMacros(); return string.Join("|", d.Body.Instructions.Select(i => i.OpCode + " " + i.Operand)); }
                if (Dump(m) != Dump(pm)) { changed.Add(m.FullName); if (Environment.GetEnvironmentVariable("VDEBUG") != null && !m.FullName.Contains("GameManager")) { var a = Dump(m).Split('|'); var b = Dump(pm).Split('|'); for (int q = 0; q < Math.Min(a.Length, b.Length); q++) if (a[q] != b[q]) { w.WriteLine($"  [{q}] {a[q]}  =>  {b[q]}"); break; } } }
            }
        }
        w.WriteLine("changed methods: " + string.Join(", ", changed));
        bool noSteam = opt.NoSteam, probe = opt.Probe;
        var allowed = new List<string> { "GameManager::" };
        if (noSteam) { allowed.Add("Platform::Init"); allowed.Add("SteamManager::Awake"); }
        if (probe) allowed.Add("CameraManager::Start");
        Check(changed.Count > 0 && changed.All(c => allowed.Any(a => c.Contains(a))), "only allowed methods changed: " + string.Join(", ", allowed));

        MethodDefinition Get(string type, string name) => p.MainModule.GetTypes().Single(t => t.FullName == type).Methods.Single(m => m.Name == name);
        if (!noSteam)
        {
            // shipped patch: Steam, saves and achievements must be exactly as the game shipped them
            Check(!changed.Any(c => c.Contains("Platform::") || c.Contains("Steam")), "default patch leaves Platform and Steam code untouched");
        }
        else
        {
            var pinit = Get("DigitalSunGames.Platforms.Platform", "Init");
            var refs = string.Join("|", pinit.Body.Instructions.Select(i => i.Operand?.ToString() ?? ""));
            Check(!refs.Contains("SteamCloudSaveController") && !refs.Contains("CustomSteamAchievementController") && !refs.Contains("SteamStoreController") && !refs.Contains("SteamDLCController"), "[test build] Platform.Init no longer constructs Steam controllers");
            Check(refs.Contains("DRMFreeManager::.ctor") && refs.Contains("DRMFreeCloudSaveController::.ctor") && refs.Contains("DRMFreeStoreController::.ctor") && refs.Contains("DRMFreeTestDLCController::.ctor") && refs.Contains("BaseAchievementController"), "[test build] Platform.Init uses DRM-free controllers and base achievements");
            Check(Get("DigitalSunGames.Platforms.SteamManager", "Awake").Body.Instructions[0].OpCode == OpCodes.Ret, "[test build] SteamManager.Awake returns immediately");
            foreach (var m in new[] { pinit, Get("DigitalSunGames.Platforms.SteamManager", "Awake") })
            {
                var il2 = m.Body.Instructions; var set2 = new HashSet<Instruction>(il2);
                Check(il2.All(i => i.Operand is not Instruction t || set2.Contains(t)), $"branch targets valid: {m.DeclaringType.Name}.{m.Name}");
            }
        }

        var gm = p.MainModule.GetType("GameManager");
        int left = gm.Methods.Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
            .Count(i => i.Operand is MethodReference mr && mr.DeclaringType.FullName == "ChromaSDK.UnityNativeChromaSDK");
        Check(left == 0, "no UnityNativeChromaSDK calls remain in GameManager");
        foreach (var m in gm.Methods.Where(m => m.HasBody))
        {
            var il = m.Body.Instructions; var set = new HashSet<Instruction>(il);
            Check(il.All(i => i.Operand is not Instruction t || set.Contains(t)) && m.Body.ExceptionHandlers.All(h => (h.TryStart == null || set.Contains(h.TryStart)) && (h.HandlerStart == null || set.Contains(h.HandlerStart))), $"branch/handler targets valid: {m.Name}");
        }

        return (pass, fail);
    }
}

/// resolves nothing: anything that tries to bind against the host runtime fails loudly.
public sealed class NullAssemblyResolver : IAssemblyResolver
{
    public AssemblyDefinition Resolve(AssemblyNameReference name) => throw new AssemblyResolutionException(name);
    public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters) => throw new AssemblyResolutionException(name);
    public void Dispose() { }
}

/// Cecil resolves enum-typed parameter defaults (DOTween's Ease and friends) while writing a module, and the
/// browser has no DOTween.dll. This resolver answers any type it cannot find with an int32-backed enum so the
/// write goes through; the verifier checks that every parameter default survives unchanged.
public sealed class LenientMetadataResolver : MetadataResolver
{
    public LenientMetadataResolver(IAssemblyResolver r) : base(r) { }
    public override TypeDefinition? Resolve(TypeReference type)
    {
        try { var d = base.Resolve(type); if (d != null) return d; } catch (AssemblyResolutionException) { }
        var fake = new TypeDefinition(type.Namespace, type.Name, TypeAttributes.Public | TypeAttributes.Sealed, type.Module.TypeSystem.Object);
        fake.BaseType = new TypeReference("System", "Enum", type.Module, type.Module.TypeSystem.CoreLibrary);
        fake.Fields.Add(new FieldDefinition("value__", FieldAttributes.Public | FieldAttributes.SpecialName | FieldAttributes.RTSpecialName, type.Module.TypeSystem.Int32));
        return fake;
    }
}

public static class Rd
{
    public static ReaderParameters Params() { var r = new NullAssemblyResolver(); return new ReaderParameters { AssemblyResolver = r, MetadataResolver = new LenientMetadataResolver(r) }; }
}
