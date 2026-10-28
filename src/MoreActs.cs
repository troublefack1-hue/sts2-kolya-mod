using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Unlocks;

namespace KolyaMod;

// Забег из 10 актов вместо 3: после третьего акты идут по кругу 1→2→3,
// на каждом шаге случайно выбирается обычный или альтернативный акт этого индекса.
[HarmonyPatch(typeof(ActModel), nameof(ActModel.GetRandomList))]
public static class MoreActs
{
    public const int TotalActs = 10;

    static IEnumerable<ActModel> Postfix(IEnumerable<ActModel> __result, Rng rng, UnlockState unlockState)
    {
        var acts = __result.ToList();
        var byIndex = ModelDb.ActsByIndex;
        for (int i = acts.Count; i < TotalActs; i++)
        {
            // последний акт — всегда третьего типа, финальный босс
            int index = i == TotalActs - 1 ? byIndex.Count - 1 : i % byIndex.Count;
            var pool = byIndex[index].Where(a => a.IsUnlocked(unlockState)).ToList();
            acts.Add(rng.NextItem(pool) ?? acts[index]);
        }
        Log.Info($"[kolya_mod] актов в забеге: {acts.Count}");
        return acts;
    }
}
