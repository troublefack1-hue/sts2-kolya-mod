using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.Runs.History;
using MegaCrit.Sts2.Core.ValueProps;

namespace KolyaMod;

// Несколько зачарований на одной карте. Игра знает только одно поле card.Enchantment, поэтому туда
// ставится составное зачарование: оно хранит список настоящих и передаёт каждому все вызовы игры.
// Урон и блок: прибавки складываются, множители перемножаются; повторы и хуки идут цепочкой.
public sealed class KolyaMultiEnchantment : EnchantmentModel
{
    public List<EnchantmentModel> Children { get; private set; } = new();

    public override bool CanEnchantCardType(CardType cardType) => true;
    public override bool CanEnchant(CardModel card) => true;
    public override bool ShouldGlowGold => Children.Any(c => c.ShouldGlowGold);
    public override bool ShouldGlowRed => Children.Any(c => c.ShouldGlowRed);
    public override bool ShouldStartAtBottomOfDrawPile => Children.Any(c => c.ShouldStartAtBottomOfDrawPile);

    protected override void DeepCloneFields()
    {
        base.DeepCloneFields();
        Children = Children.Select(c => (EnchantmentModel)c.ClonePreservingMutability()).ToList();
    }

    protected override void OnEnchant()
    {
        foreach (var c in Children) c.ModifyCard();
    }

    public override void RecalculateValues()
    {
        foreach (var c in Children) c.RecalculateValues();
    }

    public override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay? cardPlay)
    {
        foreach (var c in Children.ToList()) await c.OnPlay(choiceContext, cardPlay);
    }

    public override decimal EnchantDamageAdditive(decimal originalDamage, ValueProp props) =>
        Children.Sum(c => c.EnchantDamageAdditive(originalDamage, props));

    public override decimal EnchantDamageMultiplicative(decimal originalDamage, ValueProp props) =>
        Children.Aggregate(1m, (m, c) => m * c.EnchantDamageMultiplicative(originalDamage, props));

    public override decimal EnchantBlockAdditive(decimal originalBlock) =>
        Children.Sum(c => c.EnchantBlockAdditive(originalBlock));

    public override decimal EnchantBlockMultiplicative(decimal originalBlock) =>
        Children.Aggregate(1m, (m, c) => m * c.EnchantBlockMultiplicative(originalBlock));

    public override int EnchantPlayCount(int originalPlayCount) =>
        Children.Aggregate(originalPlayCount, (n, c) => c.EnchantPlayCount(n));

    public override async Task AfterCardPlayed(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        foreach (var c in Children.ToList()) await c.AfterCardPlayed(choiceContext, cardPlay);
    }

    public override async Task AfterCardDrawn(PlayerChoiceContext choiceContext, CardModel card, bool fromHandDraw)
    {
        foreach (var c in Children.ToList()) await c.AfterCardDrawn(choiceContext, card, fromHandDraw);
    }

    public override async Task BeforeFlush(PlayerChoiceContext choiceContext, Player player)
    {
        foreach (var c in Children.ToList()) await c.BeforeFlush(choiceContext, player);
    }

    public override async Task AfterAutoPrePlayPhaseEntered(PlayerChoiceContext choiceContext, Player player)
    {
        foreach (var c in Children.ToList()) await c.AfterAutoPrePlayPhaseEntered(choiceContext, player);
    }

    public override void ModifyShuffleOrder(Player player, List<CardModel> cards, bool isInitialShuffle)
    {
        foreach (var c in Children) c.ModifyShuffleOrder(player, cards, isInitialShuffle);
    }
}

public static class MultiEnchant
{
    public const string SaveKey = "KolyaExtraEnchantments";

    static readonly MethodInfo EnchantmentSetter = AccessTools.PropertySetter(typeof(CardModel), nameof(CardModel.Enchantment));
    static readonly FieldInfo EnchantmentChanged = AccessTools.Field(typeof(CardModel), nameof(CardModel.EnchantmentChanged));

    public static IReadOnlyList<EnchantmentModel> All(CardModel card) => card.Enchantment switch
    {
        null => Array.Empty<EnchantmentModel>(),
        KolyaMultiEnchantment m => m.Children,
        var e => new[] { e },
    };

    // Добавить зачарование к уже зачарованной карте. modify=false, если значения карты уже учтены (загрузка — нет).
    public static void Add(CardModel card, EnchantmentModel enchantment, decimal amount, bool modify = true)
    {
        var same = All(card).FirstOrDefault(c => c.GetType() == enchantment.GetType());
        if (same != null)
        {
            same.Amount += (int)amount;
            if (modify) same.ModifyCard();
            Changed(card);
            return;
        }
        var multi = card.Enchantment as KolyaMultiEnchantment;
        if (multi == null)
        {
            var old = card.Enchantment!;
            EnchantmentSetter.Invoke(card, new object?[] { null });
            old.ClearInternal();
            multi = (KolyaMultiEnchantment)ModelDb.Enchantment<KolyaMultiEnchantment>().ToMutable();
            multi.Children.Add(old);
            EnchantmentSetter.Invoke(card, new object[] { multi });
            multi.ApplyInternal(card, 1);
        }
        multi.Children.Add(enchantment);
        enchantment.ApplyInternal(card, amount);
        if (modify) enchantment.ModifyCard();
        Changed(card);
    }

    static void Changed(CardModel card) => (EnchantmentChanged.GetValue(card) as Action)?.Invoke();

    // Составное зачарование заново привязывает вложенные к карте (после клонирования карты в бою).
    [HarmonyPatch(typeof(EnchantmentModel), nameof(EnchantmentModel.ApplyInternal))]
    static class Rebind
    {
        static void Postfix(EnchantmentModel __instance, CardModel card)
        {
            if (__instance is not KolyaMultiEnchantment m) return;
            foreach (var c in m.Children)
            {
                if (c.Card == card) continue;
                c.ClearInternal();
                c.ApplyInternal(card, c.Amount);
            }
        }
    }

    // Уже зачарованную карту можно зачаровать снова: проверка игры смотрит на карту как на незачарованную.
    [HarmonyPatch(typeof(EnchantmentModel), nameof(EnchantmentModel.CanEnchant))]
    static class AllowSecond
    {
        static readonly FieldInfo Backing = AccessTools.Field(typeof(CardModel), "<Enchantment>k__BackingField");

        static void Prefix(EnchantmentModel __instance, CardModel card, out EnchantmentModel? __state)
        {
            __state = __instance is KolyaMultiEnchantment ? null : card.Enchantment;
            if (__state != null) Backing.SetValue(card, null);
        }

        static Exception? Finalizer(CardModel card, EnchantmentModel? __state, Exception? __exception)
        {
            if (__state != null) Backing.SetValue(card, __state);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(CardCmd), nameof(CardCmd.Enchant), typeof(EnchantmentModel), typeof(CardModel), typeof(decimal))]
    static class EnchantMore
    {
        static bool Prefix(EnchantmentModel enchantment, CardModel card, decimal amount, ref EnchantmentModel? __result)
        {
            if (card.Enchantment == null) return true;
            enchantment.AssertMutable();
            if (!enchantment.CanEnchant(card))
                throw new InvalidOperationException($"Cannot enchant {card.Id} with {enchantment.Id}.");
            Add(card, enchantment, amount);
            card.FinalizeUpgradeInternal();
            if (card.Pile?.Type == PileType.Deck)
                card.Owner.RunState.CurrentMapPointHistoryEntry?.GetEntry(card.Owner.NetId).CardsEnchanted
                    .Add(new CardEnchantmentHistoryEntry(card, enchantment.Id));
            Log.Info($"[kolya_mod] зачарований на {card.Id}: {string.Join(", ", All(card).Select(e => $"{e.Id.Entry} {e.Amount}"))}");
            __result = card.Enchantment;
            return false;
        }
    }

    // Подсказка и значок: каждое вложенное зачарование своей подсказкой, значок — первого.
    [HarmonyPatch(typeof(EnchantmentModel), nameof(EnchantmentModel.HoverTips), MethodType.Getter)]
    static class Tips
    {
        static bool Prefix(EnchantmentModel __instance, ref IEnumerable<IHoverTip> __result)
        {
            if (__instance is not KolyaMultiEnchantment m) return true;
            __result = m.Children.SelectMany(c => c.HoverTips).ToList();
            return false;
        }
    }

    [HarmonyPatch(typeof(EnchantmentModel), nameof(EnchantmentModel.IconPath), MethodType.Getter)]
    static class Icon
    {
        static bool Prefix(EnchantmentModel __instance, ref string __result)
        {
            if (__instance is not KolyaMultiEnchantment m || m.Children.Count == 0) return true;
            __result = m.Children[0].IconPath;
            return false;
        }
    }

    // Сохранение: первое зачарование — в родное поле, остальные — строкой в свойства карты.
    [HarmonyPatch(typeof(CardModel), nameof(CardModel.ToSerializable))]
    static class Save
    {
        static void Postfix(CardModel __instance, SerializableCard __result)
        {
            if (__instance.Enchantment is not KolyaMultiEnchantment m || m.Children.Count == 0) return;
            __result.Enchantment = m.Children[0].ToSerializable();
            var extra = string.Join(";", m.Children.Skip(1).Select(c => $"{c.Id.Category}|{c.Id.Entry}|{c.Amount}"));
            __result.Props ??= new SavedProperties();
            __result.Props.strings ??= new List<SavedProperties.SavedProperty<string>>();
            __result.Props.strings.Add(new SavedProperties.SavedProperty<string>(SaveKey, extra));
        }
    }

    [HarmonyPatch(typeof(CardModel), nameof(CardModel.FromSerializable))]
    static class Load
    {
        static void Postfix(SerializableCard save, CardModel __result)
        {
            var extra = save.Props?.strings?.FirstOrDefault(s => s.name == SaveKey).value;
            if (string.IsNullOrEmpty(extra)) return;
            foreach (var item in extra.Split(';'))
            {
                var p = item.Split('|');
                if (p.Length != 3) continue;
                var e = SaveUtil.EnchantmentOrDeprecated(new ModelId(p[0], p[1])).ToMutable();
                Add(__result, e, int.Parse(p[2]));
            }
        }
    }
}
