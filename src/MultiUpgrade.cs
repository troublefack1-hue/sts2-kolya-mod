using HarmonyLib;
using MegaCrit.Sts2.Core.Models;

namespace KolyaMod;

// Карту можно улучшать сколько угодно раз: каждое улучшение снова прибавляет ванильную прибавку,
// название показывает «+N». Карты с собственным пределом (проклятия, статусы, особые) не трогаем.
[HarmonyPatch(typeof(CardModel), nameof(CardModel.MaxUpgradeLevel), MethodType.Getter)]
public static class MultiUpgrade
{
    public const int Limit = 999;

    static void Postfix(ref int __result)
    {
        if (__result == 1) __result = Limit;
    }
}
