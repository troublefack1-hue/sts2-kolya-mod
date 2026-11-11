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

var ec = asm.GetTypes().First(t => t.Name == "CardEnergyCost");
Console.WriteLine($"=== CardEnergyCost : {ec.BaseType?.Name} ===");
foreach (var p in ec.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
    Console.WriteLine($"prop {(p.GetMethod?.IsPublic == true ? "+" : "-")} {p.PropertyType.Name} {p.Name}");
foreach (var m in ec.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
    Console.WriteLine($"meth {(m.IsPublic ? "+" : "-")} {m.ReturnType.Name} {m.Name}({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");

// CardModel.Type — как определить Power
var cm = asm.GetTypes().First(t => t.Name == "CardModel");
var typeProp = cm.GetProperty("Type");
Console.WriteLine($"CardModel.Type: {typeProp?.PropertyType.FullName}");
