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

var pm = asm.GetTypes().First(t => t.Name == "PowerModel");
foreach (var t in asm.GetTypes().Where(t => t.IsSubclassOf(pm) && t.IsPublic).OrderBy(t => t.Name))
{
    // только силы монстров (не игрока): эвристика — у всех печатаем, пользователь выберет
    Console.WriteLine(t.Name);
}
