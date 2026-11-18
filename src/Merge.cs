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

namespace KolyaMod;

// «Слияние разных карт» у костра (0.3.45): две ЛЮБЫЕ карты сливаются в одну.
// Правила Коли: результат — ПЕРВАЯ выбранная карта; она получает ВСЕ ЧИСЛОВЫЕ ПАРАМЕТРЫ
// второй (урон→урон, блок→блок, сила эффекта→сила эффекта, энергия→энергия, исцеление→
// исцеление — по типам DynamicVar), зачарования обеих объединяются (MultiEnchant,
// одинаковые складываются по силе). Цена итоговой карты = ценаА + ценаБ + 1 — защита от
// бесплатных сборок: две карты за 0 дают карту за 1.
// Ограничение движка: переносятся только ЧИСЛА (эффекты карт написаны в их коде —
// «возьми 2 карты», «наложи уязвимость» перенести нельзя). Убранная карта удаляется.
public sealed class MergeRestSiteOption : RestSiteOption
{
    public const string Id = "KOLYA_MERGE";

    public override string OptionId => Id;

    public override IEnumerable<string> AssetPaths => System.Array.Empty<string>();

    public override LocString Description => new("rest_site_ui",
        "OPTION_" + Id + (IsEnabled ? ".description" : ".descriptionDisabled"));

    public override bool IsEnabled => Owner.Deck.Cards.Count >= 2;

    public MergeRestSiteOption(Player owner) : base(owner) { }

    // тот ли это тип статов (PowerVar<T> сравниваем с учётом generic-аргумента)
    static bool SameKind(System.Type a, System.Type b)
    {
        if (a == b) return true;
        if (a.IsGenericType && b.IsGenericType)
            return a.GetGenericTypeDefinition() == b.GetGenericTypeDefinition()
                && a.GetGenericArguments().SequenceEqual(b.GetGenericArguments());
        return false;
    }

    public override async Task<bool> OnSelect()
    {
        var deck = Owner.Deck.Cards;
        var first = (await CardSelectCmd.FromDeckGeneric(Owner,
            new CardSelectorPrefs(new LocString("rest_site_ui", "KOLYA_MERGE_PROMPT_1"), 1) { Cancelable = true },
            _ => true)).FirstOrDefault();
        if (first == null) return false;
        var second = (await CardSelectCmd.FromDeckGeneric(Owner,
            new CardSelectorPrefs(new LocString("rest_site_ui", "KOLYA_MERGE_PROMPT_2"), 1) { Cancelable = true },
            c => c != first)).FirstOrDefault();
        if (second == null) return false;

        // число повторов второй карты (Меч-бумеранг и т.п. — «3 урона, 3 раза»): его суммарный
        // эффект = стат × повторы, поэтому урон/блок/исцеление переносим ПОВТОРЕННЫМИ
        decimal repeats = 1;
        foreach (var kv in second.DynamicVars)
            if (kv.Value.GetType() == typeof(RepeatVar) && kv.Value.BaseValue > 0)
                repeats = kv.Value.BaseValue;

        // все числовые параметры второй прибавляются к первой (по типам)
        var moved = new List<string>();
        foreach (var kv in second.DynamicVars)
        {
            var t = kv.Value.GetType();
            if (t == typeof(RepeatVar)) continue; // повторы — множитель, не переносим как стат
            var target = first.DynamicVars.FirstOrDefault(o => SameKind(o.Value.GetType(), t)).Value;
            if (target == null) continue;
            // у повторяемых эффектов переносим суммарный результат (3 по 3 → +9)
            bool repeated = repeats > 1 && (t == typeof(DamageVar) || t == typeof(BlockVar) || t == typeof(HealVar));
            var amount = kv.Value.BaseValue * (repeated ? repeats : 1);
            target.UpgradeValueBy(amount);
            moved.Add($"{kv.Value.Name}+{amount:0.#}{(repeated ? $" (×{repeats})" : "")}→{target.Name}");
        }

        // все зачарования второй переходят на первую; одинаковые складываются по силе
        foreach (var e in MultiEnchant.All(second).ToList())
        {
            var copy = ModelDb.GetById<EnchantmentModel>(e.Id).ToMutable();
            if (copy.CanEnchant(first)) CardCmd.Enchant(copy, first, e.Amount);
        }

        // цена = ценаА + ценаБ + 1 (минимум 1, даже за две бесплатные).
        // UpgradeBy меняет только ожидающую прибавку (Canonical её не видит), поэтому
        // ставим кастомную базовую цену — как после кузнечного улучшения.
        if (!first.EnergyCost.CostsX)
        {
            int cost = first.EnergyCost.Canonical + (second.EnergyCost.CostsX ? 0 : second.EnergyCost.Canonical) + 1;
            first.EnergyCost.SetCustomBaseCost(cost);
        }

        await CardPileCmd.RemoveFromDeck(second);
        Log.Info($"[kolya_mod] слияние разных: {first.Id} + {second.Id}: параметры [{string.Join(", ", moved)}], цена {first.EnergyCost.Canonical}, зачарования: {string.Join(", ", MultiEnchant.All(first).Select(e => $"{e.Id.Entry} {e.Amount}"))}");
        return true;
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
