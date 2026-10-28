using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace KolyaMod;

// Совместная игра: имена сохраняемых полей передаются по сети номерами из общего словаря игры.
// Регистрируем наше поле с дополнительными зачарованиями и пересчитываем ширину номера.
public class KolyaSavedFields
{
    [SavedProperty]
    public string KolyaExtraEnchantments { get; set; } = "";

    public static void Register()
    {
        SavedPropertiesTypeCache.InjectTypeIntoCache(typeof(KolyaSavedFields));
        int count = 0;
        while (true)
        {
            try { SavedPropertiesTypeCache.GetPropertyNameForNetId(count); count++; }
            catch (ArgumentOutOfRangeException) { break; }
        }
        AccessTools.PropertySetter(typeof(SavedPropertiesTypeCache), nameof(SavedPropertiesTypeCache.NetIdBitSize))
            .Invoke(null, new object[] { (int)Math.Ceiling(Math.Log2(count)) });
    }
}
