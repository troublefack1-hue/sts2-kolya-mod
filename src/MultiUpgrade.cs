using System.Reflection;
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

// Ванильный сеттер бросает исключение, если уровень выше MaxUpgradeLevel. С модом карты с
// уровнем выше ванильного максимума легальны, а старые сейвы могут содержать их с прошлых
// версий — пишем значение в поле напрямую, без проверки (загрузка не должна умирать).
[HarmonyPatch(typeof(CardModel), "set_CurrentUpgradeLevel")]
public static class TolerantUpgradeLevelSetter
{
    static readonly FieldInfo Field = AccessTools.Field(typeof(CardModel), "_currentUpgradeLevel");

    static bool Prefix(CardModel __instance, int value)
    {
        __instance.AssertMutable();
        Field.SetValue(__instance, value);
        return false;
    }
}
