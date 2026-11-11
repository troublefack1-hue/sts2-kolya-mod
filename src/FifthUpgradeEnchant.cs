using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Nodes.CommonUi;

namespace KolyaMod;

// На 5-м улучшении карта получает вдобавок случайное подходящее зачарование силы 1, которого на ней ещё нет
// (как в событиях игры). В пул не входят зачарования, которые могут навредить карте:
// Зигзаг, Чудовищная, Вязкая, Знаковая, Своё место, Королевская печать (и ломающие: Удвоение, Уголёк).
[HarmonyPatch(typeof(CardCmd), nameof(CardCmd.Upgrade), typeof(IEnumerable<CardModel>), typeof(CardPreviewStyle))]
public static class FifthUpgradeEnchant
{
    public const int Level = 5;

    public static readonly Type[] Pool =
    {
        typeof(Adroit), typeof(Glam), typeof(Inky), typeof(Instinct), typeof(Momentum),
        typeof(Nimble), typeof(Sharp), typeof(SlumberingEssence),
        typeof(SoulsPower), typeof(Sown), typeof(Spiral), typeof(Steady), typeof(Swift), typeof(Vigorous),
    };

    static void Postfix(IEnumerable<CardModel> cards)
    {
        foreach (var card in cards.ToList())
        {
            if (card.CurrentUpgradeLevel != Level) continue;
            var pick = GiveRandom(card);
            if (pick != null) Log.Info($"[kolya_mod] 5-е улучшение {card.Id}: зачарование {pick.Id}");
        }
    }

    public static List<EnchantmentModel> Options(CardModel card)
    {
        var have = MultiEnchant.All(card).Select(e => e.GetType()).ToHashSet();
        // Inky вешает Слабость на цель карты — картам без вражеской цели она не подходит:
        // при розыгрыше такой карты Target == null и игра падает с NullReferenceException
        // (поймано на «Превращении»). Остальные зачарования пула либо без таргета, либо
        // проверяют IsPoweredAttack сами.
        bool canHitEnemy = card.TargetType is TargetType.AnyEnemy or TargetType.AllEnemies or TargetType.RandomEnemy;
        return ModelDb.DebugEnchantments
            .Where(e => Pool.Contains(e.GetType()) && !have.Contains(e.GetType()) && e.CanEnchant(card))
            .Where(e => e is Inky ? canHitEnemy : true)
            .OrderBy(e => e.Id.Entry, StringComparer.Ordinal).ToList();
    }

    // Случайное безопасное зачарование силы 1, которого на карте ещё нет.
    public static EnchantmentModel? GiveRandom(CardModel card)
    {
        var options = Options(card);
        if (options.Count == 0) return null;
        // игровой генератор забега: у всех игроков совместной игры выпадет одно и то же
        var pick = options[card.Owner.RunState.Rng.Niche.NextInt(options.Count)];
        CardCmd.Enchant(pick.ToMutable(), card, 1m);
        return pick;
    }
}

// Страховка для сохранений до фикса: карта без вражеской цели с зачарованием Inky
// падает с NullReferenceException при розыгрыше (Inky вешает Слабость на Target == null).
// Проглатываем только этот случай — карта отыгрывается, Слабость просто не накладывается.
[HarmonyPatch(typeof(Inky), nameof(Inky.OnPlay))]
public static class InkyTargetGuard
{
    static Exception? Finalizer(Exception? __exception) =>
        __exception is NullReferenceException ? null : __exception;
}
