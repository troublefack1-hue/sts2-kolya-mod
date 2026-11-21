using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace KolyaMod;

// Игра сохраняет уровень улучшения, но не сохраняет произвольную базовую цену.
// Поэтому цену после слияния и услуги костра храним отдельным зарегистрированным полем.
public static class CustomCardCosts
{
    public const string SaveKey = "KolyaCustomCost";
    static readonly Dictionary<CardModel, int> Costs = new();

    public static void Set(CardModel card, int cost)
    {
        Costs[card] = cost;
        card.EnergyCost.SetCustomBaseCost(cost);
        card.EnergyCost.FinalizeUpgrade();
    }

    [HarmonyPatch(typeof(CardModel), nameof(CardModel.ToSerializable))]
    static class Save
    {
        static void Postfix(CardModel __instance, SerializableCard __result)
        {
            if (!Costs.TryGetValue(__instance, out var cost)) return;
            __result.Props ??= new SavedProperties();
            __result.Props.ints ??= new List<SavedProperties.SavedProperty<int>>();
            __result.Props.ints.Add(new SavedProperties.SavedProperty<int>(SaveKey, cost));
        }
    }

    [HarmonyPatch(typeof(CardModel), nameof(CardModel.FromSerializable))]
    static class Load
    {
        static void Postfix(SerializableCard save, CardModel __result)
        {
            var props = save.Props;
            if (props?.ints == null) return;
            var property = props.ints.FirstOrDefault(s => s.name == SaveKey);
            if (property.name != SaveKey) return;
            Set(__result, property.value);
        }
    }
}
