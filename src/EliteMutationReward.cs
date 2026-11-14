using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace KolyaMod;

// Награда за мутированную элиту: каждый игрок получает КНОПКУ «Трофей мутации» на экране
// наград за бой ( EnchantReward ). Раньше открывалось отдельное окно выбора карты после боя —
// игроки не понимали, что это, и закрывали, теряя награду.
// Кнопки добавляются синхронно в постфиксе: AfterCombatVictory — async, экран наград
// показывается позже внутри исходной задачи, так что ExtraRewards успевают попасть в список.
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCombatVictory))]
public static class EliteMutationReward
{
    static void Postfix(IRunState runState, CombatRoom room, ref Task __result)
    {
        if (room.Encounter?.RoomType != RoomType.Elite) return;
        if (Mutations.CountFor(runState.CurrentActIndex, RoomType.Elite) == 0) return;
        foreach (var player in runState.Players)
            room.AddExtraReward(player, new EnchantReward(player));
    }
}
