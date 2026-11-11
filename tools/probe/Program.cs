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

var mce = asm.GetTypes().First(t => t.Name == "MerchantCardEntry");
Console.WriteLine("=== MerchantCardEntry ctors ===");
foreach (var c in mce.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
    Console.WriteLine($"  {(c.IsPublic ? "public" : c.IsAssembly ? "internal" : c.IsFamily ? "protected" : "private")} ctor({string.Join(",", c.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");
var pop = mce.GetMethod("Populate");
Console.WriteLine($"Populate: {(pop.IsPublic ? "public" : pop.IsAssembly ? "internal" : "non-public")}");
var upd = mce.GetMethod("UpdateEntry");
Console.WriteLine($"UpdateEntry: {(upd.IsPublic ? "public" : upd.IsAssembly ? "internal" : "non-public")}");

var mi = asm.GetTypes().First(t => t.Name == "MerchantInventory");
Console.WriteLine("\n=== MerchantInventory: Player property, _colorlessCardEntries ===");
var pp = mi.GetProperty("Player", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
Console.WriteLine($"Player: {(pp == null ? "нет" : $"{(pp.GetMethod.IsPublic ? "+" : "-")} {pp.PropertyType.Name}")}");
var f = mi.GetField("_colorlessCardEntries", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
Console.WriteLine($"_colorlessCardEntries: {(f.IsPublic ? "public" : f.IsAssembly ? "internal" : "private")} {f.FieldType.Name}");

var cr = asm.GetTypes().First(t => t.Name == "CardRarity");
Console.WriteLine($"\nCardRarity: {string.Join(", ", Enum.GetNames(cr))}");

// ColorlessCardPool тип и ModelDb.CardPool<T>
var cp = asm.GetTypes().First(t => t.Name == "ColorlessCardPool");
Console.WriteLine($"\nColorlessCardPool: {cp.FullName}");
