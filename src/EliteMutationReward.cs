using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace KolyaMod;

// Награда за мутированную элиту: выбрать карту колоды, она получает случайное безопасное зачарование.
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCombatVictory))]
public static class EliteMutationReward
{
    static void Postfix(IRunState runState, CombatRoom room, ref Task __result)
    {
        if (room.Encounter?.RoomType != RoomType.Elite) return;
        if (Mutations.CountFor(runState.CurrentActIndex, RoomType.Elite) == 0) return;
        __result = Reward(__result, runState);
    }

    static async Task Reward(Task before, IRunState runState)
    {
        await before;
        // выбор каждого игрока идёт через игровую синхронизацию; зачарования выдаются строго по порядку игроков,
        // чтобы общий генератор случайности у всех клиентов дал одно и то же
        var picks = await Task.WhenAll(runState.Players.Select(player => CardSelectCmd.FromDeckGeneric(player,
            new CardSelectorPrefs(new LocString("rest_site_ui", "KOLYA_ELITE_REWARD_PROMPT"), 1) { Cancelable = true },
            c => FifthUpgradeEnchant.Options(c).Count > 0)));
        foreach (var card in picks.Select(p => p.FirstOrDefault()).Where(c => c != null))
        {
            var pick = FifthUpgradeEnchant.GiveRandom(card!);
            Log.Info($"[kolya_mod] награда за мутированную элиту: {card!.Id} получает {pick?.Id}");
        }
    }
}
