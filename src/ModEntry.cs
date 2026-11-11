using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace KolyaMod;

[ModInitializer(nameof(Init))]
public static class ModEntry
{
    public static void Init()
    {
        KolyaSavedFields.Register();
        // Патчим каждый класс отдельно: падение одного транспилера не должно отключать весь мод.
        var harmony = new Harmony("kolya.kolya_mod");
        foreach (var type in AccessTools.GetTypesFromAssembly(typeof(ModEntry).Assembly))
        {
            try
            {
                harmony.CreateClassProcessor(type).Patch();
            }
            catch (Exception e)
            {
                Log.Error($"[kolya_mod] не удалось применить патчи из {type.Name}: {e.GetType().Name}: {e.Message}");
            }
        }
        Log.Info("[kolya_mod] загружен");
    }
}
