using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Timeline;
using MegaCrit.Sts2.Core.Nodes.Screens.Timeline;
using MegaCrit.Sts2.Core.Saves;

namespace KolyaMod;

// Перед показом летописи открываем все эпохи так же, как отладочная команда «unlock epochs».
[HarmonyPatch(typeof(NTimelineScreen), nameof(NTimelineScreen._Ready))]
public static class AllEpochs
{
    static void Prefix()
    {
        var revealed = SaveManager.Instance.Progress.Epochs
            .Where(e => e.State == EpochState.Revealed).Select(e => e.Id);
        var toReveal = EpochModel.AllEpochIds.Except(revealed).ToList();
        foreach (var id in toReveal)
            SaveManager.Instance.ObtainEpochOverride(id, EpochState.Revealed);
        Log.Info($"[kolya_mod] открыто эпох летописи: {toReveal.Count}");
    }
}
