using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

var gameDir = @"E:\Steam\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64";
AssemblyLoadContext.Default.Resolving += (ctx, name) =>
{
    var p = Path.Combine(gameDir, name.Name + ".dll");
    return File.Exists(p) ? ctx.LoadFromAssemblyPath(p) : null;
};
var asm = Assembly.LoadFrom(Path.Combine(gameDir, "sts2.dll"));

// HardenedShellPower: что делает (урон за удар <= Amount?)
var t = asm.GetTypes().First(x => x.Name == "MerchantCardEntry");
Console.WriteLine($"=== {t.Name} ===");
foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
{
    if (m.IsSpecialName) continue;
    if (!(m.Name == "Populate" || m.Name == ".ctor")) continue;
    Console.WriteLine($"  {(m.IsPublic ? "+" : "-")} {(m.IsVirtual ? "virt " : "")}{m.ReturnType.Name} {m.Name}({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");
    var sm = m;
    if (m.ReturnType.Name.Contains("Task"))
    {
        var nested = t.GetNestedTypes(BindingFlags.NonPublic).FirstOrDefault(x => x.Name.Contains(m.Name));
        if (nested != null) sm = nested.GetMethod("MoveNext", BindingFlags.NonPublic | BindingFlags.Instance)!;
    }
    var body = sm.GetMethodBody();
    if (body == null) continue;
    var il = body.GetILAsByteArray()!;
    for (int pos = 0; pos + 5 <= il.Length; pos++)
        if (il[pos] == 0x28 || il[pos] == 0x6f)
        {
            try {
                var tgt = sm.Module.ResolveMethod(BitConverter.ToInt32(il, pos + 1));
                var s = tgt?.ToString()?.Replace("MegaCrit.Sts2.Core.", "M.").Replace("System.Threading.Tasks.", "").Replace("System.Collections.Generic.", "");
                if (s != null && !s.Contains("AsyncTaskMethodBuilder") && !s.Contains("TaskAwaiter") && !s.Contains("RuntimeHelpers"))
                    Console.WriteLine($"      -> {s}");
            } catch {}
        }
}
