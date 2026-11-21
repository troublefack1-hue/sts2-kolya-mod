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
        // KolyaSavedFields регистрируется постфиксом ModelIdSerializationCache.Init
        // (см. NetSafety.cs) — кэш к этому моменту ещё не инициализирован.
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
        try
        {
            MergedCards.PatchCardPlays(harmony);
        }
        catch (Exception e)
        {
            Log.Error($"[kolya_mod] не удалось подключить розыгрыш слитых карт: {e}");
        }
        Log.Info("[kolya_mod] загружен");
    }
}
