using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

namespace KolyaMod;

// Карты, у которых повторное улучшение ничего не меняет (docs/card_upgrades.md), получают за лишние уровни
// бонус: карты со словом-свойством — дополнительные применения со 2-го уровня, карты с удешевлением —
// после того, как цена дошла до 0, прочие — +2 к первому числовому стату (StatUpgrade), а если чисел нет —
// дополнительное применение. Баланс 0.3.19: повтор за каждый ВТОРОЙ лишний уровень и не больше 3 повторов
// (было: за каждый уровень — карта «+6» игралась 6 раз, прокачка была слишком быстрой).
// Игра сама пишет «Повтор N» на карте и в подсказке.
[HarmonyPatch(typeof(CardModel), nameof(CardModel.GetEnchantedReplayCount))]
public static class ExtraPlays
{
    // улучшение — слово-свойство: повторы со 2-го уровня.
    // 0.3.36: 16 талантов убраны — повтор у таланта ничего не даёт (мёртвый бонус);
    // они попали в StatUpgrade и получают удешевление до 0, как остальные таланты.
    internal static readonly HashSet<string> Keyword = new()
    {
        "Anointed", "Apparition", "BigBang", "CalculatedGamble", "Chill", "DemonicShield",
        "Discovery", "Dredge", "Fusion", "GoldAxe", "Hotfix", "Ignition",
        "KnowThyPlace", "MadScience", "Mimic", "Monologue", "Prolong", "Rainbow",
        "RoyalGamble", "Scare", "Scrawl", "SecretTechnique", "SecretWeapon",
        "Synchronize", "ThinkingAhead", "TimesUp", "Transfigure", "Voltaic", "Wish", "Wisp",
    };

    // 0.3.36: карты-множители — повторы удваивают/растягивают весь эффект (анализ docs/risky_cards.md).
    // Обычный потолок +3 слишком жирный: X-cost разгоняют ход бесконечно, Shadowmeld даёт x8 блок.
    internal static readonly HashSet<string> SoftRepeats = new()
    {
        "Cascade", "MultiCast", "Tempest", "Malaise",     // X-cost: повтор = тот же X ещё раз
        "Shadowmeld",                                    // блок x4/x8 с Unmovable
        "Nightmare",                                     // 6 копий любой карты
        "SignalBoost",                                   // талант x3
        "Monologue",                                     // +2 Силы за каждую карту в ход
        "Voltaic",                                       // саморазгон накопленных молний
        "Quadcast", "Eidolon",                           // 8 эвоков / сброс Эфирных дважды
    };

    static int SoftCap(string name) => SoftRepeats.Contains(name) ? 1 : MaxBonus;

    // улучшение — удешевление: сколько уровней уходит на то, чтобы цена дошла до 0
    internal static readonly Dictionary<string, int> CostLevels = new()
    {
        ["Alchemize"] = 1, ["Automation"] = 1, ["BansheesCry"] = 5, ["Barricade"] = 3,
        ["BodySlam"] = 1, ["BulletTime"] = 3, ["Calamity"] = 3, ["Corruption"] = 3, ["CreativeAi"] = 3,
        ["DarkEmbrace"] = 2, ["Demesne"] = 3, ["Distraction"] = 1, ["DoubleEnergy"] = 1, ["Dualcast"] = 1,
        ["Eidolon"] = 2, ["Entrench"] = 2, ["ExpectAFight"] = 2, ["Feral"] = 2, ["Flanking"] = 2,
        ["ForbiddenGrimoire"] = 2, ["HammerTime"] = 2, ["Havoc"] = 1, ["Hellraiser"] = 2, ["InfernalBlade"] = 1,
        ["MasterPlanner"] = 2, ["Mayhem"] = 2, ["MindBlast"] = 1, ["Mirage"] = 1, ["MonarchsGaze"] = 2,
        ["Murder"] = 3, ["Nightmare"] = 3, ["Nostalgia"] = 1, ["Orbit"] = 2, ["Pagestorm"] = 1,
        ["Quadcast"] = 1, ["Sacrifice"] = 1, ["Seance"] = 1, ["SentryMode"] = 2, ["SevenStars"] = 2,
        ["Shadowmeld"] = 1, ["ShadowStep"] = 1, ["SignalBoost"] = 1, ["SovereignBlade"] = 2, ["SpectrumShift"] = 2,
        ["Stampede"] = 2, ["Stratagem"] = 1, ["Subroutine"] = 1, ["SwordSage"] = 2, ["Tank"] = 1,
        ["TheSealedThrone"] = 1, ["ToolsOfTheTrade"] = 1, ["Tracking"] = 2, ["Unmovable"] = 2, ["WhiteNoise"] = 1,
        ["Zap"] = 1,
    };

    // Карты, которым повторы за лишние уровни запрещены: массовый апгрейд колоды сам себя
    // разгоняет — улучшенный Апофеоз с повторами прокачивает ВСЕ карты несколько раз за бой
    // каждый бой (жалоба Коли: кинжалы на 600 урона). Без повторов он ванильный: один
    // розыгрыш — один ванильный апгрейд всех карт. Убран из CostLevels — иначе упал бы
    // в ветку «прочие карты без статов» и получил бы повторы ещё раньше (баланс 0.3.29).
    internal static readonly HashSet<string> NoRepeats = new()
    {
        "Apotheosis",
        // 0.3.31: «Прилив сил» раздаёт энергию всей команде; повторы превращали её в бесконечный ход
        "EnergySurge",
    };

    public const int MaxBonus = 3;   // потолок повторов (0.3.19)

    // 0.3.33: карта даёт энергию (EnergyVar) — повторы за уровни запрещены (бесконечная мана),
    // вместо них удешевление (StatUpgrade) и ванильный рост энергии (под потолком StatCap).
    public static bool HasEnergyVar(CardModel card)
    {
        foreach (var kv in card.DynamicVars)
            if (kv.Value is EnergyVar) return true;
        return false;
    }

    // повтор за каждый второй лишний уровень, максимум потолка (0.3.19: было за каждый — слишком быстро)
    static int Repeats(int extraLevels, string name) =>
        System.Math.Min(SoftCap(name), System.Math.Max(extraLevels, 0) / 2);

    public static int Bonus(CardModel card)
    {
        var name = card.GetType().Name;
        if (NoRepeats.Contains(name)) return 0;
        if (HasEnergyVar(card)) return 0;
        if (Keyword.Contains(name)) return Repeats(card.CurrentUpgradeLevel - 1, name);
        if (CostLevels.TryGetValue(name, out var toZero)) return Repeats(card.CurrentUpgradeLevel - toZero, name);
        // прочие карты: с числовыми статами рост идёт через StatUpgrade; без статов — повторы
        if (StatUpgrade.HasStatVar(card)) return 0;
        return Repeats(card.CurrentUpgradeLevel - 1, name);
    }

    static void Postfix(CardModel __instance, ref int __result) => __result += Bonus(__instance);
}
