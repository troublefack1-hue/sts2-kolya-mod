using System;
using System.Collections;
using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;

namespace KolyaMod;

// 0.3.23: спасение сломанных сохранений. В сейвах, сделанных до фикса ColorlessShop,
// остались записи магазина с CreationResult.Card == null (серые пустые слоты с ценой 99).
// При входе в магазин NMerchantDialogue.ShowOnInventoryOpen() падает с NRE на такой записи,
// ввод блокируется и ломается вся карта. Лечение: при открытии магазина вырезаем
// незаполненные записи из списков инвентаря — витрина просто покажет меньше слотов.
[HarmonyPatch(typeof(NMerchantInventory), nameof(NMerchantInventory.Open))]
public static class ShopSaveFix
{
    static void Prefix(NMerchantInventory __instance)
    {
        var inv = __instance.Inventory;
        if (inv == null) return;
        try
        {
            int removed = 0;
            removed += Clean(inv, "_colorlessCardEntries");
            removed += Clean(inv, "_characterCardEntries");
            removed += Clean(inv, "_relicEntries");
            removed += Clean(inv, "_potionEntries");
            if (removed > 0)
                Log.Info($"[kolya_mod] магазин: вырезано {removed} битых записей из сохранения");
        }
        catch (Exception ex)
        {
            Log.Error($"[kolya_mod] чистка магазина не удалась: {ex.Message}");
        }
    }

    static int Clean(MerchantInventory inv, string fieldName)
    {
        var list = (IList)AccessTools.Field(typeof(MerchantInventory), fieldName).GetValue(inv)!;
        int before = list.Count;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var entry = list[i];
            if (entry is MerchantCardEntry cardEntry)
            {
                if (cardEntry.CreationResult?.Card == null) list.RemoveAt(i);
            }
        }
        return before - list.Count;
    }
}
