using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models.Powers;

namespace KolyaMod;

// «Прочный панцирь» больше не вечен: каждый ход, в котором по врагу нанесли больше урона,
// чем пропускает панцирь (20), панцирь получает трещину. Три трещины — панцирь рассыпается,
// враг получает Уязвимость 2. Контр-игра: сфокусированный большой урон пробивает защиту
// за ~3 хода, а не капает под ней бессрочно. ДоТ панцирь по-прежнему игнорирует (и это
// осознанно: тихий путь обхода защиты), но и трещин не даёт.
public static class ShellCrack
{
    public const int CracksToBreak = 3;

    sealed class State { public int Count; }

    static readonly ConditionalWeakTable<Creature, State> Cracks = new();

    // кап сработал — реальный урон меньше заявленного: трещина
    [HarmonyPatch(typeof(HardenedShellPower), nameof(HardenedShellPower.ModifyHpLostBeforeOstyLate))]
    static class OnClamp
    {
        static void Postfix(Creature target, decimal amount, ref decimal __result)
        {
            if (__result >= amount) return;
            Cracks.GetValue(target, _ => new State()).Count++;
        }
    }

    // разрушение — в начале хода владельца, внутри командного потока (синхронно у всех клиентов)
    [HarmonyPatch(typeof(HardenedShellPower), nameof(HardenedShellPower.BeforeSideTurnStart))]
    static class OnTurnStart
    {
        static void Postfix(HardenedShellPower __instance, ref Task __result)
        {
            var owner = __instance.Owner;
            if (!Cracks.TryGetValue(owner, out var st) || st.Count < CracksToBreak) return;
            __result = Break(__result, __instance, owner, st);
        }

        static async Task Break(Task before, HardenedShellPower shell, Creature owner, State st)
        {
            await before;
            Cracks.Remove(owner);
            await PowerCmd.Remove(shell);
            if (owner.IsAlive)
            {
                await PowerCmd.Apply<VulnerablePower>(new ThrowingPlayerChoiceContext(), owner, 2, owner, null);
                Log.Info($"[kolya_mod] панцирь разрушен на {owner.Monster?.Id}, +Уязвимость 2");
            }
        }
    }
}
