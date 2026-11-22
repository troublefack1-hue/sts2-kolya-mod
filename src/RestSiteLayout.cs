using System.Linq;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace KolyaMod;

// Ванильный список костра рассчитан на четыре действия. С модулем их больше,
// поэтому компактно масштабируем весь блок вместе с областями нажатия.
[HarmonyPatch(typeof(NRestSiteRoom), "UpdateRestSiteOptions")]
public static class RestSiteLayout
{
    sealed class OriginalLayout
    {
        public Vector2 Position;
        public Vector2 Scale;
    }

    static readonly ConditionalWeakTable<Control, OriginalLayout> Original = new();

    static void Postfix(NRestSiteRoom __instance)
    {
        var container = AccessTools.Field(typeof(NRestSiteRoom), "_choicesContainer").GetValue(__instance) as Control;
        if (container == null) return;
        int count = container.GetChildren().OfType<NRestSiteButton>().Count();
        var original = Original.GetOrCreateValue(container);
        if (original.Scale == default)
        {
            original.Position = container.Position;
            original.Scale = container.Scale;
        }

        if (count <= 5)
        {
            container.Position = original.Position;
            container.Scale = original.Scale;
            return;
        }

        float factor = count >= 8 ? 0.68f : 0.78f;
        container.Scale = original.Scale * factor;
        container.Position = original.Position + new Vector2(0, count >= 8 ? -95 : -55);
    }
}
