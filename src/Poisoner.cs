using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace KolyaMod;

// Мутация «Отравитель»: каждая атака врага по игроку (даже в блок) накладывает яд.
// Яд: 1 на актах 1–3, 2 в 4-м акте, +1 за акт, 8 в 10-м.
public static class Poisoner
{
    static readonly ConditionalWeakTable<Creature, object> Marked = new();

    public static void Mark(Creature c) => Marked.AddOrUpdate(c, true);

    public static bool IsMarked(Creature c) => Marked.TryGetValue(c, out _);

    public static int PoisonFor(int actIndex) => System.Math.Max(actIndex - 1, 1);

    [HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.Damage),
        typeof(PlayerChoiceContext), typeof(IEnumerable<Creature>), typeof(decimal), typeof(ValueProp), typeof(Creature), typeof(CardModel))]
    static class OnHit
    {
        static void Postfix(PlayerChoiceContext choiceContext, IEnumerable<Creature> targets, ValueProp props, Creature? dealer,
            ref Task<IEnumerable<DamageResult>> __result)
        {
            if (dealer == null || !Marked.TryGetValue(dealer, out _) || props.HasFlag(ValueProp.Unpowered)) return;
            var players = targets.Where(t => t.IsPlayer).ToList();
            if (players.Count == 0) return;
            __result = Poison(__result, choiceContext, players, dealer);
        }

        static async Task<IEnumerable<DamageResult>> Poison(Task<IEnumerable<DamageResult>> before, PlayerChoiceContext ctx,
            List<Creature> players, Creature dealer)
        {
            var results = await before;
            int amount = PoisonFor(dealer.CombatState?.RunState.CurrentActIndex ?? 0);
            if (amount > 0 && dealer.IsAlive)
                foreach (var p in players.Where(p => p.IsAlive))
                    await PowerCmd.Apply<PoisonPower>(ctx, p, amount, dealer, null);
            return results;
        }
    }
}
