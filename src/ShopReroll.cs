using System.Linq;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;

namespace KolyaMod;

// Кнопка «Обновить» в магазине: за золото заново раскладывает карты, реликвии и зелья.
// Цена — как у удаления карты (100, с инфляцией 75) и растёт на ту же надбавку за каждое обновление в этом магазине.
[HarmonyPatch(typeof(NMerchantInventory), nameof(NMerchantInventory.Initialize))]
public static class ShopReroll
{
    static readonly System.Reflection.MethodInfo Restock =
        AccessTools.Method(typeof(MerchantEntry), "RestockAfterPurchase");

    static int BaseCost() =>
        (int)AccessTools.Property(typeof(MerchantCardRemovalEntry), "BaseCost").GetValue(null)!;

    static System.Collections.Generic.IEnumerable<NMerchantSlot> Slots(Node node)
    {
        foreach (var child in node.GetChildren())
        {
            if (child is NMerchantSlot slot) yield return slot;
            foreach (var inner in Slots(child)) yield return inner;
        }
    }

    static void Postfix(NMerchantInventory __instance, MerchantInventory inventory)
    {
        int rerolls = 0;
        int Price() => BaseCost() + MerchantCardRemovalEntry.PriceIncrease * rerolls;

        var button = new Button { Name = "KolyaRerollButton", FocusMode = Control.FocusModeEnum.None };
        button.AddThemeFontSizeOverride("font_size", 28);
        button.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
        button.Position = new Vector2(-360, -140);
        button.CustomMinimumSize = new Vector2(320, 70);
        void Refresh()
        {
            button.Text = $"Обновить — {Price()} зол.";
            button.Disabled = inventory.Player.Gold < Price();
        }
        button.Pressed += async () =>
        {
            int price = Price();
            if (inventory.Player.Gold < price) return;
            await PlayerCmd.LoseGold(price, inventory.Player);
            rerolls++;
            foreach (var entry in inventory.CharacterCardEntries.Cast<MerchantEntry>()
                         .Concat(inventory.ColorlessCardEntries).Concat(inventory.RelicEntries).Concat(inventory.PotionEntries))
                Restock.Invoke(entry, new object?[] { inventory });
            foreach (var entry in inventory.AllEntries)
            {
                entry.CalcCost();
                entry.OnMerchantInventoryUpdated();
            }
            foreach (var slot in Slots(__instance))
            {
                if (slot.Entry == null || !slot.Entry.IsStocked) continue;
                slot.Visible = true;
                slot.MouseFilter = Control.MouseFilterEnum.Stop;
            }
            Log.Info($"[kolya_mod] магазин обновлён за {price} зол., обновление №{rerolls}");
            Refresh();
        };
        foreach (var entry in inventory.AllEntries)
            entry.PurchaseCompleted += (_, _) => Refresh();
        __instance.AddChild(button);
        Refresh();
    }
}
