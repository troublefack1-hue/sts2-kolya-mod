using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Merchant;

namespace KolyaMod;

// Удалять карты в магазине можно сколько угодно: слот не помечается использованным,
// а цена пересчитывается и растёт по ванильному правилу за каждое удаление.
[HarmonyPatch(typeof(MerchantCardRemovalEntry), nameof(MerchantCardRemovalEntry.SetUsed))]
public static class UnlimitedRemoval
{
    static bool Prefix(MerchantCardRemovalEntry __instance)
    {
        __instance.CalcCost();
        return false;
    }
}
