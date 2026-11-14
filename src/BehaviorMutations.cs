using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace KolyaMod;

// Поведенческие мутации яруса I (docs/mutation_library.md) — элитки актов 1–3 получают одну
// «тактическую» мутацию вместо первой статовой. Каждая — правило с одной фразой контр-игры.
// Реализация: ванильные силы там, где они точно передают механику (шипы, реген, буфер),
// хуки урона/хода там, где правило нестандартное. Всё детерминировано, в коопе сходится.
// Отличия от документа (сознательные): «Поджигатель» → «Жестокие удары» (отдельного статуса
// ожога в игре нет — вместо него PainfulStabs: незаблокированные удары оставляют раны);
// «Живой щит» прячется через буфер, а не перенаправление (перенаправление урона в движке
// ненадёжно). Значки-подсказки на карте и в бою — следующий этап, сейчас видно по силам.
public static class BehaviorMutations
{
    public enum Kind
    {
        Колючий, ЯдовитыйКлинок, ШипастыйПанцирь, ЗверинаяЯрость, Хищник, ЖивойЩит,
        Кровопийца, Берсерк, КостянаяБроня, Летун, Регенератор, ЖестокиеУдары, Заражённый,
    }

    class State
    {
        public Kind Type;
        public bool Seen;            // инициализация на первом ходу боя
        public bool TookDamage;      // звериная ярость
        public decimal Dealt;        // кровопийца: урон за прошлый раунд
        public decimal ArmorLeft;    // костяная броня: остаток поглощения за раунд
        public int BerserkGiven;     // берсерк: уже выдано единиц силы
        public bool Exploded;        // заражённый: взорвался ли уже
    }

    static readonly ConditionalWeakTable<Creature, State> Marked = new();

    public static int Count => Enum.GetValues<Kind>().Length;

    // Детерминированный выбор мутации комнаты: подсказка на карте и бой обязаны совпадать,
    // поэтому вместо rng в начале боя — хэш (сид забега, координаты элитки). В коопе сид
    // общий, у всех клиентов одна и та же мутация на одну и ту же комнату.
    public static int IndexFor(uint seed, int row, int col)
    {
        unchecked
        {
            uint h = seed ^ 0x9E3779B9u;
            h = (h * 2654435761u) ^ ((uint)(row + 1) * 40503u);
            h = (h * 2246822519u) ^ ((uint)(col + 11) * 92821u);
            h ^= h >> 13;
            return (int)(h % (uint)Count);
        }
    }

    // Название и одна строка правила — для тултипа на карте и лога.
    public static (string Name, string Desc) Info(int index) => ((Kind)index) switch
    {
        Kind.Колючий          => ("Колючий", "Получаешь урон за каждую атаку по нему. Бей редко и тяжело, добивай ядом."),
        Kind.ЯдовитыйКлинок   => ("Ядовитый клинок", "Каждая его атака кладёт 2 яда. Чисти яд или убивай быстро."),
        Kind.ШипастыйПанцирь  => ("Шипастый панцирь", "Удар по его блоку ранит тебя. Сначала снимай блок, потом бей."),
        Kind.ЗверинаяЯрость   => ("Звериная ярость", "Каждый ход без урона по нему: +2 силы. Не пропускай ходы урона."),
        Kind.Хищник           => ("Хищник", "Каждый ход добирает самого слабого героя. Держи здоровье сбалансированно."),
        Kind.ЖивойЩит         => ("Живой щит", "Пока живы его союзники, первые удары по нему не проходят. Бей ядом или с краю пака."),
        Kind.Кровопийца       => ("Кровопийца", "Лечится на треть нанесённого тебе урона. Добивай залпом."),
        Kind.Берсерк          => ("Берсерк", "+1 сила за каждые 15% потерянного здоровья. Финишируй с запасом урона."),
        Kind.КостянаяБроня    => ("Костяная броня", "Первые удары по нему за ход почти не проходят. Одна большая атака вместо мелочи."),
        Kind.Летун            => ("Летун", "Уклоняется от первой атаки каждый ход. Дай мелкую карту первой."),
        Kind.Регенератор      => ("Регенератор", "Лечится каждый ход. Яд и ожог останавливают реген — убивай быстро."),
        Kind.ЖестокиеУдары    => ("Жестокие удары", "Незаблокированные удары оставляют раны. Блокируй весь урон."),
        Kind.Заражённый       => ("Заражённый", "При смерти взрывается: 10 яда всем. Убивай с чисткой яда на руке."),
        _ => ("", ""),
    };

    static State Of(Creature c) => Marked.GetOrCreateValue(c);

    // применение по индексу (индекс — IndexFor(seed, coord), чтобы совпадать с подсказкой на карте)
    public static async Task<string> Apply(int index, Creature enemy, int act)
    {
        var kind = (Kind)index;
        var s = Of(enemy);
        s.Type = kind;
        s.Seen = false;
        switch (kind)
        {
            case Kind.Колючий:
                await Give<ThornsPower>(enemy, 4 + 3 * act);
                break;
            case Kind.ЖестокиеУдары:
                await Give<PainfulStabsPower>(enemy, 1);
                break;
            // остальные — чисто хуками ниже
        }
        return Info(index).Name;
    }

    static Task Give<T>(Creature c, decimal amount) where T : PowerModel =>
        PowerCmd.Apply<T>(new ThrowingPlayerChoiceContext(), c, amount, c, null);

    // ---------- общий ход врага: стартовые эффекты, сбросы счётчиков раунда ----------
    [HarmonyPatch(typeof(Hook), nameof(Hook.BeforeSideTurnStart))]
    static class TurnStart
    {
        static void Postfix(ICombatState combatState, CombatSide side, IReadOnlyList<Creature> participants, ref Task __result)
        {
            if (side != CombatSide.Enemy) return;
            if (!participants.Any(c => Marked.TryGetValue(c, out _))) return;
            __result = Do(__result, combatState, participants);
        }

        static async Task Do(Task before, ICombatState combat, IReadOnlyList<Creature> participants)
        {
            await before;
            int act = combat.RunState?.CurrentActIndex ?? 0;
            var players = combat.Players.Select(p => p.Creature).Where(c => c.IsAlive).ToList();
            foreach (var e in participants.Where(c => c.IsAlive && Marked.TryGetValue(c, out _)).ToList())
            {
                var s = Of(e);
                if (!s.Seen) { s.Seen = true; s.TookDamage = false; s.Dealt = 0; s.BerserkGiven = 0; s.Exploded = false; }
                s.ArmorLeft = 4 + act;   // костяная броня: лимит на новый раунд игрока
                switch (s.Type)
                {
                    case Kind.ЗверинаяЯрость:
                        if (!s.TookDamage)
                            await Give<StrengthPower>(e, 2);
                        s.TookDamage = false;
                        break;
                    case Kind.Хищник:
                        // «охотится за самым слабым»: добирает до него, кого бы ни выбрал ИИ
                        if (players.Count > 0)
                        {
                            var prey = players.MinBy(p => p.CurrentHp)!;
                            await CreatureCmd.Damage(new ThrowingPlayerChoiceContext(), prey, 3 + 2 * act,
                                (ValueProp)0, e);
                            Log.Info($"[kolya_mod] хищник {e.Monster?.Id} набросился на самого слабого");
                        }
                        break;
                    // буфер и реген не стакаются от хода к ходу — поддерживаем, а не наращиваем
                    case Kind.ЖивойЩит:
                        if (!e.HasPower<BufferPower>() &&
                            combat.Enemies.Count(x => x.IsAlive && !ReferenceEquals(x, e)) > 0)
                            await Give<BufferPower>(e, 1 + act / 2);
                        break;
                    case Kind.Летун:
                        if (!e.HasPower<BufferPower>())
                            await Give<BufferPower>(e, 1);
                        break;
                    case Kind.Регенератор:
                        if (!e.HasPower<RegenPower>())
                            await Give<RegenPower>(e, 3 + act);
                        break;
                    case Kind.Кровопийца:
                        if (s.Dealt > 0)
                        {
                            var heal = decimal.Round(s.Dealt * 0.3m);
                            if (heal > 0) await CreatureCmd.Heal(e, heal, true);
                            s.Dealt = 0;
                        }
                        break;
                    case Kind.Берсерк:
                        // +1 сила за каждые 15% потерянного HP; даём только дельту
                        int target = (int)((1m - (decimal)e.CurrentHp / e.MaxHp) * 100m / 15m);
                        if (target > s.BerserkGiven)
                        {
                            await Give<StrengthPower>(e, target - s.BerserkGiven);
                            s.BerserkGiven = target;
                        }
                        break;
                }
            }
        }
    }

    // ---------- урон: яд за атаки, контрурон за блок, счётчики, взрыв ----------
    [HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.Damage),
        typeof(PlayerChoiceContext), typeof(IEnumerable<Creature>), typeof(decimal), typeof(ValueProp), typeof(Creature))]
    static class OnDamage
    {
        static void Postfix(PlayerChoiceContext choiceContext, IEnumerable<Creature> targets, decimal amount,
            ValueProp props, Creature? dealer, ref Task<IEnumerable<DamageResult>> __result)
        {
            if (dealer == null || !Marked.TryGetValue(dealer, out var s)) return;
            if (props.HasFlag(ValueProp.Unpowered)) return;
            var players = targets.Where(t => t.IsPlayer).ToList();
            if (players.Count > 0 && s.Type == Kind.ЯдовитыйКлинок)
            {
                __result = Poison(__result, choiceContext, players, dealer);
                return;
            }
            if (dealer.IsPlayer && s.Type == Kind.Кровопийца)
                __result = TrackDealt(__result, dealer);
        }

        static async Task<IEnumerable<DamageResult>> Poison(Task<IEnumerable<DamageResult>> before,
            PlayerChoiceContext ctx, List<Creature> players, Creature dealer)
        {
            var results = await before;
            if (!dealer.IsAlive) return results;
            foreach (var p in players.Where(p => p.IsAlive))
                await PowerCmd.Apply<PoisonPower>(ctx, p, 2, dealer, null);
            return results;
        }

        static async Task<IEnumerable<DamageResult>> TrackDealt(Task<IEnumerable<DamageResult>> before, Creature dealer)
        {
            var results = await before;
            var s = Of(dealer);
            if (dealer.IsAlive) s.Dealt += results.Sum(r => r.TotalDamage);
            return results;
        }
    }

    // ---------- шипастый панцирь: ударил по защищённому — получи; заражённый: взрыв ----------
    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterDamageGiven))]
    static class AfterHit
    {
        static void Postfix(PlayerChoiceContext choiceContext, ICombatState combatState, Creature? dealer,
            DamageResult results, ValueProp props, Creature target, CardModel? cardSource,
            ref Task __result)
        {
            if (dealer == null) return;
            if (!Marked.TryGetValue(target, out var s)) return;
            int act = combatState.RunState?.CurrentActIndex ?? 0;
            if (dealer.IsPlayer && s.Type == Kind.ШипастыйПанцирь && results.BlockedDamage > 0)
            {
                __result = Counter(__result, choiceContext, dealer, target, 2 * act);
                return;
            }
            if (dealer.IsPlayer && s.Type == Kind.ЗверинаяЯрость && results.TotalDamage > 0)
            {
                __result = MarkTook(__result, target);
                return;
            }
            if (dealer.IsPlayer && s.Type == Kind.Заражённый && results.WasTargetKilled && !s.Exploded)
            {
                s.Exploded = true;
                __result = Explode(__result, choiceContext, combatState, target);
            }
        }

        static async Task Counter(Task before, PlayerChoiceContext ctx, Creature dealer, Creature source, decimal dmg)
        {
            await before;
            if (dmg > 0 && dealer.IsAlive)
                await CreatureCmd.Damage(ctx, dealer, dmg, (ValueProp)0, source);
        }

        static async Task MarkTook(Task before, Creature self)
        {
            await before;
            Of(self).TookDamage = true;
        }

        static async Task Explode(Task before, PlayerChoiceContext ctx, ICombatState combat, Creature source)
        {
            await before;
            foreach (var p in combat.Players.Select(p => p.Creature).Where(c => c.IsAlive))
                await PowerCmd.Apply<PoisonPower>(ctx, p, 10, source, null);
            Log.Info($"[kolya_mod] заражённый {source.Monster?.Id} взорвался ядом");
        }
    }

    // ---------- костяная броня: первые N урона за раунд не проходят ----------
    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyDamage))]
    static class BoneArmor
    {
        static void Postfix(ref decimal __result, Creature target, Creature? dealer,
            MegaCrit.Sts2.Core.Entities.Cards.CardPreviewMode previewMode)
        {
            if (previewMode != CardPreviewMode.None) return;
            if (dealer == null || !dealer.IsPlayer || __result <= 0) return;
            if (!Marked.TryGetValue(target, out var s) || s.Type != Kind.КостянаяБроня || s.ArmorLeft <= 0) return;
            var absorb = Math.Min(__result, s.ArmorLeft);
            __result -= absorb;
            s.ArmorLeft -= absorb;
        }
    }
}
