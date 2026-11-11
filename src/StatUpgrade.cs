using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;

namespace KolyaMod;

// Карты, у которых за пределами ванильного улучшения нечем расти, получают рост статов: если
// ванильное улучшение не изменило ничего (сверено значений до/после), карта получает +2 к первому
// числовому стату (урон → блок → сила эффекта → исцеление). Таланты (карты Power) вместо этого
// сначала удешевляются на 1 за уровень, а по нулевой цене растут как все. Карты, растящие ванильным
// апгрейдом повторно (Удар, Обратный отсчёт — у них каждый уровень даёт ванильную прибавку),
// не дублируются. У карт вообще без чисел повторы за лишние уровни даёт ExtraPlays.
[HarmonyPatch(typeof(CardModel), "UpgradeInternal")]
public static class StatUpgrade
{
    public const decimal Amount = 2m;

    // есть ли у карты числовой стат, который можно растить
    public static bool HasStatVar(CardModel card) => PickStat(card.DynamicVars) != null;

    static DynamicVar? PickStat(DynamicVarSet vars)
    {
        DynamicVar? block = null, power = null, heal = null;
        foreach (var kv in vars)
        {
            var v = kv.Value;
            var t = v.GetType();
            if (t == typeof(DamageVar)) return v;
            if (t == typeof(BlockVar)) { block ??= v; continue; }
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(PowerVar<>)) { power ??= v; continue; }
            if (t == typeof(HealVar)) heal ??= v;
        }
        return block ?? power ?? heal;
    }

    static void Prefix(CardModel __instance, out Dictionary<string, decimal> __state)
    {
        __state = new();
        foreach (var kv in __instance.DynamicVars)
            __state[kv.Key] = kv.Value.BaseValue;
    }

    static void Postfix(CardModel __instance, Dictionary<string, decimal> __state)
    {
        var card = __instance;
        if (card.CurrentUpgradeLevel <= 1) return;
        var name = card.GetType().Name;
        if (ExtraPlays.Keyword.Contains(name) || ExtraPlays.CostLevels.ContainsKey(name)) return;
        // ванильное улучшение само что-то изменило? значит карта уже растёт — не дублируем прибавку
        foreach (var kv in card.DynamicVars)
            if (__state.TryGetValue(kv.Key, out var before) && kv.Value.BaseValue != before) return;
        // Таланты (карты Power) сначала удешевляются на 1 за уровень, пока цена не станет 0
        if (card.Type == CardType.Power && !card.EnergyCost.CostsX && card.EnergyCost.Canonical > 0)
        {
            card.EnergyCost.UpgradeBy(-1);
            Log.Info($"[kolya_mod] талант: {card.Id} цена снижена до {card.EnergyCost.Canonical} (уровень {card.CurrentUpgradeLevel})");
            return;
        }
        var stat = PickStat(card.DynamicVars);
        if (stat == null) return; // нет статов — повторы даёт ExtraPlays
        stat.UpgradeValueBy(Amount);
        Log.Info($"[kolya_mod] статы: {card.Id} +{Amount} к {stat.Name} (уровень {card.CurrentUpgradeLevel})");
    }
}
