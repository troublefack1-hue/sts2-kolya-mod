using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;

namespace KolyaMod;

// Кузнец у костра улучшает карту на 2 уровня вместо 1: после ванильного улучшения каждая
// выбранная карта улучшается ещё раз, если ещё может. Выбор карты и синхронизация — ванильные.
[HarmonyPatch(typeof(SmithRestSiteOption), nameof(SmithRestSiteOption.OnSelect))]
public static class SmithTwice
{
    static readonly System.Reflection.FieldInfo Selection =
        AccessTools.Field(typeof(SmithRestSiteOption), "_selection");

    static void Postfix(SmithRestSiteOption __instance, ref Task<bool> __result) =>
        __result = UpgradeAgain(__result, __instance);

    static async Task<bool> UpgradeAgain(Task<bool> before, SmithRestSiteOption smith)
    {
        if (!await before) return false;
        foreach (var card in ((IEnumerable<CardModel>?)Selection.GetValue(smith) ?? Enumerable.Empty<CardModel>()).ToList())
        {
            if (!card.IsUpgradable) continue;
            CardCmd.Upgrade(card, CardPreviewStyle.None);
            Log.Info($"[kolya_mod] кузнец: {card.Id} улучшена до +{card.CurrentUpgradeLevel}");
        }
        return true;
    }
}
