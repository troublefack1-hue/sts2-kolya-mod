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
    static ICombatState? _combat;
    public static bool IsMutated(Creature c) => MutatedEnemies.Contains(c);

    // a — индекс акта (0-based); суммы растут с актом
    // Система семейств: мутации делятся на 4 семейства, на одного врага — не больше одной
    // мутации из семейства. Враг с 4 мутациями (10-й акт) гарантированно получает по одной
    // из каждого семейства — разнообразие и контр-игра вместо стака однотипных капов.
    enum Fam { Оборона, Контроль, Атака, Заражение }

    static readonly (string Name, Fam Family, Func<Creature, int, Task> Apply)[] All =
    {
        ("Прочный панцирь", Fam.Оборона,  (c, a) => Give<HardenedShellPower>(c, 20)),
        ("Живучесть",       Fam.Оборона,  (c, a) => Give<HardToKillPower>(c, 9)),
        ("Шипы",            Fam.Оборона,  (c, a) => Give<ThornsPower>(c, 5 + a)),
        ("Баррикада",       Fam.Оборона,  (c, a) => Give<BarricadePower>(c, 1)),
        ("Изворотливость",  Fam.Контроль, (c, a) => Give<SlipperyPower>(c, 1)),
        ("Немезис",         Fam.Контроль, (c, a) => Give<NemesisPower>(c, 1)),
        ("Артефакт",        Fam.Контроль, (c, a) => Give<ArtifactPower>(c, 1 + a / 6)),
        ("Бешенство",       Fam.Атака,    (c, a) => Give<EnragePower>(c, 2 + a / 3)),
        ("Ритуал",          Fam.Атака,    (c, a) => Give<RitualPower>(c, 2 + a / 4)),
        ("Отравитель",      Fam.Заражение,(c, a) => { Poisoner.Mark(c); return Task.CompletedTask; }),
        ("Регенерация",     Fam.Заражение,(c, a) => Give<RegenPower>(c, 6 + a)),
        // 0.3.20: принципиально новые приёмы — ответ на «сидят в броне» (NewTactics.cs)
        ("Гниение",         Fam.Заражение,(c, a) => { NewTactics.MarkCorrode(c); return Task.CompletedTask; }),
        ("Порча",           Fam.Контроль, (c, a) => HexPlayers(c)),
        ("Призыв",          Fam.Атака,    (c, a) => Give<SummonNextTurnPower>(c, 1)),
    };

    // «Порча»: ванильный Hex на каждого игрока — карты в руке прокляты (afflict)
    static async Task HexPlayers(Creature source)
    {
        var players = source.CombatState?.Players;
        if (players == null) return;
        foreach (var p in players)
        {
            await PowerCmd.Apply<HexPower>(new ThrowingPlayerChoiceContext(), p.Creature, 1, source, null);
            Log.Info($"[kolya_mod] порча: игрок {p.NetId} проклят ({source.Monster?.Id})");
        }
    }

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
        // хук BeforeCombatStart вызывается по разу на каждого игрока в коопе — чистим список
        // только при смене боя, а уже мутированных врагов пропускаем (иначе мутации
        // накладывались бы по кругу, по 2×/3× в зависимости от числа игроков)
        if (!ReferenceEquals(_combat, combat))
        {
            _combat = combat;
            MutatedEnemies.Clear();
        }
        if (count == 0) return;
        // игровой генератор забега: у всех игроков совместной игры мутации совпадут
        var rng = runState.Rng.Niche;
        bool bossFight = combat.Encounter.RoomType == RoomType.Boss;
        foreach (var enemy in combat.Enemies.ToList())
        {
            if (MutatedEnemies.Contains(enemy)) continue;
            // случайный выбор без возврата: одно семейство — одна мутация на врага
            var pool = All.ToList();
            // «Призыв» боссам не даём: подмога боссу ломает бой (NewTactics)
            if (bossFight) pool.RemoveAll(p => p.Name == "Призыв");
            var taken = new HashSet<Fam>();
            for (int i = 0; i < count && pool.Count > 0; i++)
            {
                var avail = pool.Where(p => !taken.Contains(p.Family)).ToList();
                if (avail.Count == 0) avail = pool;
                var pick = avail[rng.NextInt(avail.Count)];
                pool.Remove(pick);
                taken.Add(pick.Family);
                await pick.Apply(enemy, act);
                MutatedEnemies.Add(enemy);
                Log.Info($"[kolya_mod] мутация {pick.Name} на {enemy.Monster?.Id} (акт {act + 1})");
            }
        }
    }
}
