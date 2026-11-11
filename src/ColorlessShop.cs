using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;

namespace KolyaMod;

// Нейтральные (бесцветные) карты в магазине: их может играть любой персонаж — удобный товар
// и «валюта» для обмена между игроками. Ваниль продаёт ровно 2 (необычная и редкая,
// поле _colorlessCardRarities в MerchantInventory). Добавляем слоты за акты:
// обычная — всегда, вторая редкая — с 4-го акта, вторая необычная — с 7-го.
// Слоты создаём тем же конструктором MerchantCardEntry(player, inventory, pool, rarity),
// что и ваниль — отображение, цены и покупка работают как у обычных нейтральных карт.
[HarmonyPatch(typeof(MerchantInventory), "PopulateColorlessCardEntries")]
public static class ColorlessShop
{
    static readonly (int MinAct, CardRarity Rarity)[] ExtraSlots =
    {
        (0, CardRarity.Common),
        (3, CardRarity.Rare),
        (6, CardRarity.Uncommon),
    };

    static void Postfix(MerchantInventory __instance)
    {
        var player = __instance.Player;
        int act = player.RunState.CurrentActIndex;
        var pool = ModelDb.CardPool<ColorlessCardPool>()
            .GetUnlockedCards(player.UnlockState, player.RunState.CardMultiplayerConstraint)
            .ToList();
        var entries = (List<MerchantCardEntry>)AccessTools.Field(typeof(MerchantInventory), "_colorlessCardEntries")
            .GetValue(__instance)!;
        foreach (var (minAct, rarity) in ExtraSlots)
        {
            if (act < minAct) continue;
            var entry = new MerchantCardEntry(player, __instance, pool, rarity);
            entry.Populate();
            entries.Add(entry);
            Log.Info($"[kolya_mod] магазин: нейтральный слот «{rarity}» (акт {act + 1})");
        }
    }
}
