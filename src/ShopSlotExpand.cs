using System.Collections.Generic;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;

namespace KolyaMod;

// 0.3.25: настоящая причина «сломанного магазина». Ванильный NMerchantInventory.Initialize
// раскладывает записи ColorlessCardEntries по слотам через GetChild<NMerchantCard>(i) —
// в сцене контейнера ровно 2 слота (ваниль продаёт 2 нейтральные карты). Наши дополнительные
// слоты (ColorlessShop) создают 3-ю запись → GetChild(2) за границей → NRE → комната магазина
// не открывается, ввод блокируется, ломается карта. Лечение: перед ванильной Initialize
// дублируем недостающие слоты в контейнере — дубликат раскладывается витриной как обычно.
[HarmonyPatch(typeof(NMerchantInventory), nameof(NMerchantInventory.Initialize))]
public static class ShopSlotExpand
{
    static void Prefix(NMerchantInventory __instance, MerchantInventory inventory)
    {
        try
        {
            var container = AccessTools.Field(typeof(NMerchantInventory), "_colorlessCardContainer")
                .GetValue(__instance) as Control;
            if (container == null || container.GetChildCount() == 0) return;
            var entries = (List<MerchantCardEntry>)AccessTools.Field(typeof(MerchantInventory), "_colorlessCardEntries")
                .GetValue(inventory)!;
            var proto = container.GetChild(0);
            while (container.GetChildCount() < entries.Count)
            {
                var copy = proto.Duplicate();
                container.AddChild(copy);
            }
            if (container.GetChildCount() > 2)
                Log.Info($"[kolya_mod] магазин: слотов нейтральных карт {container.GetChildCount()} (записей {entries.Count})");
        }
        catch (System.Exception ex)
        {
            Log.Error($"[kolya_mod] не удалось расширить слоты магазина: {ex.Message}");
        }
    }
}
