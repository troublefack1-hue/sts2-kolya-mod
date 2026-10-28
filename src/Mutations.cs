using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace KolyaMod;

// Мутации врагов с 4-го акта (docs/mutations.md). Обычные враги и элита: 1 мутация на актах 4–6,
// 2 на 7–9, 3 на 10-м. Боссы: по одной. Сила каждой мутации — как у родного монстра с этой силой.
[HarmonyPatch(typeof(Hook), nameof(Hook.BeforeCombatStart))]
public static class Mutations
{
    static readonly (string Name, Func<Creature, Task> Apply)[] All =
    {
        ("Прочный панцирь", c => Give<HardenedShellPower>(c, 20)),
        ("Живучесть",       c => Give<HardToKillPower>(c, 9)),
        ("Шипы",            c => Give<ThornsPower>(c, 5)),
        ("Изворотливость",  c => Give<SlipperyPower>(c, 1)),
        ("Бешенство",       c => Give<EnragePower>(c, 2)),
        ("Ритуал",          c => Give<RitualPower>(c, 2)),
        ("Немезис",         c => Give<NemesisPower>(c, 1)),
        ("Артефакт",        c => Give<ArtifactPower>(c, 1)),
        ("Отравитель",      c => { Poisoner.Mark(c); return Task.CompletedTask; }),
    };

    static Task Give<T>(Creature c, decimal amount) where T : PowerModel =>
        PowerCmd.Apply<T>(new ThrowingPlayerChoiceContext(), c, amount, c, null);

    public static int CountFor(int actIndex, RoomType room)
    {
        if (actIndex < 3) return 0;
        if (room == RoomType.Boss) return 1;
        return actIndex < 6 ? 1 : actIndex < 9 ? 2 : 3;
    }

    static void Postfix(IRunState runState, ICombatState? combatState, ref Task __result)
    {
        if (combatState?.Encounter == null) return;
        __result = Mutate(__result, runState, combatState);
    }

    static async Task Mutate(Task before, IRunState runState, ICombatState combat)
    {
        await before;
        int act = runState.CurrentActIndex;
        int count = CountFor(act, combat.Encounter!.RoomType);
        if (count == 0) return;
        // игровой генератор забега: у всех игроков совместной игры мутации совпадут
        var rng = runState.Rng.Niche;
        foreach (var enemy in combat.Enemies.ToList())
        {
            foreach (var (name, apply) in All.OrderBy(_ => rng.NextInt()).Take(count))
            {
                await apply(enemy);
                Log.Info($"[kolya_mod] мутация {name} на {enemy.Monster?.Id} (акт {act + 1})");
            }
        }
    }
}
