using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace KolyaMod;

// Улучшение реликвий. Победа в бою, где был хоть один мутированный враг, улучшает одну
// случайную реликвию каждого игрока: +1 ко всем её числовым параметрам. Реликвии хранят
// свои числа в DynamicVars (эффекты читают BaseValue при срабатывании — проверено на
// Ваджре и Якоре), поэтому рост работает для любой реликвии с числами. Уровень пишется
// в Props сохранения и переприменяется при загрузке. Выбор случайной реликвии идёт через
// общий генератор забега — в совместной игре все клиенты улучшат одну и ту же.
public static class RelicUpgrade
{
    public const string SaveKey = "KolyaRelicUpgrade";
    public const decimal Amount = 1m;

    // текущий уровень реликвии в этом забеге (не клонируется вместе с реликвией)
    sealed class LevelBox { public int Value; }
    static readonly ConditionalWeakTable<RelicModel, LevelBox> Levels = new();

    public static int LevelOf(RelicModel relic) => Levels.TryGetValue(relic, out var box) ? box.Value : 0;

    static bool Upgradeable(RelicModel relic) => relic.DynamicVars.Count > 0;

    public static void Apply(RelicModel relic, int times)
    {
        var box = Levels.GetOrCreateValue(relic);
        foreach (var kv in relic.DynamicVars)
            for (int i = 0; i < times; i++)
                kv.Value.UpgradeValueBy(Amount);
        box.Value += times;
        relic.Flash();
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterCombatVictory))]
    static class Victory
    {
        static void Postfix(IRunState runState, ICombatState combatState, CombatRoom room, ref Task __result)
        {
            if (combatState?.Encounter == null) return;
            if (!combatState.Enemies.Any(Mutations.IsMutated)) return;
            __result = Reward(__result, runState);
        }

        static async Task Reward(Task before, IRunState runState)
        {
            await before;
            var rng = runState.Rng.Niche;
            foreach (var player in runState.Players)
            {
                var relics = player.Relics.Where(Upgradeable).OrderBy(r => r.Id.Entry).ToList();
                if (relics.Count == 0) continue;
                var relic = relics.OrderBy(_ => rng.NextInt()).First();
                Apply(relic, 1);
                Log.Info($"[kolya_mod] реликвия улучшена: {relic.Id} у игрока {player.NetId} (уровень {LevelOf(relic)})");
            }
        }
    }

    [HarmonyPatch(typeof(RelicModel), nameof(RelicModel.ToSerializable))]
    static class Save
    {
        static void Postfix(RelicModel __instance, SerializableRelic __result)
        {
            int level = LevelOf(__instance);
            if (level <= 0) return;
            __result.Props ??= new SavedProperties();
            __result.Props.strings ??= new System.Collections.Generic.List<SavedProperties.SavedProperty<string>>();
            __result.Props.strings.Add(new SavedProperties.SavedProperty<string>(SaveKey, level.ToString()));
        }
    }

    [HarmonyPatch(typeof(RelicModel), nameof(RelicModel.FromSerializable))]
    static class Load
    {
        static void Postfix(SerializableRelic save, RelicModel __result)
        {
            var raw = save.Props?.strings?.FirstOrDefault(s => s.name == SaveKey).value;
            if (string.IsNullOrEmpty(raw) || !int.TryParse(raw, out int level) || level <= 0) return;
            Apply(__result, level);
            Log.Info($"[kolya_mod] реликвия {__result.Id} загружена с уровнем {level}");
        }
    }
}
