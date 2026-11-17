using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Runs;

namespace KolyaMod;

// «Передача карты» у костра (только кооп), v7 — односторонняя передача с согласием.
// Жалобы на v1: карта исчезала у передающего и не появлялась у получателя, согласия
// не было, опция безлимитна.
//
// Архитектура коопа StS2 (установлена раскопками IL и живым логом):
// - OnSelect опции костра исполняется У ВСЕХ клиентов детерминированно; синкаются
//   только сетевые ВЫБОРЫ (CardSelectCmd). Никаких локальных попапов внутри OnSelect!
// - Каждый клиент — авторитет своей колоды. RemoveFromDeck ЧУЖОЙ карты падает
//   ("Card X already has an owner" из RunState.RemoveCard/AddCard) — каждый правит
//   только свою колоду, изменения доезжают до напарника синхронизацией.
//
// Схема:
// 1) Инициатор А выбирает опцию, выбирает карту (сетевой выбор — ок), удаляет её
//    У СЕБЯ (IsMine-гейт). Это предложение.
// 2) Получатель Б: фоновый наблюдатель видит пропажу карты у А (зеркало колоды А
//    на машине Б обновляется синком) → всплывает окно «Напарник предлагает вам карту
//    "<карта>" (ур. N). Принять?».
// 3) «Принять» → Б кладёт копию карты в СВОЮ колоду (IsMine — рассылается).
// 4) А ждёт появления карты у получателя (45 с) → костёр истрачен. Таймаут →
//    возвращает карту себе.
// Выбор получателя (3+ игроков): отдельная опция костра на каждого напарника —
// выбор опции детерминирован (индекс синкается), имя получателя в описании.
public class TradeRestSiteOption : RestSiteOption
{
    public const string Id = "KOLYA_TRADE";
    const int WaitSeconds = 45;

    readonly Player? _target; // зашитый получатель (null — любой напарник, только 1в1)

    public ulong? TargetNetId => _target?.NetId;

    public override string OptionId => _target == null ? Id : Id + "_TO_" + _target.NetId;

    public override IEnumerable<string> AssetPaths => System.Array.Empty<string>();

    public override LocString Description => new LocString("rest_site_ui",
        "OPTION_" + (_target == null ? Id : "KOLYA_TRADE_TO") + (IsEnabled ? ".description" : ".descriptionDisabled"))
        .WithVar("name", _target?.Character.Title ?? LocString.GetIfExists("rest_site_ui", "KOLYA_TRADE_PARTNER"));

    public override bool IsEnabled => Owner.RunState.Players.Count() > 1;

    public TradeRestSiteOption(Player owner) : base(owner) { }

    public TradeRestSiteOption(Player owner, Player target) : base(owner)
    {
        _target = target;
    }

    public override async Task<bool> OnSelect()
    {
        var players = Owner.RunState.Players.ToList();
        if (players.Count < 2) return false;
        int me = players.FindIndex(p => p.NetId == Owner.NetId);
        if (me < 0) return false;
        var target = _target ?? players.First(p => p.NetId != Owner.NetId);

        // выбор карты — сетевой выбор, детерминирован у всех клиентов
        var myPick = await CardSelectCmd.FromDeckGeneric(Owner,
            new CardSelectorPrefs(new LocString("rest_site_ui", "KOLYA_TRADE_PROMPT"), 1) { Cancelable = true },
            _ => true);
        var mine = myPick.FirstOrDefault();
        if (mine == null) return false; // отмена — костёр не тратится

        // только авторитет владельца удаляет карту из своей колоды
        if (LocalContext.IsMine(mine))
        {
            await CardPileCmd.RemoveFromDeck(mine, false);
            Log.Info($"[kolya_mod] передача: {Owner.NetId} предложил {mine.Id} (+{mine.CurrentUpgradeLevel}) для {target.NetId}");
        }

        // ждём: получатель кладёт копию в свою колоду (у себя он IsMine — сработает)
        var before = Snap(target.Deck);
        var deadline = DateTime.UtcNow.AddSeconds(WaitSeconds);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(500);
            if (Appeared(Snap(target.Deck), before, mine.Id))
            {
                Log.Info($"[kolya_mod] передача: {target.NetId} принял {mine.Id}");
                return true; // костёр истрачен
            }
        }

        // таймаут: не ответил — возвращаем карту (только владелец, IsMine-гейт)
        if (LocalContext.IsMine(mine))
        {
            if (!Appeared(Snap(target.Deck), before, mine.Id))
            {
                await GiveCopyTo(mine.Id, mine.CurrentUpgradeLevel, Owner);
                Log.Info($"[kolya_mod] передача: {target.NetId} не ответил, {Owner.NetId} вернул {mine.Id}");
            }
        }
        return false;
    }

    // карта в снимке колоды
    internal readonly record struct CardSnap(ModelId Id, int Level);

    internal static List<CardSnap> Snap(CardPile deck) =>
        deck.Cards.Select(c => new CardSnap(c.Id, c.CurrentUpgradeLevel))
            .OrderBy(c => c.Id.Entry).ThenBy(c => c.Level).ToList();

    // первая карта из «было», которой стало меньше в «сейчас»
    internal static CardSnap? Missing(List<CardSnap> now, List<CardSnap> before)
    {
        var counts = now.GroupBy(c => c).ToDictionary(g => g.Key, g => g.Count());
        foreach (var b in before)
        {
            if (counts.TryGetValue(b, out int n) && n > 0)
                counts[b] = n - 1;
            else
                return b;
        }
        return null;
    }

    // появилась ли карта с заданным Id в «сейчас» по сравнению с «было»
    internal static bool Appeared(List<CardSnap> now, List<CardSnap> before, ModelId id)
    {
        int beforeCount = before.Count(c => c.Id.Equals(id));
        return now.Count(c => c.Id.Equals(id)) > beforeCount;
    }

    // копия карты в свою колоду по Id+уровню
    internal static async Task GiveCopyTo(ModelId id, int level, Player to)
    {
        var copy = ModelDb.GetById<CardModel>(id).ToMutable();
        while (copy.CurrentUpgradeLevel < level && copy.IsUpgradable)
            CardCmd.Upgrade(copy, CardPreviewStyle.None);
        await CardPileCmd.Add(copy, to.Deck, CardPilePosition.Random, null, false);
    }
}

// вспомогательный метод для LocString (Add возвращает void)
static class LocStringExt
{
    public static LocString WithVar(this LocString loc, string name, LocString value)
    {
        loc.Add(name, value);
        return loc;
    }
}

// Фоновое наблюдение за колодами напарников: обнаружение предложений передачи
// и показ окна принятия. Локально на каждой машине; UI — через CallDeferred.
public static class TradeOffers
{
    static System.Threading.Timer? _timer;
    static Player? _me; // последний локальный игрок у костра
    static readonly Dictionary<ulong, List<TradeRestSiteOption.CardSnap>> _snaps = new();
    static bool _busy; // окно предложения открыто

    public static void Ensure(Player me)
    {
        _me = me;
        _timer ??= new System.Threading.Timer(_ => Tick(), null, 1500, 500);
    }

    public static void Tick()
    {
        try
        {
            var me = _me;
            if (me == null || !RunManager.Instance.IsInProgress) { _snaps.Clear(); return; }
            var players = me.RunState.Players.ToList();
            if (players.Count < 2) { _snaps.Clear(); return; }
            ulong myId = me.NetId;

            foreach (var p in players)
            {
                if (p.NetId == myId) continue;
                var now = TradeRestSiteOption.Snap(p.Deck);
                if (!_snaps.TryGetValue(p.NetId, out var before))
                {
                    _snaps[p.NetId] = now;
                    continue;
                }
                var gone = TradeRestSiteOption.Missing(now, before);
                _snaps[p.NetId] = now;
                if (gone.HasValue && !_busy)
                {
                    _busy = true;
                    var from = p;
                    var offer = gone.Value;
                    OnMainThread(() => ShowOffer(from, offer));
                }
            }
        }
        catch (Exception e)
        {
            Log.Error($"[kolya_mod] передача: ошибка наблюдения: {e.GetType().Name}: {e.Message}");
        }
    }

    // окно да/нет; результат — через TCS (безопасно из любого потока)
    internal static Task<bool> Ask(LocString body, LocString header, LocString no, LocString yes)
    {
        var tcs = new TaskCompletionSource<bool>();
        var game = NGame.Instance;
        if (game == null) { tcs.SetResult(false); return tcs.Task; }
        Callable.From(() =>
        {
            try
            {
                var popup = NGenericPopup.Create();
                NModalContainer.Instance.Add(popup, true);
                _ = AwaitConfirm(popup, body, header, no, yes, tcs);
            }
            catch (Exception e)
            {
                Log.Error($"[kolya_mod] передача: ошибка окна: {e.GetType().Name}: {e.Message}");
                tcs.TrySetResult(false);
            }
        }).CallDeferred();
        return tcs.Task;
    }

    static async Task AwaitConfirm(NGenericPopup popup, LocString body, LocString header,
        LocString no, LocString yes, TaskCompletionSource<bool> tcs)
    {
        bool result = await popup.WaitForConfirmation(body, header, no, yes);
        tcs.TrySetResult(result);
    }

    // окно принятия у получателя — локально на его машине
    static async void ShowOffer(Player from, TradeRestSiteOption.CardSnap offer)
    {
        try
        {
            var me = _me;
            if (me == null) { _busy = false; return; }

            var cardLoc = ModelDb.GetById<CardModel>(offer.Id).TitleLocString;
            var body = new LocString("rest_site_ui", "KOLYA_TRADE_OFFER_BODY");
            body.Add("card", cardLoc);
            body.Add("level", (decimal)(offer.Level + 1));

            bool accepted = await Ask(body,
                new LocString("rest_site_ui", "KOLYA_TRADE_OFFER_HEADER"),
                new LocString("rest_site_ui", "KOLYA_TRADE_OFFER_NO"),
                new LocString("rest_site_ui", "KOLYA_TRADE_OFFER_YES"));

            if (!accepted)
            {
                Log.Info($"[kolya_mod] передача: {me.NetId} отказался от {offer.Id} ({from.NetId})");
                return;
            }

            // принимаем: кладём копию в СВОЮ колоду (мы авторитет своей колоды)
            await TradeRestSiteOption.GiveCopyTo(offer.Id, offer.Level, me);
            Log.Info($"[kolya_mod] передача: {me.NetId} принял {offer.Id} (+{offer.Level}) от {from.NetId}");
        }
        catch (Exception e)
        {
            Log.Error($"[kolya_mod] передача: ошибка принятия: {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    static void OnMainThread(Action a)
    {
        var game = NGame.Instance;
        if (game == null) { _busy = false; return; }
        Callable.From(() =>
        {
            try { a(); }
            catch (Exception e)
            {
                Log.Error($"[kolya_mod] передача: ошибка UI: {e.GetType().Name}: {e.Message}");
                _busy = false;
            }
        }).CallDeferred();
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.ModifyRestSiteOptions))]
public static class AddTradeOption
{
    static void Prefix(Player player, ICollection<RestSiteOption> options)
    {
        if (player.RunState.Players.Count() < 2) return; // только кооп
        TradeOffers.Ensure(player);
        var others = player.RunState.Players.Where(p => p.NetId != player.NetId).ToList();
        if (others.Count == 1)
        {
            if (!options.OfType<TradeRestSiteOption>().Any()) options.Add(new TradeRestSiteOption(player));
        }
        else
        {
            // 3+ игроков: опция на каждого напарника — выбор опции детерминирован
            foreach (var target in others)
                if (!options.OfType<TradeRestSiteOption>().Any(o => o.TargetIs(target)))
                    options.Add(new TradeRestSiteOption(player, target));
        }
    }
}

public static class TradeOptionTargetExt
{
    public static bool TargetIs(this TradeRestSiteOption opt, Player target)
        => opt.TargetNetId == target.NetId;
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
