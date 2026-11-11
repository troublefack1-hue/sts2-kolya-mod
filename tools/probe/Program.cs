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

// публичность типов статов
foreach (var n in new[] { "DynamicVar", "DamageVar", "BlockVar", "PowerVar`1", "HealVar", "MaxHpVar", "HpLossVar", "GoldVar", "DynamicVarSet" })
{
    var t = asm.GetTypes().FirstOrDefault(x => x.Name == n);
    if (t != null)
        Console.WriteLine($"{n}: public={t.IsPublic} ns={t.Namespace} generic={(t.IsGenericTypeDefinition ? "def" : (t.IsGenericType ? t.GetGenericTypeDefinition().Name : "no"))}");
    else
        Console.WriteLine($"{n}: НЕ НАЙДЕН");
}
