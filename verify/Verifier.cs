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
    /// leave out the dungeon door camera (reproduces the boot-only patch, 45e6f2ca...)
    public bool NoDoorCam;
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
        // Mono pitfall: a reference that binds to the patcher's own .NET builds fine and then fails at JIT on device.
        var hostLibs = new[] { "System.Private.CoreLib", "netstandard", "System.Runtime" };
        string[] Scopes(ModuleDefinition mod) => mod.GetTypeReferences().Select(t => t.Scope.Name).Concat(mod.AssemblyReferences.Select(r => r.Name)).Distinct().OrderBy(x => x).ToArray();
        var hostBad = Scopes(p.MainModule).Where(n => hostLibs.Contains(n) && !Scopes(o.MainModule).Contains(n)).ToArray();
        Check(hostBad.Length == 0, "no assembly or type reference resolves against the patcher's .NET (System.Private.CoreLib, netstandard, System.Runtime)" + (hostBad.Length > 0 ? ": " + string.Join(",", hostBad) : ""));
        Check(Scopes(p.MainModule).All(n => Scopes(o.MainModule).Contains(n)), "every type reference scope already existed in the original");
        Check(o.MainModule.Types.Count == p.MainModule.Types.Count, "type count unchanged");
        // methods the patch adds (DoorCam) are not in the original, so compare only the original's methods
        var origNames = new HashSet<string>(o.MainModule.GetTypes().SelectMany(t => t.Methods).Select(m => m.FullName));
        string Consts(ModuleDefinition mod) => string.Join(";", mod.GetTypes().SelectMany(t => t.Methods).Where(m => origNames.Contains(m.FullName)).SelectMany(m => m.Parameters.Select(q => q.HasConstant ? q.Constant?.GetType().Name + "=" + q.Constant : "-")));
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
        if (probe) { allowed.Add("CameraManager::Start"); allowed.Add("CameraManager::MoveCamera"); }
        bool door = !opt.NoDoorCam;
        // the door camera's names, spelled out here on purpose: the verifier does not trust the patcher's own constants
        const string dEnd = "End43", dFollow = "FollowRoom43", dMove = "MoveCameraRoom43", dPan = "Pan43", dOn = "room43";
        const string dMarker = "Disable DungeonLoadingPanel - FadeBoth - inCallback: ";
        var dArea = new[] { "AreaMinX", "AreaMaxX", "AreaMinY", "AreaMaxY" };
        var dHooked = new[] { "MoveTo", "SetArea", "SetFollowTarget", "StopFollowingTarget", "TeleportCameraToPoint" };
        var doorChanged = new[] { "CameraManager::MoveTo(", "CameraManager::SetArea(", "CameraManager::SetFollowTarget(", "CameraManager::StopFollowingTarget(",
            "CameraManager::TeleportCameraToPoint(UnityEngine.Vector3,", "DungeonManager::ChangeRoomTo(", "DungeonManager/<Start>d__", "DungeonLoadingPanel/<>c__DisplayClass" };
        if (door) allowed.AddRange(doorChanged);
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

        // ---- dungeon door camera
        var ocm = o.MainModule.GetType("CameraManager"); var pcm = p.MainModule.GetType("CameraManager");
        var newM = pcm.Methods.Where(m => !origNames.Contains(m.FullName)).Select(m => m.Name).OrderBy(x => x).ToArray();
        var newF = pcm.Fields.Select(f => f.Name).Except(ocm.Fields.Select(f => f.Name)).OrderBy(x => x).ToArray();
        bool othersSame = o.MainModule.GetTypes().Where(t => t.FullName != "CameraManager").All(t => { var q = p.MainModule.GetType(t.FullName); return q != null && q.Methods.Count == t.Methods.Count && q.Fields.Count == t.Fields.Count; });
        Check(othersSame, "no type other than CameraManager gains methods or fields");
        if (!door)
        {
            Check(newM.Length == 0 && newF.Length == 0, "[--no-doorcam] CameraManager has no new members");
            Check(!changed.Any(c => doorChanged.Any(d => c.Contains(d))), "[--no-doorcam] no door camera call site or hook");
        }
        else
        {
            // the method diff above left every body macro-simplified; put both sides back in canonical short form
            foreach (var t in new[] { o.MainModule, p.MainModule }.SelectMany(md => md.GetTypes()).Where(t => t.FullName.StartsWith("CameraManager") || t.FullName.StartsWith("DungeonManager") || t.FullName.StartsWith("DungeonLoadingPanel")))
                foreach (var m in t.Methods.Where(m => m.HasBody)) m.Body.OptimizeMacros();
            Check(newM.SequenceEqual(new[] { dEnd, dFollow, dMove, dPan }.OrderBy(x => x)), "door camera: CameraManager gains exactly End43, FollowRoom43, MoveCameraRoom43, Pan43 (" + string.Join(",", newM) + ")");
            Check(newF.SequenceEqual(dArea.Select(f => f + "43").Append(dOn).OrderBy(x => x)), "door camera: CameraManager gains exactly 5 private fields (" + string.Join(",", newF) + ")");
            Check(pcm.Fields.Where(f => newF.Contains(f.Name)).All(f => f.IsPrivate && !f.IsStatic && f.CustomAttributes.Count == 0), "door camera: new fields are private instance fields with no attributes (Unity does not serialize them)");
            foreach (var d in doorChanged) Check(changed.Count(c => c.Contains(d)) == (d.Contains("SetArea") ? 3 : 1), "door camera: changed exactly as planned: " + d);
            var end = pcm.Methods.Single(m => m.Name == dEnd);
            foreach (var m in pcm.Methods.Where(m => dHooked.Contains(m.Name) && !(m.Name == "TeleportCameraToPoint" && m.Parameters[0].ParameterType.Name != "Vector3")))
            {
                var il = m.Body.Instructions; int k = m.Name == "MoveTo" ? 2 : 0;
                bool ok = il[k].OpCode == OpCodes.Ldarg_0 && il[k + 1].OpCode == OpCodes.Call && il[k + 1].Operand == end
                    && (k == 0 || (il[0].OpCode == OpCodes.Ldarg_S && ((ParameterDefinition)il[0].Operand).Name == "_followingTarget" && il[1].OpCode.FlowControl == FlowControl.Cond_Branch && il[1].Operand == il[2 + 2]));
                // the rest of the body is the original, instruction for instruction
                var om = ocm.Methods.Single(x => x.FullName == m.FullName);
                string D(IEnumerable<Instruction> xs) => string.Join("|", xs.Select(i => i.OpCode + " " + (i.Operand is Instruction t ? "->" + t.Offset : i.Operand)));
                ok &= il.Skip(k + 2).Select(i => i.OpCode + " " + (i.Operand is Instruction ? "br" : i.Operand?.ToString())).SequenceEqual(om.Body.Instructions.Select(i => i.OpCode + " " + (i.Operand is Instruction ? "br" : i.Operand?.ToString())));
                Check(ok, $"door camera: {m.Name}({string.Join(",", m.Parameters.Select(q => q.ParameterType.Name))}) = End43 prefix + original body");
            }
            var move = pcm.Methods.Single(m => m.Name == dMove);
            var pdm = p.MainModule.GetType("DungeonManager");
            var ch = pdm.Methods.Single(m => m.Name == "ChangeRoomTo");
            var st = pdm.NestedTypes.Single(t => t.Name.StartsWith("<Start>d__")).Methods.Single(m => m.Name == "MoveNext");
            foreach (var (m, load) in new[] { (ch, OpCodes.Ldarg_2), (st, OpCodes.Ldnull) })
            {
                var il = m.Body.Instructions;
                int at = il.ToList().FindIndex(i => i.Operand == move);
                Check(at > 0 && il.Count(i => i.Operand == move) == 1 && il[at - 1].OpCode == load && !il.Any(i => i.Operand is MethodReference r && r.Name == "MoveCamera"), $"door camera: {m.DeclaringType.Name}.{m.Name} moves the camera through MoveCameraRoom43 once, no MoveCamera left");
            }
            var lp = p.MainModule.GetType("DungeonLoadingPanel").NestedTypes.SelectMany(t => t.Methods).Single(m => m.HasBody && m.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == dMarker));
            var fol = pcm.Methods.Single(m => m.Name == dFollow);
            var lpi = lp.Body.Instructions.ToList();
            Check(lpi.Count(i => i.Operand == fol) == 2 && lpi.Where(i => i.Operand == fol).All(i => { int x = lpi.IndexOf(i); return lpi[x - 5].Operand is MethodReference er && er.Name == "EnterRoom"; }), "door camera: loading callback starts the follow right after each of its 2 EnterRoom calls");
            // the shape of the new code: no exception handlers, no stray references, every branch lands inside its method
            foreach (var m in pcm.Methods.Where(m => newM.Contains(m.Name)).Concat(new[] { ch, st, lp }).Concat(pcm.Methods.Where(m => dHooked.Contains(m.Name))))
            {
                var il = m.Body.Instructions; var set = new HashSet<Instruction>(il);
                Check(il.All(i => i.Operand is not Instruction t || set.Contains(t)) && m.Body.ExceptionHandlers.All(h => set.Contains(h.TryStart) && set.Contains(h.HandlerStart)), $"door camera: branch/handler targets valid: {m.DeclaringType.Name}.{m.Name}");
            }
            var pan = pcm.Methods.Single(m => m.Name == dPan);
            Check(pan.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldc_R4 && (float)i.Operand == 16f / 9f) && pan.Body.Instructions.Any(i => i.OpCode == OpCodes.Isinst && ((TypeReference)i.Operand).FullName == "DungeonBossRoom"), "door camera: pan range is the 16:9 view, boss rooms excluded");
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
