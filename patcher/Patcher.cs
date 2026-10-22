// Moonlighter boot patch, two parts:
// 1. Stubs every UnityNativeChromaSDK call in GameManager. Under Proton the Razer
//    plugin's dependency mfc140u.dll is missing, so the type initializer throws
//    inside GameManager.Awake and the rest of game init never runs.
// 2. TEST ONLY, behind --no-steam (never shipped): drops the Steam client dependency. "Start without Steam" has no Steam client, so
//    SteamManager.Awake calls Application.Quit when SteamAPI.Init fails. SteamManager.Awake
//    now returns at once and Platform.Init wires the game's own DRM-free controllers
//    (controller, saves, store, DLC) and the base achievement controller instead.
// usage: patcher <in Assembly-CSharp.dll> <out Assembly-CSharp.dll> [--no-steam] [--probe]
// --no-steam is for "Start without Steam" test runs: it redirects saves away from Steam Cloud and
// disables Steam achievements, so the default (shipped) patch leaves Steam alone.
// --probe is a diagnostic that logs camera numbers to Player.log (test only).
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

public static class MoonPatch
{
    public const string ChromaType = "ChromaSDK.UnityNativeChromaSDK";

    /// rewrites asm in place. throws when there is nothing to patch.
    public static int Apply(AssemblyDefinition asm, PatchOptions opt, TextWriter log)
    {
        bool noSteam = opt.NoSteam, probe = opt.Probe;
        var gm = asm.MainModule.GetType("GameManager") ?? throw new Exception("GameManager not found");
        int stubbed = 0;
        foreach (var m in gm.Methods.Where(m => m.HasBody))
        {
            var il = m.Body.Instructions;
            for (int i = 0; i < il.Count; i++)
            {
                var ins = il[i];
                if (ins.OpCode != OpCodes.Call || ins.Operand is not MethodReference mr || mr.DeclaringType.FullName != ChromaType) continue;
                // static call: pop each argument, then push a stand-in result if non-void.
                // Init() returns int, 0 = success, so report failure (1); everything else gets 0/false.
                var repl = new List<Instruction>();
                for (int p = 0; p < mr.Parameters.Count; p++) repl.Add(Instruction.Create(OpCodes.Pop));
                var rt = mr.ReturnType.FullName;
                if (rt == "System.Void") { }
                else if (rt == "System.Int32" || rt == "System.Boolean") repl.Add(Instruction.Create(OpCodes.Ldc_I4, mr.Name == "Init" ? 1 : 0));
                else throw new Exception($"unhandled return type {rt} in {mr.FullName}");
                if (repl.Count == 0) repl.Add(Instruction.Create(OpCodes.Nop));
                // keep branch targets/handlers pointing at the first replacement instruction
                ins.OpCode = repl[0].OpCode; ins.Operand = repl[0].Operand;
                for (int k = 1; k < repl.Count; k++) il.Insert(i + k, repl[k]);
                i += repl.Count - 1;
                stubbed++;
                log.WriteLine($"stubbed {mr.Name} in {m.Name}");
            }
            m.Body.OptimizeMacros();
        }
        if (stubbed == 0) throw new Exception("nothing patched");

        var mod = asm.MainModule;
        MethodDefinition Find(string type, string name) => mod.GetTypes().Single(t => t.FullName == type).Methods.Single(m => m.Name == name);
        TypeDefinition T(string n) => mod.GetTypes().Single(t => t.FullName == n);
        MethodReference Ctor(string n) => T(n).Methods.Single(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 0);

        if (noSteam)
        {
        var sm = Find("DigitalSunGames.Platforms.SteamManager", "Awake");
        sm.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Ret));
        log.WriteLine("SteamManager.Awake returns immediately");

        var init = Find("DigitalSunGames.Platforms.Platform", "Init");
        var ins2 = init.Body.Instructions;
        int edits = 0;
        for (int i = 0; i < ins2.Count; i++)
        {
            var x = ins2[i];
            if (x.OpCode == OpCodes.Call && x.Operand is GenericInstanceMethod gim && gim.Name == "GetOrAddComponent")
            {
                var arg = gim.GenericArguments[0].Name;
                if (arg == "SteamCloudSaveController")
                {   // [instance, ldloc.0] -> newobj DRMFreeCloudSaveController
                    ins2[i - 1].OpCode = OpCodes.Nop; ins2[i - 1].Operand = null;
                    x.OpCode = OpCodes.Newobj; x.Operand = Ctor("DigitalSunGames.Platforms.DRMFreeCloudSaveController"); edits++;
                }
                else if (arg == "CustomSteamAchievementController")
                {   // base controller never touches Steamworks
                    var g2 = new GenericInstanceMethod(gim.ElementMethod);
                    g2.GenericArguments.Add(T("DigitalSunGames.Platforms.BaseAchievementController"));
                    x.Operand = g2; edits++;
                }
            }
            else if (x.OpCode == OpCodes.Stfld && x.Operand is FieldReference f && f.Name == "m_Controller")
            {
                ins2[i - 1].OpCode = OpCodes.Newobj; ins2[i - 1].Operand = Ctor("DigitalSunGames.Platforms.DRMFreeManager"); edits++;
            }
            else if (x.OpCode == OpCodes.Newobj && x.Operand is MethodReference c)
            {
                var dt = c.DeclaringType.Name;
                if (dt == "SteamStoreController") { x.Operand = Ctor("DRMFreeStoreController"); edits++; }
                else if (dt == "SteamDLCController") { x.Operand = Ctor("DigitalSunGames.Platforms.DRMFreeTestDLCController"); edits++; }
            }
        }
        if (edits != 5) throw new Exception($"Platform.Init: expected 5 edits, made {edits}");
        log.WriteLine("Platform.Init rewired to DRM-free controllers");
        sm.Body.OptimizeMacros(); init.Body.OptimizeMacros();
        }

        if (probe)
        {
            // Log camera facts once per CameraManager.Start; refs are built from the game's own UnityEngine references.
            TypeReference U(string n) => mod.GetTypeReferences().First(t => t.FullName == n);
            MethodReference UM(string t, string n, TypeReference ret, bool hasThis) { var r = new MethodReference(n, ret, U(t)) { HasThis = hasThis }; return mod.ImportReference(r); }
            var logObj = (MethodReference)mod.GetMemberReferences().First(r => r.FullName == "System.Void UnityEngine.Debug::Log(System.Object)");
            var f32 = mod.TypeSystem.Single; var i32 = mod.TypeSystem.Int32;
            var cm = T("CameraManager"); var start = cm.Methods.Single(m => m.Name == "Start");
            var il = start.Body.Instructions; var first = il[0];
            var ilp = start.Body.GetILProcessor();
            void Emit(params Instruction[] xs) { foreach (var x in xs) ilp.InsertBefore(first, x); }
            void LogStr(string s) => Emit(Instruction.Create(OpCodes.Ldstr, s), Instruction.Create(OpCodes.Call, logObj));
            void LogVal(TypeReference t, params Instruction[] load) { Emit(load); Emit(Instruction.Create(OpCodes.Box, t), Instruction.Create(OpCodes.Call, logObj)); }
            var fCam = cm.Fields.Single(f => f.Name == "_camera"); var fGui = cm.Fields.Single(f => f.Name == "_GUICamera");
            LogStr("PROBE CameraManager.Start");
            foreach (var (fld, tag) in new[] { (fCam, "main"), (fGui, "gui") })
            {
                LogStr("PROBE cam " + tag + " ortho,pixW,pixH,aspect:");
                LogVal(f32, Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldfld, fld), Instruction.Create(OpCodes.Callvirt, UM("UnityEngine.Camera", "get_orthographicSize", f32, true)));
                LogVal(i32, Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldfld, fld), Instruction.Create(OpCodes.Callvirt, UM("UnityEngine.Camera", "get_pixelWidth", i32, true)));
                LogVal(i32, Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldfld, fld), Instruction.Create(OpCodes.Callvirt, UM("UnityEngine.Camera", "get_pixelHeight", i32, true)));
                LogVal(f32, Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldfld, fld), Instruction.Create(OpCodes.Callvirt, UM("UnityEngine.Camera", "get_aspect", f32, true)));
            }
            LogStr("PROBE Screen w,h:");
            LogVal(i32, Instruction.Create(OpCodes.Call, UM("UnityEngine.Screen", "get_width", i32, false)));
            LogVal(i32, Instruction.Create(OpCodes.Call, UM("UnityEngine.Screen", "get_height", i32, false)));
            start.Body.OptimizeMacros();
            log.WriteLine("probe added to CameraManager.Start");
        }

        return stubbed;
    }
}
