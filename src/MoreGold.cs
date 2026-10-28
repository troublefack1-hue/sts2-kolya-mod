using HarmonyLib;
using MegaCrit.Sts2.Core.Hooks;

namespace KolyaMod;

// Всё добываемое золото (бои, сундуки, события) — ×1,5, округление вниз.
[HarmonyPatch(typeof(Hook), nameof(Hook.ModifyGoldGained))]
public static class MoreGold
{
    static void Postfix(ref decimal __result)
    {
        if (__result > 0m) __result = decimal.Floor(__result * 1.5m);
    }
}
