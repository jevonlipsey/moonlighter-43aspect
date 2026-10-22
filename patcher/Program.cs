// Moonlighter boot patch: stubs every UnityNativeChromaSDK call in GameManager.
// Under Proton the Razer plugin's dependency mfc140u.dll is missing, so the type
// initializer throws inside GameManager.Awake and the rest of game init never runs.
// usage: patcher <in Assembly-CSharp.dll> <out Assembly-CSharp.dll>
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

const string ChromaType = "ChromaSDK.UnityNativeChromaSDK";

if (args.Length != 2) { Console.Error.WriteLine("usage: patcher <in> <out>"); return 2; }
var res = new DefaultAssemblyResolver(); res.AddSearchDirectory(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[0]))!, "Managed"));
var asm = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters { AssemblyResolver = res });
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
        Console.WriteLine($"stubbed {mr.Name} in {m.Name}");
    }
    m.Body.OptimizeMacros();
}
if (stubbed == 0) throw new Exception("nothing patched");
asm.Write(args[1]);
Console.WriteLine($"ok: {stubbed} call(s) stubbed");
return 0;
