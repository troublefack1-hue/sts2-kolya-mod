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

// Мутации врагов с 1-го акта (docs/mutations.md). Обычные враги и элита: 1 мутация на актах 1–3,
// 2 на 4–6, 3 на 7–9, 4 на 10-м. Боссы мутируют с 4-го акта: 1, с 7-го — 2, на 10-м — 3.
// Пул — тактические силы: они меняют способ победы (не тянуть бой, бить большими ударами,
// лечить/снимать), а не просто добавляют урон. Сила мутаций растёт с актом.
[HarmonyPatch(typeof(Hook), nameof(Hook.BeforeCombatStart))]
public static class Mutations
{
    // кого именно мутировали в этом бою — для награды; заполняется детерминированно,
    // в совместной игре у всех клиентов одинаково
    static readonly HashSet<Creature> MutatedEnemies = new();
    public static bool IsMutated(Creature c) => MutatedEnemies.Contains(c);

    // a — индекс акта (0-based); суммы растут с актом
    static readonly (string Name, Func<Creature, int, Task> Apply)[] All =
    {
        ("Прочный панцирь", (c, a) => Give<HardenedShellPower>(c, 20)),
        ("Живучесть",       (c, a) => Give<HardToKillPower>(c, 9)),
        ("Шипы",            (c, a) => Give<ThornsPower>(c, 5 + a)),
        ("Изворотливость",  (c, a) => Give<SlipperyPower>(c, 1)),
        ("Бешенство",       (c, a) => Give<EnragePower>(c, 2 + a / 3)),
        ("Ритуал",          (c, a) => Give<RitualPower>(c, 2 + a / 4)),
        ("Немезис",         (c, a) => Give<NemesisPower>(c, 1)),
        ("Артефакт",        (c, a) => Give<ArtifactPower>(c, 1 + a / 6)),
        ("Отравитель",      (c, a) => { Poisoner.Mark(c); return Task.CompletedTask; }),
        ("Регенерация",     (c, a) => Give<RegenPower>(c, 6 + a)),
        ("Баррикада",       (c, a) => Give<BarricadePower>(c, 1)),
    };

    static Task Give<T>(Creature c, decimal amount) where T : PowerModel =>
        PowerCmd.Apply<T>(new ThrowingPlayerChoiceContext(), c, amount, c, null);

    public static int CountFor(int actIndex, RoomType room)
    {
        // боссы мутируют только с 4-го акта; остальные — с 1-го
        if (room == RoomType.Boss)
            return actIndex < 3 ? 0 : actIndex < 6 ? 1 : actIndex < 9 ? 2 : 3;
        return actIndex < 3 ? 1 : actIndex < 6 ? 2 : actIndex < 9 ? 3 : 4;
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
        MutatedEnemies.Clear();
        if (count == 0) return;
        // игровой генератор забега: у всех игроков совместной игры мутации совпадут
        var rng = runState.Rng.Niche;
        foreach (var enemy in combat.Enemies.ToList())
        {
            foreach (var (name, apply) in All.OrderBy(_ => rng.NextInt()).Take(count))
            {
                await apply(enemy, act);
                MutatedEnemies.Add(enemy);
                Log.Info($"[kolya_mod] мутация {name} на {enemy.Monster?.Id} (акт {act + 1})");
            }
        }
    }
}
