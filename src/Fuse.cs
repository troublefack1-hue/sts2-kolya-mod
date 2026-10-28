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
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Runs;

namespace KolyaMod;

// «Слияние» у костра: две копии одной карты становятся одной. Уровни улучшений складываются и дают ещё +1,
// сила одинаковых зачарований складывается, зачарование второй карты переходит на пустую первую.
// Разные зачарования пока не складываются: первая карта оставляет своё (нужны несколько зачарований на карте).
public sealed class FuseRestSiteOption : RestSiteOption
{
    public const string Id = "KOLYA_FUSE";

    public override string OptionId => Id;

    public override IEnumerable<string> AssetPaths => System.Array.Empty<string>();

    public override LocString Description => new("rest_site_ui",
        "OPTION_" + Id + (IsEnabled ? ".description" : ".descriptionDisabled"));

    public override bool IsEnabled =>
        Owner.Deck.Cards.GroupBy(c => c.Id).Any(g => g.Count() > 1);

    public FuseRestSiteOption(Player owner) : base(owner) { }

    public override async Task<bool> OnSelect()
    {
        var deck = Owner.Deck.Cards;
        var first = (await CardSelectCmd.FromDeckGeneric(Owner,
            new CardSelectorPrefs(new LocString("rest_site_ui", "KOLYA_FUSE_PROMPT_1"), 1) { Cancelable = true },
            c => deck.Count(o => o.Id == c.Id) > 1)).FirstOrDefault();
        if (first == null) return false;
        var second = (await CardSelectCmd.FromDeckGeneric(Owner,
            new CardSelectorPrefs(new LocString("rest_site_ui", "KOLYA_FUSE_PROMPT_2"), 1) { Cancelable = true },
            c => c != first && c.Id == first.Id)).FirstOrDefault();
        if (second == null) return false;

        // само слияние даёт ещё один уровень: итог = уровень первой + уровень второй + 1
        for (int i = 0; i < second.CurrentUpgradeLevel + 1 && first.IsUpgradable; i++)
            CardCmd.Upgrade(first, CardPreviewStyle.None);
        var extra = second.Enchantment;
        if (extra != null)
        {
            if (first.Enchantment == null && extra.CanonicalInstance is EnchantmentModel canon)
                CardCmd.Enchant(canon.ToMutable(), first, extra.Amount);
            else if (first.Enchantment != null && first.Enchantment.Id == extra.Id)
                first.Enchantment.Amount += extra.Amount;
        }
        await CardPileCmd.RemoveFromDeck(second);
        Log.Info($"[kolya_mod] слияние: {first.Id} +{first.CurrentUpgradeLevel}, зачарование {first.Enchantment?.Id} {first.Enchantment?.Amount}");
        return true;
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.ModifyRestSiteOptions))]
public static class AddFuseOption
{
    static void Prefix(Player player, ICollection<RestSiteOption> options)
    {
        if (!options.OfType<FuseRestSiteOption>().Any()) options.Add(new FuseRestSiteOption(player));
    }
}

[HarmonyPatch(typeof(RestSiteOption), nameof(RestSiteOption.Icon), MethodType.Getter)]
public static class FuseIcon
{
    static Texture2D? _icon;

    static bool Prefix(RestSiteOption __instance, ref Texture2D __result)
    {
        if (__instance is not FuseRestSiteOption) return true;
        if (_icon == null)
        {
            var dir = Path.GetDirectoryName(typeof(FuseIcon).Assembly.Location)!;
            _icon = ImageTexture.CreateFromImage(Image.LoadFromFile(Path.Combine(dir, "option_fuse.png")));
        }
        __result = _icon;
        return false;
    }
}

[HarmonyPatch(typeof(LocTable))]
public static class FuseText
{
    static readonly Dictionary<string, string> Text = new()
    {
        ["OPTION_KOLYA_FUSE.name"] = "Слияние",
        ["OPTION_KOLYA_FUSE.description"] = "Слить две одинаковые карты в одну: улучшения складываются и дают ещё +1, зачарования складываются.",
        ["OPTION_KOLYA_FUSE.descriptionDisabled"] = "В колоде нет двух одинаковых карт.",
        ["KOLYA_FUSE_PROMPT_1"] = "Выберите карту для слияния",
        ["KOLYA_FUSE_PROMPT_2"] = "Выберите вторую такую же карту",
    };

    [HarmonyPrefix, HarmonyPatch(nameof(LocTable.GetRawText))]
    static bool Raw(string key, ref string __result) => !Text.TryGetValue(key, out __result!);

    [HarmonyPrefix, HarmonyPatch(nameof(LocTable.HasEntry))]
    static bool Has(string key, ref bool __result)
    {
        if (!Text.ContainsKey(key)) return true;
        __result = true;
        return false;
    }

    [HarmonyPrefix, HarmonyPatch(nameof(LocTable.GetLocString))]
    static bool Get(LocTable __instance, string key, ref LocString __result)
    {
        if (!Text.ContainsKey(key)) return true;
        __result = new LocString((string)AccessTools.Field(typeof(LocTable), "_name").GetValue(__instance)!, key);
        return false;
    }
}
