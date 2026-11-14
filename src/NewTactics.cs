using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models.Powers;

namespace KolyaMod;

// Принципиально новые приёмы врагов (0.3.20) — ответ на «игроки сидят в броне до посинения».
// Три новые мутации (Mutations.cs):
//  • «Гниение» — в начале хода врага у каждого живого игрока сгорает часть блока.
//    Броня перестаёт быть бесплатной: копить её меж ходами невыгодно, надо либо добивать
//    врага, либо переживать без стены.
//  • «Порча» — ванильный Hex на игроков: карты в руке прокляты (afflict), игрок не видит,
//    что разыгрывает, пока не сыграл. Бьёт по колодам-черепахам мозгом, а не телом.
//  • 0.3.21: «Призыв» (SummonNextTurn) заменён на «Подавление» (Weak+Vulnerable на игроков) —
//    призываемые крипы умирали от Злого рока раньше, чем делали ход: бесплатный корм для игрока.
// Обе без генератора случайности — кооператив сходится.
public static class NewTactics
{
    static readonly ConditionalWeakTable<Creature, object> Corroded = new();

    public static void MarkCorrode(Creature c) => Corroded.AddOrUpdate(c, true);

    static bool IsCorroded(Creature c) => Corroded.TryGetValue(c, out _);

    // «Гниение»: начало хода стороны врагов — блок игроков тает
    [HarmonyPatch(typeof(Hook), nameof(Hook.BeforeSideTurnStart))]
    static class Corrode
    {
        static void Postfix(ICombatState combatState, CombatSide side, IReadOnlyList<Creature> participants, ref Task __result)
        {
            if (side != CombatSide.Enemy) return;
            if (!participants.Any(IsCorroded)) return;
            __result = Eat(__result, combatState);
        }

        static async Task Eat(Task before, ICombatState combat)
        {
            await before;
            int act = combat.RunState?.CurrentActIndex ?? 0;
            decimal amount = 2 + act / 2;   // 2 на 1-м акте … 6 на 10-м
            foreach (var p in combat.Players.Select(p => p.Creature).Where(c => c.IsAlive && c.Block > 0))
            {
                await CreatureCmd.LoseBlock(p, amount);
                Log.Info($"[kolya_mod] гниение: у игрока сгорело {amount} блока (акт {act + 1})");
            }
        }
    }
}
