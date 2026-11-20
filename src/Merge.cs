using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace KolyaMod;

// «Слияние разных карт» у костра: итогом остаётся первая карта, но при розыгрыше она
// последовательно выполняет собственный эффект и эффект сохранённой второй карты. Поэтому
// «3 урона 3 раза» остаётся именно тремя атаками, а не превращается в одну атаку на 9.
// Цена = ценаА + ценаБ + 1; X-карты исключены. Зачарования объединяются через MultiEnchant.
public sealed class MergeRestSiteOption : RestSiteOption
{
    public const string Id = "KOLYA_MERGE";

    public override string OptionId => Id;

    public override IEnumerable<string> AssetPaths => System.Array.Empty<string>();

    public override LocString Description => new("rest_site_ui",
        "OPTION_" + Id + (IsEnabled ? ".description" : ".descriptionDisabled"));

    public override bool IsEnabled => Owner.Deck.Cards.Count >= 2;

    public MergeRestSiteOption(Player owner) : base(owner) { }

    public override async Task<bool> OnSelect()
    {
        var first = (await CardSelectCmd.FromDeckGeneric(Owner,
            new CardSelectorPrefs(new LocString("rest_site_ui", "KOLYA_MERGE_PROMPT_1"), 1) { Cancelable = true },
            c => !c.EnergyCost.CostsX)).FirstOrDefault();
        if (first == null) return false;
        var second = (await CardSelectCmd.FromDeckGeneric(Owner,
            new CardSelectorPrefs(new LocString("rest_site_ui", "KOLYA_MERGE_PROMPT_2"), 1) { Cancelable = true },
            c => c != first && !c.EnergyCost.CostsX)).FirstOrDefault();
        if (second == null) return false;

        foreach (var e in MultiEnchant.All(second).ToList())
        {
            var copy = ModelDb.GetById<EnchantmentModel>(e.Id).ToMutable();
            if (copy.CanEnchant(first)) CardCmd.Enchant(copy, first, e.Amount);
        }

        int cost = first.EnergyCost.Canonical + second.EnergyCost.Canonical + 1;
        first.EnergyCost.SetCustomBaseCost(cost);

        await CardPileCmd.RemoveFromDeck(second);
        MergedCards.Mark(first, second);
        Log.Info($"[kolya_mod] слияние разных: {first.Id} затем {second.Id}, цена {first.EnergyCost.Canonical}, зачарования: {string.Join(", ", MultiEnchant.All(first).Select(e => $"{e.Id.Entry} {e.Amount}"))}");
        return true;
    }
}

// Связь «основа → вторая карта» живёт и в боевых копиях, и в сохранении. Вторая карта
// удалена из колоды, но её модель сохраняется целиком как вложенный SerializableCard.
public static class MergedCards
{
    public const string SaveKey = "KolyaMerged";
    static readonly Dictionary<CardModel, CardModel?> Parts = new();

    public static bool Is(CardModel card) => Parts.ContainsKey(card);
    public static CardModel? Second(CardModel card) => Parts.TryGetValue(card, out var second) ? second : null;

    public static void Mark(CardModel first, CardModel second)
    {
        second.Owner = first.Owner;
        Parts[first] = second;
    }

    // OnPlay переопределяется каждой картой, поэтому патчим реальные переопределения,
    // а не только базовый CardModel.OnPlay (его виртуальные вызовы обходят базовое тело).
    public static void PatchCardPlays(Harmony harmony)
    {
        foreach (var type in AccessTools.GetTypesFromAssembly(typeof(CardModel).Assembly))
        {
            if (!typeof(CardModel).IsAssignableFrom(type)) continue;
            var method = type.GetMethod("OnPlay",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly);
            if (method == null || method.IsAbstract) continue;
            harmony.Patch(method, postfix: new HarmonyMethod(typeof(MergedCards), nameof(AfterPlay)));
        }
    }

    static void AfterPlay(ref Task __result, CardModel __instance,
        MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceContext choiceContext,
        MegaCrit.Sts2.Core.Entities.Cards.CardPlay cardPlay)
    {
        var second = Second(__instance);
        if (second != null) __result = PlaySecond(__result, second, choiceContext, cardPlay);
    }

    static async Task PlaySecond(Task firstPlay, CardModel second,
        MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceContext choiceContext,
        MegaCrit.Sts2.Core.Entities.Cards.CardPlay firstPlayData)
    {
        await firstPlay;
        second.Owner = firstPlayData.Player;
        var secondPlay = new MegaCrit.Sts2.Core.Entities.Cards.CardPlay
        {
            Card = second,
            Player = firstPlayData.Player,
            Target = firstPlayData.Target,
            ResultPile = firstPlayData.ResultPile,
            Resources = firstPlayData.Resources,
            IsAutoPlay = firstPlayData.IsAutoPlay,
            PlayIndex = firstPlayData.PlayIndex,
            PlayCount = firstPlayData.PlayCount,
        };
        var onPlay = AccessTools.Method(second.GetType(), "OnPlay");
        await (Task)onPlay.Invoke(second, new object[] { choiceContext, secondPlay })!;
    }

    static void CloneParts(CardModel original, CardModel clone)
    {
        if (!Parts.TryGetValue(original, out var second)) return;
        if (second == null)
        {
            Parts[clone] = null; // обратная совместимость со старыми сейвами
            return;
        }
        var secondClone = second.CreateClone();
        secondClone.Owner = clone.Owner;
        Parts[clone] = secondClone;
    }

    [HarmonyPatch(typeof(CardModel), nameof(CardModel.CreateClone))]
    static class Clone
    {
        static void Postfix(CardModel __instance, CardModel __result) => CloneParts(__instance, __result);
    }

    [HarmonyPatch(typeof(CardModel), nameof(CardModel.CreateCloneForPlayer))]
    static class CloneForPlayer
    {
        static void Postfix(CardModel __instance, CardModel __result) => CloneParts(__instance, __result);
    }

    [HarmonyPatch(typeof(CardModel), nameof(CardModel.ToSerializable))]
    static class Save
    {
        static void Postfix(CardModel __instance, SerializableCard __result)
        {
            if (!Parts.TryGetValue(__instance, out var second)) return;
            __result.Props ??= new SavedProperties();
            if (second == null)
            {
                __result.Props.strings ??= new List<SavedProperties.SavedProperty<string>>();
                __result.Props.strings.Add(new SavedProperties.SavedProperty<string>(SaveKey, "1"));
                return;
            }
            __result.Props.cards ??= new List<SavedProperties.SavedProperty<SerializableCard>>();
            __result.Props.cards.Add(new SavedProperties.SavedProperty<SerializableCard>(SaveKey, second.ToSerializable()));
        }
    }

    [HarmonyPatch(typeof(CardModel), nameof(CardModel.FromSerializable))]
    static class Load
    {
        static void Postfix(SerializableCard save, CardModel __result)
        {
            var embedded = save.Props?.cards?.FirstOrDefault(s => s.name == SaveKey).value;
            if (embedded != null)
            {
                var second = CardModel.FromSerializable(embedded);
                second.Owner = __result.Owner;
                Parts[__result] = second;
                return;
            }
            var legacy = save.Props?.strings?.FirstOrDefault(s => s.name == SaveKey).value;
            if (legacy == "1") Parts[__result] = null;
        }
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.ModifyRestSiteOptions))]
public static class AddMergeOption
{
    static void Prefix(Player player, ICollection<RestSiteOption> options)
    {
        if (!options.OfType<MergeRestSiteOption>().Any()) options.Add(new MergeRestSiteOption(player));
    }
}

[HarmonyPatch(typeof(RestSiteOption), nameof(RestSiteOption.Icon), MethodType.Getter)]
public static class MergeIcon
{
    static Texture2D? _icon;

    static bool Prefix(RestSiteOption __instance, ref Texture2D __result)
    {
        if (__instance is not MergeRestSiteOption) return true;
        if (_icon == null)
        {
            var dir = Path.GetDirectoryName(typeof(MergeIcon).Assembly.Location)!;
            _icon = ImageTexture.CreateFromImage(Image.LoadFromFile(Path.Combine(dir, "option_merge.png")));
        }
        __result = _icon;
        return false;
    }
}
