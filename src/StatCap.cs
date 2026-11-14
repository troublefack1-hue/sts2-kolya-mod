using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Logging;

namespace KolyaMod;

// Потолок роста статов карты. Без него комбинация «Апофеоз + карта-копия + повторы» прокачивает
// урон бесконечно: у кинжала каждый уровень даёт +4 урона, а MultiUpgrade разрешает 999 уровней
// (у игрока был кинжал на 600 урона). Весь рост статов в игре идёт через DynamicVar.UpgradeValueBy
// (зонд подтвердил: все вызывающие — OnUpgrade карт, кроме Wither.FakeUpgrade), поэтому потолок
// ставим там: он работает и на ванильный апгрейд, и на StatUpgrade, и на прокачку Апофеозом.
// Зачарования рост не трогают — они меняют EnchantedValue, а не BaseValue.
// Копия карты (Mimic) получает свежий DynamicVar с уже выросшей базой — потолок для копии
// считается от её базы, поэтому у скопированной карты есть запас ещё одного потолка. Сознательно
// принято: копия — отдельная карта, а бесконечного роста внутри одной копии это не даёт.
public static class StatCap
{
    public const decimal DamageCap = 30m;
    public const decimal BlockCap = 40m;
    public const decimal OtherCap = 30m;

    class Track { public decimal Initial; public bool Set; }
    static readonly ConditionalWeakTable<DynamicVar, Track> Tracks = new();

    public static decimal CapFor(DynamicVar v) => v switch
    {
        DamageVar => DamageCap,
        BlockVar => BlockCap,
        _ => OtherCap,
    };

    // true, если стат уже на потолке и расти дальше не может
    public static bool IsCapped(DynamicVar v)
    {
        var t = Tracks.GetValue(v, _ => new Track());
        if (!t.Set) { t.Initial = v.BaseValue; t.Set = true; }
        return v.BaseValue - t.Initial >= CapFor(v);
    }

    [HarmonyPatch(typeof(DynamicVar), nameof(DynamicVar.UpgradeValueBy))]
    static class Clamp
    {
        static bool Prefix(DynamicVar __instance, decimal amount)
        {
            if (amount <= 0m) return true;
            if (!IsCapped(__instance)) return true;
            Log.Info($"[kolya_mod] потолок статов: {__instance.Name} уже +{__instance.BaseValue:0.#}, прирост {amount:0.#} отклонён");
            return false;
        }
    }
}
