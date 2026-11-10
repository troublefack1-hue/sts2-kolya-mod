using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Singleton;
using MegaCrit.Sts2.Core.Runs;

namespace KolyaMod;

// Акты 1–3 ванильные; дальше сила врагов растёт с ускорением (степень 1,5): акт 4 ×1,11, 5 ×1,31,
// 6 ×1,57, 7 ×1,87, 8 ×2,22, 9 ×2,60, 10 ×3,0. Начало ощутимое, к концу рост круче.
// Враги десятого акта (он всегда третьего типа, см. MoreActs) втрое сильнее третьего.
public static class EnemyScaling
{
    public const decimal Top = 3m;

    public static decimal Multiplier(int actIndex) =>
        1m + (Top - 1m) * Square(System.Math.Clamp(actIndex - 2, 0, MoreActs.TotalActs - 3) / (decimal)(MoreActs.TotalActs - 3));

    // показатель 1,5: между ровным ростом и квадратом — начало не скучное, конец круче
    static decimal Square(decimal x) => (decimal)System.Math.Pow((double)x, 1.5);
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

// Кооператив: ванильная таблица масштабирования под число игроков знает только 3 акта
// и бросает ArgumentOutOfRangeException на 4-м. Для актов 4+ берём строку последнего
// ванильного акта; наш множитель (EnemyHpScaling/EnemyDamageScaling) применяется поверх.
[HarmonyPatch(typeof(MultiplayerScalingModel), nameof(MultiplayerScalingModel.GetMultiplayerScaling))]
public static class MultiplayerScalingActClamp
{
    static void Prefix(ref int actIndex) =>
        actIndex = System.Math.Clamp(actIndex, 0, ModelDb.ActsByIndex.Count - 1);
}
