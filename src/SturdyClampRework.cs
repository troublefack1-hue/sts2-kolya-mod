using System.Collections.Generic;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.ValueProps;

namespace KolyaMod;

// «Крепкий зажим» срезал блок до 10 и ломал вечный блок. Теперь он не трогает сброс блока,
// а даёт 5 блока в начале каждого хода игрока.
[HarmonyPatch(typeof(SturdyClamp))]
public static class SturdyClampRework
{
    public const int BlockPerTurn = 5;

    [HarmonyPrefix, HarmonyPatch(nameof(SturdyClamp.ShouldClearBlock))]
    static bool NoBlockHold(ref bool __result)
    {
        __result = true;
        return false;
    }

    [HarmonyPrefix, HarmonyPatch(nameof(SturdyClamp.AfterPreventingBlockClear))]
    static bool NoBlockTrim(ref Task __result)
    {
        __result = Task.CompletedTask;
        return false;
    }

    [HarmonyPrefix, HarmonyPatch("CanonicalVars", MethodType.Getter)]
    static bool Vars(ref IEnumerable<DynamicVar> __result)
    {
        __result = new DynamicVar[] { new BlockVar(BlockPerTurn, ValueProp.Unpowered) };
        return false;
    }
}

[HarmonyPatch(typeof(AbstractModel), nameof(AbstractModel.AfterPlayerTurnStart))]
public static class SturdyClampTurnBlock
{
    static void Postfix(AbstractModel __instance, Player player, ref Task __result)
    {
        if (__instance is SturdyClamp clamp && clamp.Owner == player)
            __result = Gain(__result, clamp, player.Creature);
    }

    static async Task Gain(Task before, SturdyClamp clamp, Creature creature)
    {
        await before;
        clamp.Flash();
        await CreatureCmd.GainBlock(creature, clamp.DynamicVars.Block, null);
    }
}
