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

var tt = asm.GetTypes().First(t => t.Name == "TargetType");
Console.WriteLine($"TargetType: {string.Join(", ", Enum.GetNames(tt))}");
var cm = asm.GetTypes().First(t => t.Name == "CardModel");
var p = cm.GetProperty("TargetType", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
Console.WriteLine($"CardModel.TargetType: {(p == null ? "нет" : $"{(p.GetMethod?.IsPublic == true ? "+" : "-")} {p.PropertyType.Name}")}");
