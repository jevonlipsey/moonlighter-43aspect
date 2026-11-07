// Moonlighter patch, three parts:
// 1. Stubs every UnityNativeChromaSDK call in GameManager. Under Proton the Razer
//    plugin's dependency mfc140u.dll is missing, so the type initializer throws
//    inside GameManager.Awake and the rest of game init never runs.
// 2. TEST ONLY, behind --no-steam (never shipped): drops the Steam client dependency. "Start without Steam" has no Steam client, so
//    SteamManager.Awake calls Application.Quit when SteamAPI.Init fails. SteamManager.Awake
//    now returns at once and Platform.Init wires the game's own DRM-free controllers
//    (controller, saves, store, DLC) and the base achievement controller instead.
// usage: patcher <in Assembly-CSharp.dll> <out Assembly-CSharp.dll> [--no-steam] [--probe] [--no-doorcam]
// --no-steam is for "Start without Steam" test runs: it redirects saves away from Steam Cloud and
// disables Steam achievements, so the default (shipped) patch leaves Steam alone.
// --probe is a diagnostic that logs camera numbers (ortho, pixel size, aspect, room size in pixels) to Player.log (test only).
// 3. Dungeon door camera (DoorCam below; on by default, --no-doorcam leaves it out and rebuilds the boot-only patch).
//    A normal dungeon room is framed by a fixed camera at its center and fills the screen height, so at 4:3 its side
//    walls and doors fall outside the view. The camera now pans sideways inside the room and follows the player, the
//    way boss rooms and the town already do, at the same scale. The pan never goes past what 16:9 shows.
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

        if (!opt.NoDoorCam) DoorCam.Apply(asm.MainModule, log);

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
            var cm = T("CameraManager");
            var fCam = cm.Fields.Single(f => f.Name == "_camera"); var fGui = cm.Fields.Single(f => f.Name == "_GUICamera");
            // the same block goes at the top of CameraManager.Start (scene load) and of MoveCamera(Vector2,..) (every room change),
            // so one run covers town and dungeon even if the camera object outlives a scene.
            void Probe(MethodDefinition target, string label)
            {
                var ilp = target.Body.GetILProcessor(); var first = target.Body.Instructions[0];
                void Emit(params Instruction[] xs) { foreach (var x in xs) ilp.InsertBefore(first, x); }
                void LogStr(string s) => Emit(Instruction.Create(OpCodes.Ldstr, s), Instruction.Create(OpCodes.Call, logObj));
                void LogVal(TypeReference t, params Instruction[] load) { Emit(load); Emit(Instruction.Create(OpCodes.Box, t), Instruction.Create(OpCodes.Call, logObj)); }
                Instruction C(FieldDefinition fld) => Instruction.Create(OpCodes.Ldfld, fld);
                LogStr("PROBE " + label);
                foreach (var (fld, tag) in new[] { (fCam, "main"), (fGui, "gui") })
                {
                    var skip = Instruction.Create(OpCodes.Nop); // field unset in this scene: log nothing, never throw
                    Emit(Instruction.Create(OpCodes.Ldarg_0), C(fld), Instruction.Create(OpCodes.Brfalse, skip));
                    LogStr("PROBE cam " + tag + " ortho,pixW,pixH,aspect:");
                    LogVal(f32, Instruction.Create(OpCodes.Ldarg_0), C(fld), Instruction.Create(OpCodes.Callvirt, UM("UnityEngine.Camera", "get_orthographicSize", f32, true)));
                    LogVal(i32, Instruction.Create(OpCodes.Ldarg_0), C(fld), Instruction.Create(OpCodes.Callvirt, UM("UnityEngine.Camera", "get_pixelWidth", i32, true)));
                    LogVal(i32, Instruction.Create(OpCodes.Ldarg_0), C(fld), Instruction.Create(OpCodes.Callvirt, UM("UnityEngine.Camera", "get_pixelHeight", i32, true)));
                    LogVal(f32, Instruction.Create(OpCodes.Ldarg_0), C(fld), Instruction.Create(OpCodes.Callvirt, UM("UnityEngine.Camera", "get_aspect", f32, true)));
                    if (tag == "main")
                    {
                        // pixels per world unit = pixelHeight / (2 * ortho); a dungeon room is 480 x 264 units (DungeonRoom gridX/gridY)
                        void Ppu() => Emit(Instruction.Create(OpCodes.Ldarg_0), C(fld), Instruction.Create(OpCodes.Callvirt, UM("UnityEngine.Camera", "get_pixelHeight", i32, true)), Instruction.Create(OpCodes.Conv_R4),
                            Instruction.Create(OpCodes.Ldc_R4, 2f), Instruction.Create(OpCodes.Ldarg_0), C(fld), Instruction.Create(OpCodes.Callvirt, UM("UnityEngine.Camera", "get_orthographicSize", f32, true)), Instruction.Create(OpCodes.Mul), Instruction.Create(OpCodes.Div));
                        LogStr("PROBE main px-per-unit, room(480x264) px W, room px H:");
                        Ppu(); Emit(Instruction.Create(OpCodes.Box, f32), Instruction.Create(OpCodes.Call, logObj));
                        Ppu(); Emit(Instruction.Create(OpCodes.Ldc_R4, 480f), Instruction.Create(OpCodes.Mul), Instruction.Create(OpCodes.Box, f32), Instruction.Create(OpCodes.Call, logObj));
                        Ppu(); Emit(Instruction.Create(OpCodes.Ldc_R4, 264f), Instruction.Create(OpCodes.Mul), Instruction.Create(OpCodes.Box, f32), Instruction.Create(OpCodes.Call, logObj));
                    }
                    Emit(skip);
                }
                LogStr("PROBE Screen w,h:");
                LogVal(i32, Instruction.Create(OpCodes.Call, UM("UnityEngine.Screen", "get_width", i32, false)));
                LogVal(i32, Instruction.Create(OpCodes.Call, UM("UnityEngine.Screen", "get_height", i32, false)));
                target.Body.OptimizeMacros();
            }
            Probe(cm.Methods.Single(m => m.Name == "Start"), "CameraManager.Start");
            Probe(cm.Methods.Single(m => m.Name == "MoveCamera" && m.Parameters[0].ParameterType.Name == "Vector2"), "CameraManager.MoveCamera (room change)");
            log.WriteLine("probe added to CameraManager.Start and MoveCamera(Vector2)");
        }

        return stubbed;
    }
}

/// Part 3: the dungeon door camera. Everything new lives on CameraManager (4 methods, 5 private fields); the game's
/// own code only gains prefix calls and two swapped call sites. All references used already exist in the assembly.
public static class DoorCam
{
    public const string Follow = "FollowRoom43", Move = "MoveCameraRoom43", Pan = "Pan43", End = "End43", OnField = "room43";
    public static readonly string[] AreaFields = { "AreaMinX", "AreaMaxX", "AreaMinY", "AreaMaxY" };
    // methods that hand the camera to someone else: each first restores the area the game had before the room follow
    public static readonly string[] Hooked = { "MoveTo", "SetArea", "SetFollowTarget", "StopFollowingTarget", "TeleportCameraToPoint" };
    public const string LoadingMarker = "Disable DungeonLoadingPanel - FadeBoth - inCallback: ";

    public static void Apply(ModuleDefinition mod, TextWriter log)
    {
        TypeDefinition T(string n) => mod.GetTypes().Single(t => t.FullName == n);
        MemberReference R(string full) => mod.GetMemberReferences().FirstOrDefault(r => r.FullName == full) ?? throw new Exception("reference not found: " + full);
        MethodReference M(string full) => (MethodReference)R(full);
        FieldReference F(string full) => (FieldReference)R(full);

        var cm = T("CameraManager"); var dm = T("DungeonManager"); var boss = T("DungeonBossRoom"); var door = T("DungeonDoor");
        if (cm.Methods.Any(m => m.Name == Follow)) throw new Exception("door camera already present");
        FieldDefinition CF(string n) => cm.Fields.Single(f => f.Name == n);
        var area = AreaFields.Select(CF).ToArray();
        var following = CF("followingTarget");
        MethodDefinition CM(string n) => cm.Methods.Single(m => m.Name == n);
        var getVert = CM("get_vertExtent"); var getHoriz = CM("get_horizExent"); var setFollow = CM("SetFollowTarget");
        var moveT = cm.Methods.Single(m => m.Name == "MoveCamera" && m.Parameters[0].ParameterType.Name == "Transform");
        var moveV = cm.Methods.Single(m => m.Name == "MoveCamera" && m.Parameters[0].ParameterType.Name == "Vector2");
        var cmInst = CM("get_Instance");
        var dmInst = dm.Methods.Single(m => m.Name == "get_Instance");
        var curRoom = dm.Fields.Single(f => f.Name == "currentDungeonRoom");
        var roomT = curRoom.FieldType;
        var destRoom = door.Fields.Single(f => f.Name == "destinationRoom"); var enterPt = door.Fields.Single(f => f.Name == "playerEnterDestinyRoomPoint");

        var objOk = M("System.Boolean UnityEngine.Object::op_Implicit(UnityEngine.Object)");
        var objNeq = M("System.Boolean UnityEngine.Object::op_Inequality(UnityEngine.Object,UnityEngine.Object)");
        var getTransform = M("UnityEngine.Transform UnityEngine.Component::get_transform()");
        var getPos = M("UnityEngine.Vector3 UnityEngine.Transform::get_position()");
        var vx = F("System.Single UnityEngine.Vector3::x"); var vy = F("System.Single UnityEngine.Vector3::y");
        var toV2 = M("UnityEngine.Vector2 UnityEngine.Vector2::op_Implicit(UnityEngine.Vector3)");
        var clampF = M("System.Single UnityEngine.Mathf::Clamp(System.Single,System.Single,System.Single)");
        var combine = M("System.Delegate System.Delegate::Combine(System.Delegate,System.Delegate)");
        var actionCtor = M("System.Void System.Action::.ctor(System.Object,System.IntPtr)");
        var logObj = M("System.Void UnityEngine.Debug::Log(System.Object)");
        var concat = M("System.String System.String::Concat(System.Object,System.Object)");
        var v3 = getPos.ReturnType; var f32 = mod.TypeSystem.Single; var i32 = mod.TypeSystem.Int32;
        Instruction I(OpCode op) => Instruction.Create(op);

        // fields: on/off, and the area the game had before the room follow took over
        var on = new FieldDefinition(OnField, FieldAttributes.Private, mod.TypeSystem.Boolean); cm.Fields.Add(on);
        var saved = area.Select(f => { var s = new FieldDefinition(f.Name + "43", FieldAttributes.Private, i32); cm.Fields.Add(s); return s; }).ToArray();

        MethodDefinition New(string name, TypeReference ret, bool pub, params (string n, TypeReference t)[] ps)
        {
            var m = new MethodDefinition(name, (pub ? MethodAttributes.Public : MethodAttributes.Private) | MethodAttributes.HideBySig, ret);
            foreach (var (n, t) in ps) m.Parameters.Add(new ParameterDefinition(n, ParameterAttributes.None, t));
            m.Body.InitLocals = true; cm.Methods.Add(m); return m;
        }

        // float Pan43(DungeonRoom room): how far the camera may move off the room center each way, so that the view
        // stays inside what 16:9 shows. 0 for no room and for boss rooms (they have their own follow); <= 0 at 16:9 and wider.
        var pan = New(Pan, f32, false, ("room", roomT));
        {
            var il = pan.Body.GetILProcessor(); var zero = Instruction.Create(OpCodes.Ldc_R4, 0f);
            il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Call, objOk); il.Emit(OpCodes.Brfalse, zero);
            il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Isinst, boss); il.Emit(OpCodes.Brtrue, zero);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, getVert); il.Emit(OpCodes.Ldc_R4, 16f / 9f); il.Emit(OpCodes.Mul);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, getHoriz); il.Emit(OpCodes.Sub); il.Emit(OpCodes.Ldc_R4, 0.5f); il.Emit(OpCodes.Mul); il.Emit(OpCodes.Ret);
            il.Append(zero); il.Emit(OpCodes.Ret);
        }

        // void End43(): if the room follow is on, turn it off, stop following and put the game's own area back.
        var end = New(End, mod.TypeSystem.Void, false);
        {
            var il = end.Body.GetILProcessor(); var ret = I(OpCodes.Ret);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, on); il.Emit(OpCodes.Brfalse, ret);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stfld, on);
            for (int i = 0; i < 4; i++) { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, saved[i]); il.Emit(OpCodes.Stfld, area[i]); }
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stfld, following);
            il.Append(ret);
        }

        // public void FollowRoom43(): in the current dungeon room, follow the player with the area set to the room
        // center +- Pan43 in x and the center in y (no vertical movement), like DungeonBossRoom.CameraFollowPlayer.
        var follow = New(Follow, mod.TypeSystem.Void, true);
        {
            var b = follow.Body; var r = new VariableDefinition(roomT); var half = new VariableDefinition(f32); var c = new VariableDefinition(v3);
            b.Variables.Add(r); b.Variables.Add(half); b.Variables.Add(c);
            var il = b.GetILProcessor(); var ret = I(OpCodes.Ret);
            il.Emit(OpCodes.Call, dmInst); il.Emit(OpCodes.Call, objOk); il.Emit(OpCodes.Brfalse, ret);
            il.Emit(OpCodes.Call, dmInst); il.Emit(OpCodes.Ldfld, curRoom); il.Emit(OpCodes.Stloc, r);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldloc, r); il.Emit(OpCodes.Call, pan); il.Emit(OpCodes.Stloc, half);
            il.Emit(OpCodes.Ldloc, half); il.Emit(OpCodes.Ldc_R4, 0.5f); il.Emit(OpCodes.Ble_Un, ret);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldnull); il.Emit(OpCodes.Call, setFollow);   // ends any earlier room follow first
            for (int i = 0; i < 4; i++) { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, area[i]); il.Emit(OpCodes.Stfld, saved[i]); }
            il.Emit(OpCodes.Ldloc, r); il.Emit(OpCodes.Callvirt, getTransform); il.Emit(OpCodes.Callvirt, getPos); il.Emit(OpCodes.Stloc, c);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldloca, c); il.Emit(OpCodes.Ldfld, vx); il.Emit(OpCodes.Ldloc, half); il.Emit(OpCodes.Sub); il.Emit(OpCodes.Conv_I4); il.Emit(OpCodes.Stfld, area[0]);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldloca, c); il.Emit(OpCodes.Ldfld, vx); il.Emit(OpCodes.Ldloc, half); il.Emit(OpCodes.Add); il.Emit(OpCodes.Conv_I4); il.Emit(OpCodes.Stfld, area[1]);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldloca, c); il.Emit(OpCodes.Ldfld, vy); il.Emit(OpCodes.Conv_I4); il.Emit(OpCodes.Stfld, area[2]);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldloca, c); il.Emit(OpCodes.Ldfld, vy); il.Emit(OpCodes.Conv_I4); il.Emit(OpCodes.Stfld, area[3]);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Stfld, on);
            il.Emit(OpCodes.Ldstr, "[4:3] dungeon room camera follows the player, pan +-"); il.Emit(OpCodes.Ldloc, half); il.Emit(OpCodes.Box, f32);
            il.Emit(OpCodes.Call, concat); il.Emit(OpCodes.Call, logObj);
            il.Append(ret);
        }

        // public void MoveCameraRoom43(Transform room, Action callback, bool clamp, DungeonDoor door): the room-change
        // camera move. For a normal room it glides straight to where the follow will hold it (the entry point's x,
        // clamped to the pan range), then starts the follow after the game's own callback. Otherwise it is MoveCamera.
        var moveRoom = New(Move, mod.TypeSystem.Void, true, ("room", moveT.Parameters[0].ParameterType), ("callback", moveT.Parameters[1].ParameterType), ("clamp", mod.TypeSystem.Boolean), ("door", door));
        {
            var b = moveRoom.Body; var r = new VariableDefinition(roomT); var half = new VariableDefinition(f32);
            var c = new VariableDefinition(v3); var p = new VariableDefinition(v3); var e = new VariableDefinition(v3);
            foreach (var v in new[] { r, half, c, p, e }) b.Variables.Add(v);
            var il = b.GetILProcessor(); var van = I(OpCodes.Ldarg_0); var mv = I(OpCodes.Ldarg_0); var dArg = moveRoom.Parameters[3];
            il.Emit(OpCodes.Call, dmInst); il.Emit(OpCodes.Call, objOk); il.Emit(OpCodes.Brfalse, van);
            il.Emit(OpCodes.Call, dmInst); il.Emit(OpCodes.Ldfld, curRoom); il.Emit(OpCodes.Stloc, r);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldloc, r); il.Emit(OpCodes.Call, pan); il.Emit(OpCodes.Stloc, half);
            il.Emit(OpCodes.Ldloc, half); il.Emit(OpCodes.Ldc_R4, 0.5f); il.Emit(OpCodes.Ble_Un, van);
            il.Emit(OpCodes.Ldloc, r); il.Emit(OpCodes.Callvirt, getTransform); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Call, objNeq); il.Emit(OpCodes.Brtrue, van);
            il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Callvirt, getPos); il.Emit(OpCodes.Stloc, c);
            il.Emit(OpCodes.Ldloc, c); il.Emit(OpCodes.Stloc, p);
            il.Emit(OpCodes.Ldarg, dArg); il.Emit(OpCodes.Call, objOk); il.Emit(OpCodes.Brfalse, mv);
            il.Emit(OpCodes.Ldarg, dArg); il.Emit(OpCodes.Ldfld, destRoom); il.Emit(OpCodes.Ldloc, r); il.Emit(OpCodes.Call, objNeq); il.Emit(OpCodes.Brtrue, mv);
            il.Emit(OpCodes.Ldarg, dArg); il.Emit(OpCodes.Ldfld, enterPt); il.Emit(OpCodes.Call, objOk); il.Emit(OpCodes.Brfalse, mv);
            il.Emit(OpCodes.Ldarg, dArg); il.Emit(OpCodes.Ldfld, enterPt); il.Emit(OpCodes.Callvirt, getPos); il.Emit(OpCodes.Stloc, e);
            il.Emit(OpCodes.Ldloca, p);
            il.Emit(OpCodes.Ldloca, e); il.Emit(OpCodes.Ldfld, vx);
            il.Emit(OpCodes.Ldloca, c); il.Emit(OpCodes.Ldfld, vx); il.Emit(OpCodes.Ldloc, half); il.Emit(OpCodes.Sub);
            il.Emit(OpCodes.Ldloca, c); il.Emit(OpCodes.Ldfld, vx); il.Emit(OpCodes.Ldloc, half); il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Call, clampF); il.Emit(OpCodes.Stfld, vx);
            il.Append(mv); il.Emit(OpCodes.Ldloc, p); il.Emit(OpCodes.Call, toV2); il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldftn, follow); il.Emit(OpCodes.Newobj, actionCtor);
            il.Emit(OpCodes.Call, combine); il.Emit(OpCodes.Castclass, actionCtor.DeclaringType); il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Call, moveV); il.Emit(OpCodes.Ret);
            il.Append(van); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Ldarg_3); il.Emit(OpCodes.Call, moveT); il.Emit(OpCodes.Ret);
        }
        foreach (var m in new[] { pan, end, follow, moveRoom }) m.Body.OptimizeMacros();

        // prefixes: whoever takes the camera next gets the game's own area back first
        int hooks = 0;
        foreach (var m in cm.Methods.Where(m => Hooked.Contains(m.Name) && m.HasBody).ToList())
        {
            if (m.Name == "TeleportCameraToPoint" && m.Parameters[0].ParameterType.Name != "Vector3") continue;   // the GameObject overload calls the Vector3 one
            var il = m.Body.GetILProcessor(); var first = m.Body.Instructions[0];
            if (m.Name == "MoveTo")
            {   // the follow's own per-frame MoveTo(_followingTarget: true) keeps it; any other move ends it
                var ft = m.Parameters.Single(q => q.Name == "_followingTarget");
                il.InsertBefore(first, Instruction.Create(OpCodes.Ldarg, ft)); il.InsertBefore(first, Instruction.Create(OpCodes.Brtrue, first));
            }
            il.InsertBefore(first, I(OpCodes.Ldarg_0)); il.InsertBefore(first, Instruction.Create(OpCodes.Call, end));
            m.Body.OptimizeMacros(); hooks++;
        }
        if (hooks != 7) throw new Exception($"door camera: expected 7 hooked CameraManager methods, got {hooks}");

        // DungeonManager.ChangeRoomTo (walking through a door): MoveCamera(to.transform, cb, false) -> MoveCameraRoom43(.., door)
        var change = dm.Methods.Single(m => m.Name == "ChangeRoomTo");
        if (change.Parameters.Count != 2 || change.Parameters[1].ParameterType.FullName != "DungeonDoor") throw new Exception("ChangeRoomTo signature changed");
        Swap(change, Instruction.Create(OpCodes.Ldarg_2));
        // DungeonManager.Start (coroutine): the first camera move of a floor, no door
        var start = dm.NestedTypes.Single(t => t.Name.StartsWith("<Start>d__")).Methods.Single(m => m.Name == "MoveNext");
        Swap(start, I(OpCodes.Ldnull));
        void Swap(MethodDefinition m, Instruction doorLoad)
        {
            var calls = m.Body.Instructions.Where(i => i.OpCode == OpCodes.Callvirt && i.Operand == moveT).ToList();
            if (calls.Count != 1) throw new Exception($"{m.FullName}: expected 1 MoveCamera(Transform) call, found {calls.Count}");
            m.Body.GetILProcessor().InsertBefore(calls[0], doorLoad);
            calls[0].OpCode = OpCodes.Call; calls[0].Operand = moveRoom;
            m.Body.OptimizeMacros();
        }

        // DungeonLoadingPanel (arriving on a floor, or back through an emblem portal): after the room's EnterRoom, start the follow
        var dlp = T("DungeonLoadingPanel");
        var cb = dlp.NestedTypes.SelectMany(t => t.Methods).Where(m => m.HasBody && m.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == LoadingMarker)).ToList();
        if (cb.Count != 1) throw new Exception($"DungeonLoadingPanel: expected 1 loading callback, found {cb.Count}");
        var enters = cb[0].Body.Instructions.Where(i => i.OpCode == OpCodes.Callvirt && i.Operand is MethodReference mr && mr.Name == "EnterRoom" && mr.DeclaringType.FullName == "DungeonRoom").ToList();
        if (enters.Count != 2) throw new Exception($"DungeonLoadingPanel callback: expected 2 EnterRoom calls, found {enters.Count}");
        {
            var il = cb[0].Body.GetILProcessor();
            foreach (var x in enters)
            {
                var next = x.Next;
                var seq = new[] { Instruction.Create(OpCodes.Call, cmInst), Instruction.Create(OpCodes.Call, objOk), Instruction.Create(OpCodes.Brfalse, next),
                                  Instruction.Create(OpCodes.Call, cmInst), Instruction.Create(OpCodes.Call, follow) };
                foreach (var s in seq) il.InsertBefore(next, s);
            }
            cb[0].Body.OptimizeMacros();
        }
        log.WriteLine($"door camera: CameraManager.{Follow}/{Move}/{Pan}/{End}, {hooks} hooks, ChangeRoomTo + Start + loading callback wired");
    }
}
