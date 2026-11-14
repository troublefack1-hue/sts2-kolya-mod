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
            // 0.3.32: EnergyVar — полноценный стат. Без этого энергокарты (Адреналин, Турбо,
            // Кровопускание, Двойная энергия и др.) не считались «с картой статов» и падали
            // в ветку ExtraPlays «прочих карт» — получали ПОВТОРЫ за уровни и разгоняли
            // бесконечную ману. Теперь: повторов нет, энергия растёт через StatUpgrade
            // с потолком StatCap.EnergyCap (+4), потом рост уходит в прочие статы.
            if (t == typeof(EnergyVar)) { if (!StatCap.IsCapped(v)) return v; continue; }
            if (t == typeof(DamageVar)) { if (!StatCap.IsCapped(v)) return v; continue; }
            if (t == typeof(BlockVar)) { if (block == null && !StatCap.IsCapped(v)) block = v; continue; }
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
        // 0.3.33/0.3.34: энергокарты не получают ни повторов (ExtraPlays), ни удешевления —
        // бесплатная энергокарта = тот же бесконечный ход через прокрутку колоды (аргумент Коли).
        // Растут только ванильным апгрейдом энергии, суммарно не выше StatCap.EnergyCap (+4).
        if (ExtraPlays.HasEnergyVar(card)) return;
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
        // баланс 0.3.19: прибавка +2 только на чётных уровнях (в среднем +1 за уровень вместо +2) —
        // с костром «+2 за отдых» карты качались слишком быстро
        if (card.CurrentUpgradeLevel % 2 != 0) return;
        var stat = PickStat(card.DynamicVars);
        if (stat == null) return; // нет статов — повторы даёт ExtraPlays
        stat.UpgradeValueBy(Amount);
        Log.Info($"[kolya_mod] статы: {card.Id} +{Amount} к {stat.Name} (уровень {card.CurrentUpgradeLevel})");
    }
}
