using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
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

namespace KolyaMod;

// Мутация «Призыватель»: враг зовёт себе подмогу — того же вида, что сам (крысы зовут крыс).
// Механика ванильная (по образцу TwoTailedRat.CallForBackup, разобрана зондом):
//   EncounterModel.GetNextSlot(combat) -> CreatureCmd.Add<T>(combat, slot) — действие само
//   идёт через сетевую очередь, в коопе сходится. Призванному вешается MinionPower (для «Связки»)
//   и, начиная с 4-го акта, ArtifactPower — иначе твари умирали от Злого рока до своего хода
//   (та самая причина, по которой «Призыв» вырезали в 0.3.21; теперь игрокам придётся
//   снимать артефакт или добивать призывателя, а не спамить рок).
// Ограничения: у каждого призывателя свой запас призывов и период; общий потолок числа врагов.
public static class Summoner
{
    public const int EnemyCap = 5;            // всего врагов в бою не больше
    public const int MaxSummonsNormal = 2;    // запас призывов у обычного врага
    public const int MaxSummonsBoss = 3;      // у элиты и босса

    class State { public int Turns; public int Summons; }
    static readonly ConditionalWeakTable<Creature, State> Summoners = new();

    public static void Mark(Creature c) => Summoners.GetOrCreateValue(c);

    static bool IsSummoner(Creature c) => Summoners.TryGetValue(c, out _);

    // считаем, что новый бой — свежие счётчики: ConditionalWeakTable чистится сборщиком,
    // но бой может держать ссылки — сбрасываем состояние при первом ходе призывателя
    static readonly ConditionalWeakTable<Creature, object> Reset = new();

    [HarmonyPatch(typeof(Hook), nameof(Hook.BeforeSideTurnStart))]
    static class SummonTurn
    {
        static void Postfix(ICombatState combatState, CombatSide side, IReadOnlyList<Creature> participants, ref Task __result)
        {
            if (side != CombatSide.Enemy) return;
            if (!participants.Any(IsSummoner)) return;
            __result = Do(__result, combatState, participants);
        }

        static async Task Do(Task before, ICombatState combat, IReadOnlyList<Creature> participants)
        {
            await before;
            int act = combat.RunState?.CurrentActIndex ?? 0;
            var room = combat.Encounter?.RoomType;
            foreach (var s in participants.Where(IsSummoner).Where(c => c.IsAlive && !c.HasPower<MinionPower>()).ToList())
            {
                var st = Summoners.GetOrCreateValue(s);
                // новый бой — сброс счётчиков (ConditionalWeakTable живёт дольше боя)
                if (!Reset.TryGetValue(s, out _))
                {
                    Reset.AddOrUpdate(s, true);
                    st.Turns = 0;
                    st.Summons = 0;
                }
                st.Turns++;
                int max = room == RoomType.Elite || room == RoomType.Boss ? MaxSummonsBoss : MaxSummonsNormal;
                if (st.Summons >= max) continue;
                int period = act < 3 ? 3 : 2;   // на ранних актах реже
                if (st.Turns % period != 0) continue;
                int enemies = combat.Enemies.Count(e => e.IsAlive);
                if (enemies >= EnemyCap) continue;

                var slot = combat.Encounter?.GetNextSlot(combat);
                if (string.IsNullOrEmpty(slot))
                {
                    Log.Info($"[kolya_mod] призыв: свободных слотов нет, {s.Monster?.Id} зовёт в пустую");
                    continue;
                }
                // тот же вид, что сам призыватель
                var type = s.Monster!.GetType();
                var add = typeof(CreatureCmd).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    .First(m => m.Name == "Add" && m.IsGenericMethodDefinition &&
                                m.GetParameters() is { Length: 2 } ps &&
                                ps[0].ParameterType.Name == "ICombatState" && ps[1].ParameterType == typeof(string));
                var task = (Task<Creature>)add.MakeGenericMethod(type).Invoke(null, new object?[] { combat, slot })!;
                var spawned = await task;
                st.Summons++;
                await PowerCmd.Apply<MinionPower>(new ThrowingPlayerChoiceContext(), spawned, 1, s, null);
                if (act >= 3)
                    await PowerCmd.Apply<ArtifactPower>(new ThrowingPlayerChoiceContext(), spawned, 1 + act / 4, s, null);
                Log.Info($"[kolya_mod] призыв: {s.Monster?.Id} призвал {type.Name} в слот {slot} (призыв {st.Summons}/{max}, акт {act + 1})");
            }
        }
    }
}
