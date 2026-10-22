// Verifier: patched vs original Assembly-CSharp.dll.
// usage: verify <original> <patched>
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

var o = AssemblyDefinition.ReadAssembly(args[0]);
var p = AssemblyDefinition.ReadAssembly(args[1]);
int fail = 0;
void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fail++; }

Check(o.MainModule.AssemblyReferences.Select(r => r.FullName).SequenceEqual(p.MainModule.AssemblyReferences.Select(r => r.FullName)), "assembly references unchanged (Mono-safe: nothing resolved against the patcher's .NET)");
Check(o.MainModule.Types.Count == p.MainModule.Types.Count, "type count unchanged");

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
        if (Dump(m) != Dump(pm)) { changed.Add(m.FullName); if (Environment.GetEnvironmentVariable("VDEBUG") != null && !m.FullName.Contains("GameManager")) { var a = Dump(m).Split('|'); var b = Dump(pm).Split('|'); for (int q = 0; q < Math.Min(a.Length, b.Length); q++) if (a[q] != b[q]) { Console.WriteLine($"  [{q}] {a[q]}  =>  {b[q]}"); break; } } }
    }
}
Console.WriteLine("changed methods: " + string.Join(", ", changed));
Check(changed.Count > 0 && changed.All(c => c.Contains("GameManager::")), "only GameManager methods changed");

var gm = p.MainModule.GetType("GameManager");
int left = gm.Methods.Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
    .Count(i => i.Operand is MethodReference mr && mr.DeclaringType.FullName == "ChromaSDK.UnityNativeChromaSDK");
Check(left == 0, "no UnityNativeChromaSDK calls remain in GameManager");
foreach (var m in gm.Methods.Where(m => m.HasBody))
{
    var il = m.Body.Instructions; var set = new HashSet<Instruction>(il);
    Check(il.All(i => i.Operand is not Instruction t || set.Contains(t)) && m.Body.ExceptionHandlers.All(h => (h.TryStart == null || set.Contains(h.TryStart)) && (h.HandlerStart == null || set.Contains(h.HandlerStart))), $"branch/handler targets valid: {m.Name}");
}
Console.WriteLine(fail == 0 ? "ALL PASS" : $"{fail} FAILED");
return fail == 0 ? 0 : 1;
