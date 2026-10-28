using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Runs;

namespace KolyaMod;

// Акты 1–3 ванильные; дальше сила врагов растёт на 1/7 за акт, и в акте 10 она ×2,0,
// то есть враги десятого акта (он всегда третьего типа, см. MoreActs) вдвое сильнее третьего.
public static class EnemyScaling
{
    public static decimal Multiplier(int actIndex) =>
        1m + System.Math.Clamp(actIndex - 2, 0, MoreActs.TotalActs - 3) / (decimal)(MoreActs.TotalActs - 3);
}

[HarmonyPatch(typeof(Creature), nameof(Creature.ScaleMonsterHpForMultiplayer))]
public static class EnemyHpScaling
{
    static void Postfix(Creature __instance, int actIndex)
    {
        var mult = EnemyScaling.Multiplier(actIndex);
        if (mult == 1m) return;
        __instance.SetMaxHpInternal(decimal.Round(__instance.MaxHp * mult));
        __instance.SetCurrentHpInternal(__instance.MaxHp);
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.ModifyDamage))]
public static class EnemyDamageScaling
{
    static void Postfix(ref decimal __result, IRunState runState, Creature? dealer)
    {
        if (dealer == null || !dealer.IsMonster) return;
        __result = decimal.Floor(__result * EnemyScaling.Multiplier(runState.CurrentActIndex));
    }
}
