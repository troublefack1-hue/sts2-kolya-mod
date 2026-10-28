using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;

namespace KolyaMod;

// Блок игрока не сгорает в конце хода, как под «Баррикадой». Враги теряют блок по-прежнему.
[HarmonyPatch(typeof(Hook), nameof(Hook.ShouldClearBlock))]
public static class KeepBlock
{
    static void Postfix(ref bool __result, Creature creature)
    {
        if (creature.IsPlayer) __result = false;
    }
}

// С вечным блоком «Баррикада» и «Размытие» бесполезны — убираем их из наград и магазина.
[HarmonyPatch(typeof(CardPoolModel), nameof(CardPoolModel.GetUnlockedCards))]
public static class RemoveBlockKeepers
{
    static IEnumerable<CardModel> Postfix(IEnumerable<CardModel> __result) =>
        __result.Where(c => c is not Barricade and not Blur);
}
