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

namespace KolyaMod;

// «Снизить стоимость» у костра (0.3.45): снизить цену карты на 1, но НЕ ниже 1, ценой
// 10 здоровья. Одна карта за отдых. Карты со стоимостью X и дешевле 2 не подходят —
// в окне выбора просто не показываются. Услуга недоступна, если здоровья не больше 10 —
// герой не должен умереть от услуги (после списания останется минимум 1 ХП).
public sealed class CostDownRestSiteOption : RestSiteOption
{
    public const string Id = "KOLYA_COSTDOWN";
    public const int HpCost = 10;

    public override string OptionId => Id;

    public override IEnumerable<string> AssetPaths => System.Array.Empty<string>();

    public override LocString Description => new("rest_site_ui",
        "OPTION_" + Id + (IsEnabled ? ".description" : ".descriptionDisabled"));

    static bool Fits(CardModel c) => !c.EnergyCost.CostsX && c.EnergyCost.Canonical >= 2;

    public override bool IsEnabled =>
        Owner.Creature.CurrentHp > HpCost && Owner.Deck.Cards.Any(Fits);

    public CostDownRestSiteOption(Player owner) : base(owner) { }

    public override async Task<bool> OnSelect()
    {
        var deck = Owner.Deck.Cards;
        var card = (await CardSelectCmd.FromDeckGeneric(Owner,
            new CardSelectorPrefs(new LocString("rest_site_ui", "KOLYA_COSTDOWN_PROMPT"), 1) { Cancelable = true },
            Fits)).FirstOrDefault();
        if (card == null) return false;

        int cost = card.EnergyCost.Canonical - 1; // не ниже 1: Fits пропускает только карты от 2
        card.EnergyCost.SetCustomBaseCost(cost);
        CreatureCmd.SetCurrentHp(Owner.Creature, Owner.Creature.CurrentHp - HpCost);
        Log.Info($"[kolya_mod] снижение стоимости: {card.Id} → {cost} за {HpCost} ХП (осталось {Owner.Creature.CurrentHp})");
        return true;
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.ModifyRestSiteOptions))]
public static class AddCostDownOption
{
    static void Prefix(Player player, ICollection<RestSiteOption> options)
    {
        if (!options.OfType<CostDownRestSiteOption>().Any()) options.Add(new CostDownRestSiteOption(player));
    }
}

[HarmonyPatch(typeof(RestSiteOption), nameof(RestSiteOption.Icon), MethodType.Getter)]
public static class CostDownIcon
{
    static Texture2D? _icon;

    static bool Prefix(RestSiteOption __instance, ref Texture2D __result)
    {
        if (__instance is not CostDownRestSiteOption) return true;
        if (_icon == null)
        {
            var dir = Path.GetDirectoryName(typeof(CostDownIcon).Assembly.Location)!;
            _icon = ImageTexture.CreateFromImage(Image.LoadFromFile(Path.Combine(dir, "option_costdown.png")));
        }
        __result = _icon;
        return false;
    }
}
