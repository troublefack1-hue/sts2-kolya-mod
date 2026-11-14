using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;
using MegaCrit.Sts2.Core.Settings;

namespace KolyaMod;

// 0.3.22: «карты разыгрываются быстрее» — поднимаем ванильный Fast Mode на шаг выше.
// FastMode живёт в PrefsSave (файл prefs.save, галочка «Быстрый режим» в настройках игры).
// Читают его 52 места (твины карт, полёты в стопки, смерть монстров, баннеры), поэтому
// вместо 52 патчей честно меняем саму настройку один раз после загрузки префов.
// Поднимаем только с Normal/None: если игрок сам выставил Fast/Instant — не трогаем.
[HarmonyPatch(typeof(PrefsSaveManager), nameof(PrefsSaveManager.LoadPrefs))]
public static class FastCards
{
    static void Postfix(PrefsSaveManager __instance)
    {
        var prefs = __instance.Prefs;
        if (prefs == null) return;
        // None — старое «не задано», игра сама нормализует его в Normal при старте
        if (prefs.FastMode != FastModeType.Normal && prefs.FastMode != FastModeType.None) return;
        prefs.FastMode = FastModeType.Fast;
        __instance.SavePrefs();
        Log.Info("[kolya_mod] fast mode: Normal -> Fast");
    }
}
