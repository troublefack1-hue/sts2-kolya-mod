using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace KolyaMod;

// Награда за мутированных врагов на актах 1–3: фиксированное золото за каждого убитого
// (10/15/20 за акт 1/2/3) каждому игроку. Суммы без генератора случайности —
// в совместной игре все клиенты выдадут одно и то же.
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCombatVictory))]
public static class MutationBounty
{
    static void Postfix(IRunState runState, ICombatState combatState, CombatRoom room, ref Task __result)
    {
        int act = runState.CurrentActIndex;
        if (act >= 3 || room.Encounter?.RoomType != RoomType.Monster) return;
        __result = Reward(__result, runState, combatState, act);
    }

    static async Task Reward(Task before, IRunState runState, ICombatState combat, int act)
    {
        await before;
        int count = combat.Enemies.Count(Mutations.IsMutated);
        if (count == 0) return;
        decimal gold = count * (10 + 5 * act);
        foreach (var player in runState.Players)
        {
            await PlayerCmd.GainGold(gold, player, false);
            Log.Info($"[kolya_mod] награда за мутантов: {count} врагов, +{gold} зол. игроку {player.NetId} (акт {act + 1})");
        }
    }
}
