using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;

namespace KolyaMod;

// «Обмен картами» у костра (только кооп): игрок, выбравший опцию, отдаёт одну свою карту
// следующему по кругу игроку (порядок фиксирован — детерминированно для всех клиентов).
// Если оба игрока выберут обмен, каждый отдаст и получит карту — полноценный обмен.
// Карта переносится целиком: уровни улучшений (+N), зачарования. Выбор — только своей
// колоды (как у кузнеца/слияния), перенос — через сетевые команды CardPileCmd: в совместной
// игре все клиенты сойдутся. Действие НЕ завершает костёр. Было: Task.WhenAll по выбору
// карт всех игроков сразу — выбор удалённого игрока не доходил до его клиента, обмен
// молча отменялся (жалоба «обмен всё ещё не работает»).
public sealed class TradeRestSiteOption : RestSiteOption
{
    public const string Id = "KOLYA_TRADE";

    public override string OptionId => Id;

    public override IEnumerable<string> AssetPaths => System.Array.Empty<string>();

    public override LocString Description => new("rest_site_ui",
        "OPTION_" + Id + (IsEnabled ? ".description" : ".descriptionDisabled"));

    public override bool IsEnabled => Owner.RunState.Players.Count() > 1;

    public TradeRestSiteOption(Player owner) : base(owner) { }

    public override async Task<bool> OnSelect()
    {
        var players = Owner.RunState.Players.ToList();
        if (players.Count < 2) return false;
        int me = players.FindIndex(p => p.NetId == Owner.NetId);
        if (me < 0) return false;
        var taker = players[(me + 1) % players.Count];
        var pick = await CardSelectCmd.FromDeckGeneric(Owner,
            new CardSelectorPrefs(new LocString("rest_site_ui", "KOLYA_TRADE_PROMPT"), 1) { Cancelable = true },
            _ => true);
        var card = pick.FirstOrDefault();
        if (card == null) return false;
        await CardPileCmd.RemoveFromDeck(card, false);
        card.Owner = taker;
        await CardPileCmd.Add(card, taker.Deck, CardPilePosition.Random, null, false);
        Log.Info($"[kolya_mod] обмен: {card.Id} от игрока {Owner.NetId} к игроку {taker.NetId} (+{card.CurrentUpgradeLevel})");
        return false; // костёр не завершается — остаются ковка и отдых
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.ModifyRestSiteOptions))]
public static class AddTradeOption
{
    static void Prefix(Player player, ICollection<RestSiteOption> options)
    {
        if (player.RunState.Players.Count() < 2) return; // только кооп
        if (!options.OfType<TradeRestSiteOption>().Any()) options.Add(new TradeRestSiteOption(player));
    }
}

[HarmonyPatch(typeof(RestSiteOption), nameof(RestSiteOption.Icon), MethodType.Getter)]
public static class TradeIcon
{
    static Texture2D? _icon;

    static bool Prefix(RestSiteOption __instance, ref Texture2D __result)
    {
        if (__instance is not TradeRestSiteOption) return true;
        if (_icon == null)
        {
            var dir = Path.GetDirectoryName(typeof(TradeIcon).Assembly.Location)!;
            _icon = ImageTexture.CreateFromImage(Image.LoadFromFile(Path.Combine(dir, "option_trade.png")));
        }
        __result = _icon;
        return false;
    }
}
