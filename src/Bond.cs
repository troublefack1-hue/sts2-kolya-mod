using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace KolyaMod;

// «Связка» с 4-го акта: пока у врага-призывателя живы приспешники, половина любой его потери ОЗ
// (атаки, яд и другие эффекты игрока) поровну уходит приспешникам. Прямое убийство (Kill) не делится.
public static class Bond
{
    static readonly List<(Creature Owner, decimal Share)> Pending = new();

    static List<Creature> Minions(Creature owner) =>
        owner.CombatState?.Enemies.Where(e => e.IsAlive && e != owner && e.HasPower<MinionPower>()).ToList()
        ?? new List<Creature>();

    [HarmonyPatch(typeof(Creature), nameof(Creature.LoseHpInternal))]
    static class Split
    {
        static void Prefix(Creature __instance, ref decimal amount)
        {
            if (!__instance.IsMonster || __instance.HasPower<MinionPower>() || amount <= 1m) return;
            if ((__instance.CombatState?.RunState.CurrentActIndex ?? 0) < 3) return;
            if (Minions(__instance).Count == 0) return;
            if (new StackTrace().GetFrames().Any(f => f.GetMethod()?.DeclaringType?.Name.Contains("<Kill>") == true)) return;
            var share = decimal.Floor(amount / 2m);
            amount -= share;
            Pending.Add((__instance, share));
        }
    }

    [HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.Damage),
        typeof(PlayerChoiceContext), typeof(IEnumerable<Creature>), typeof(decimal), typeof(ValueProp), typeof(Creature), typeof(CardModel))]
    static class Flush
    {
        static void Postfix(PlayerChoiceContext choiceContext, ref Task<IEnumerable<DamageResult>> __result) =>
            __result = Run(__result, choiceContext);

        static async Task<IEnumerable<DamageResult>> Run(Task<IEnumerable<DamageResult>> before, PlayerChoiceContext ctx)
        {
            var results = await before;
            while (Pending.Count > 0)
            {
                var (owner, share) = Pending[0];
                Pending.RemoveAt(0);
                var minions = Minions(owner);
                if (minions.Count == 0) continue;
                var each = decimal.Floor(share / minions.Count);
                if (each <= 0m) continue;
                Log.Info($"[kolya_mod] связка: {owner.Monster?.Id} передал {share} урона {minions.Count} приспешникам по {each}");
                await CreatureCmd.Damage(ctx, minions, each, ValueProp.Unblockable | ValueProp.Unpowered, null, null);
            }
            return results;
        }
    }
}
