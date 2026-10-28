using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;

namespace KolyaMod;

// Карты, у которых повторное улучшение ничего не меняет (docs/card_upgrades.md), получают за лишние уровни
// дополнительные применения: +1 повтор за уровень. Карты со словом-свойством — со 2-го уровня,
// карты с удешевлением — после того, как цена дошла до 0. Игра сама пишет «Повтор N» на карте и в подсказке.
[HarmonyPatch(typeof(CardModel), nameof(CardModel.GetEnchantedReplayCount))]
public static class ExtraPlays
{
    // улучшение — слово-свойство: повторы со 2-го уровня
    static readonly HashSet<string> Keyword = new()
    {
        "Afterimage", "Aggression", "Anointed", "Apparition", "Arsenal", "BeaconOfHope",
        "BigBang", "CalculatedGamble", "CallOfTheVoid", "Chill", "DemonicShield", "Discovery",
        "Dredge", "EchoForm", "Entropy", "Fusion", "GoldAxe", "HelloWorld",
        "Hotfix", "Ignition", "InfiniteBlades", "Juggling", "KnowThyPlace", "MachineLearning",
        "MadScience", "Mimic", "Monologue", "Prolong", "Rainbow", "ReaperForm",
        "RoyalGamble", "Scare", "Scrawl", "SecretTechnique", "SecretWeapon", "Speedster",
        "Synchronize", "ThinkingAhead", "TimesUp", "Transfigure", "TrashToTreasure", "Tyranny",
        "VoidForm", "Voltaic", "Wish", "Wisp",
    };

    // улучшение — удешевление: сколько уровней уходит на то, чтобы цена дошла до 0
    static readonly Dictionary<string, int> CostLevels = new()
    {
        ["Alchemize"] = 1, ["Apotheosis"] = 2, ["Automation"] = 1, ["BansheesCry"] = 5, ["Barricade"] = 3,
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

    public static int Bonus(CardModel card)
    {
        var name = card.GetType().Name;
        if (Keyword.Contains(name)) return System.Math.Max(card.CurrentUpgradeLevel - 1, 0);
        if (CostLevels.TryGetValue(name, out var toZero)) return System.Math.Max(card.CurrentUpgradeLevel - toZero, 0);
        return 0;
    }

    static void Postfix(CardModel __instance, ref int __result) => __result += Bonus(__instance);
}
