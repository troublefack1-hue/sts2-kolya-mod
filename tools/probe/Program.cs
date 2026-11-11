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

void IL(MethodBase ci, int max = 80)
{
    var body = ci.GetMethodBody();
    if (body == null) { Console.WriteLine("  <no body>"); return; }
    var il = body.GetILAsByteArray()!;
    var pos = 0; var n = 0;
    while (pos < il.Length && n < max)
    {
        int offset = pos;
        ushort op = il[pos++];
        if (op == 0xfe) op = (ushort)(0xfe00 | il[pos++]);
        string? operand = null; int token;
        switch (op)
        {
            case 0x72: token = BitConverter.ToInt32(il, pos); pos += 4;
                try { operand = "\"" + ci.Module.ResolveString(token) + "\""; } catch { operand = $"str:{token:x}"; } break;
            case 0x73: case 0x28: case 0x6f: case 0x7b: case 0x7d: case 0x7e: case 0x80: case 0x74: case 0x71: case 0x70:
                token = BitConverter.ToInt32(il, pos); pos += 4;
                try {
                    var m = op switch { 0x73 or 0x28 or 0x6f => (object?)ci.Module.ResolveMethod(token), 0x74 => ci.Module.ResolveType(token), _ => ci.Module.ResolveMember(token) };
                    operand = m?.ToString()?.Replace("MegaCrit.Sts2.Core.", "M.").Replace("System.Threading.Tasks.", "");
                } catch { operand = $"tok:{token:x}"; } break;
            case 0x20: operand = BitConverter.ToInt32(il, pos).ToString(); pos += 4; break;
            case 0x1f: case 0x12: case 0x13: case 0x11: case 0x0e: case 0x0f: case 0x10: operand = il[pos++].ToString(); break;
            default:
                if (op >= 0x38 && op <= 0x44) { operand = BitConverter.ToInt32(il, pos).ToString(); pos += 4; }
                break;
        }
        Console.WriteLine($"  {offset:x4}: {op:x2} {operand}");
        n++;
    }
}

var cm = asm.GetTypes().First(t => t.Name == "CardModel");
Console.WriteLine("=== set_CurrentUpgradeLevel ===");
IL(cm.GetMethod("set_CurrentUpgradeLevel", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!);
Console.WriteLine("=== MaxUpgradeLevel (declared) ===");
var mp = cm.GetProperty("MaxUpgradeLevel", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
Console.WriteLine(mp == null ? "нет у CardModel" : $"{mp.DeclaringType?.Name}.{mp.Name} {(mp.GetMethod?.IsPublic == true ? "public" : "non-public")} {(mp.GetMethod?.IsVirtual == true ? "virtual" : "")}");
