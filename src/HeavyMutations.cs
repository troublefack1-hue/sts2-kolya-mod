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
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace KolyaMod;

// Ярус III (акты 6+, 0-based act >= 5) и босс-пул (боссы с 4-го акта) — docs/mutation_library.md.
// Сознательные упрощения относительно документа (фиксируем в release notes):
// «Зеркало» копирует блок вместо копии карты игрока; «Коллекционер» — растущие дебаффы вместо
// кражи карт (ручное создание карт в бою API-рискованно); «Теневой двойник» копирует силу героев
// вместо копии колоды; «Личинка»/«Феникс» не дают умереть первый раз (кламп урона), воскрешение
// оформляется в начале следующего хода; «Крадущий талант» снимает положительные эффекты вместо
// зачарований карт; «Пожиратель душ» растит себя пока герои ранены вместо запрета возрождения.
public static class HeavyMutations
{
    // ---------- ярус III ----------
    public enum Kind3
    {
        Зеркало, Зачарованный, Личинка, Коллекционер, Хрономан,
        ПожирательЭнергии, Феникс, КрадущийТалант, ТеневойДвойник, Жнец,
    }
    public const int Tier3Count = 10;

    // ---------- босс-пул ----------
    public enum BossKind
    {
        Полководец, ГнилоеЦарство, Лавина, ТеневойКонтракт,
        Жертвенник, КроваваяЛуна, ПожирательДуш, Перевертыш,
    }
    public const int BossCount = 8;

    class State
    {
        public bool IsBoss;
        public int KindIdx;
        public bool Seen;
        public int TurnCounter;
        public int Attacks;          // зачарованный: счётчик атак по героям
        public bool Revived;         // личинка/феникс: смертельный удар уже погашен
        public bool ReviveApplied;   // воскрешение выдано следующим ходом
        public bool Charged;         // хрономан/кровавая луна: усиленная атака ждёт
        public bool Rage;            // кровавая луна: неистовство (лечится от урона)
        public decimal Banked;       // теневой контракт: недобранный урон по героям
        public Creature? Mark;       // жнец: отмеченный герой
    }

    static readonly ConditionalWeakTable<Creature, State> Marked = new();
    static State Of(Creature c) => Marked.GetOrCreateValue(c);
    public static bool IsMarked(Creature c) => Marked.TryGetValue(c, out _);

    // свой хэш на комнату — независим от элиточного, чтобы пулы не влияли друг на друга
    public static int IndexFor3(uint seed, int row, int col)
    {
        unchecked
        {
            uint h = seed ^ 0x51ED270Bu;
            h = (h * 2246822519u) ^ ((uint)(row + 7) * 3266489917u);
            h = (h * 3266489917u) ^ ((uint)(col + 3) * 668265263u);
            h ^= h >> 15;
            return (int)(h % (uint)Tier3Count);
        }
    }

    public static int IndexForBoss(uint seed, int row, int col)
    {
        unchecked
        {
            uint h = seed ^ 0x85EBCA6Bu;
            h = (h * 0x9E3779B1u) ^ ((uint)(row + 5) * 374761393u);
            h = (h * 1274126177u) ^ ((uint)(col + 13) * 2246822519u);
            h ^= h >> 16;
            return (int)(h % (uint)BossCount);
        }
    }

    public static (string Name, string Desc) Info3(int index, int act) => ((Kind3)index) switch
    {
        Kind3.Зеркало           => ("Зеркало", "Каждые 2 хода копирует весь блок самого защищённого героя. Возвращает 50% урона от атак по нему."),
        Kind3.Зачарованный      => ("Зачарованный", $"В начале хода +2 силы и {6 + act} блока. Каждая вторая атака по героям бьёт вдвое сильнее."),
        Kind3.Личинка           => ("Личинка", "Один раз переживает смертельный удар: восстанавливает половину здоровья и получает +2 силы."),
        Kind3.Коллекционер      => ("Коллекционер", "Каждые 3 хода: 1 уязвимость и 1 слабость каждому герою, +1 силы себе."),
        Kind3.Хрономан          => ("Хрономан", $"Каждый 3-й ход получает {2 * (6 + act)} блока, а следующая атака бьёт вдвое сильнее."),
        Kind3.ПожирательЭнергии => ("Пожиратель энергии", "Каждые 2 хода герой с наибольшей энергией теряет 1 энергию следующего хода."),
        Kind3.Феникс            => ("Феникс", "Один раз переживает смертельный удар: полностью восстанавливает здоровье и получает +2 силы."),
        Kind3.КрадущийТалант    => ("Крадущий талант", "Каждые 3 хода снимает по 1 положительному эффекту с каждого героя и растёт."),
        Kind3.ТеневойДвойник    => ("Теневой двойник", "Копирует вашу силу: получает столько силы, сколько есть у всех героев вместе."),
        Kind3.Жнец              => ("Жнец", "Отмечает самого раненого героя; через 3 хода казнит его, если у того меньше четверти здоровья."),
        _ => ("", ""),
    };

    public static (string Name, string Desc) InfoBoss(int index, int act) => ((BossKind)index) switch
    {
        BossKind.Полководец      => ("Полководец", "По очереди: даёт всем врагам 3 блока, потом всем +1 силы, потом 1 слабость и уязвимость каждому герою."),
        BossKind.ГнилоеЦарство   => ("Гнилое царство", $"Каждые 2 хода проклинает карты каждого героя и лечится на {5 + act}."),
        BossKind.Лавина          => ("Лавина", $"Три хода копит силу (получает на 50% больше урона), затем бьёт каждого героя на {15 + 3 * act}."),
        BossKind.ТеневойКонтракт => ("Теневой контракт", "Каждые 3 хода наносит каждому герою урон, равный недобранному им урону с прошлого хода."),
        BossKind.Жертвенник      => ("Жертвенник", "Каждые 3 хода пожирает самого слабого союзника: лечится на его здоровье и получает +2 силы."),
        BossKind.КроваваяЛуна    => ("Кровавая луна", "Два хода неиствует: растёт и лечится от нанесённого урона. Третий отдыхает и получает двойной урон."),
        BossKind.ПожирательДуш   => ("Пожиратель душ", "Пока любой герой ранен (меньше половины здоровья), каждый ход получает +1 силы и лечится."),
        BossKind.Перевертыш      => ("Перевертыш", "Каждые 2 хода сжигает весь блок самого защищённого героя и растёт от украденного."),
        _ => ("", ""),
    };

    // ---------- применение ----------
    public static async Task<string> Apply3(int index, Creature enemy, int act)
    {
        var kind = (Kind3)index;
        var s = Of(enemy);
        s.IsBoss = false; s.KindIdx = index; s.Seen = false;
        s.TurnCounter = 0; s.Attacks = 0; s.Revived = false; s.ReviveApplied = false;
        s.Charged = false; s.Rage = false; s.Banked = 0; s.Mark = null;
        switch (kind)
        {
            case Kind3.ТеневойДвойник:
                {
                    // копия силы героев (в Apply бой уже идёт, CombatState доступен)
                    int sum = 0;
                    var players = enemy.CombatState?.Players;
                    if (players != null)
                        foreach (var p in players)
                        {
                            var str = p.Creature.Powers.FirstOrDefault(x => x is StrengthPower);
                            if (str != null) sum += str.Amount;
                        }
                    await Give<StrengthPower>(enemy, Math.Max(2, sum));
                    break;
                }
        }
        return Info3(index, act).Name;
    }

    public static async Task<string> ApplyBoss(int index, Creature boss, int act)
    {
        var kind = (BossKind)index;
        var s = Of(boss);
        s.IsBoss = true; s.KindIdx = index; s.Seen = false;
        s.TurnCounter = 0; s.Attacks = 0; s.Revived = false; s.ReviveApplied = false;
        s.Charged = false; s.Rage = false; s.Banked = 0; s.Mark = null;
        // стартовые состояния уникальных механик
        if (kind == BossKind.КроваваяЛуна) s.Rage = true;
        return InfoBoss(index, act).Name;
    }

    static Task Give<T>(Creature c, decimal amount) where T : PowerModel =>
        PowerCmd.Apply<T>(new ThrowingPlayerChoiceContext(), c, amount, c, null);

    // ---------- общий ход врага ----------
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
                s.TurnCounter++;
                if (!s.IsBoss)
                    await Turn3(e, s, (Kind3)s.KindIdx, act, combat, players);
                else
                    await TurnBoss(e, s, (BossKind)s.KindIdx, act, combat, players);
            }
        }

        static async Task Turn3(Creature e, State s, Kind3 kind, int act, ICombatState combat, List<Creature> players)
        {
            switch (kind)
            {
                case Kind3.Зеркало:
                    if (s.TurnCounter % 2 == 0 && players.Count > 0)
                    {
                        var best = players.MaxBy(p => p.Block)!;
                        if (best.Block > 0)
                            await CreatureCmd.GainBlock(e, best.Block, (ValueProp)0, null!, false);
                    }
                    break;
                case Kind3.Зачарованный:
                    await Give<StrengthPower>(e, 2);
                    await CreatureCmd.GainBlock(e, 6 + act, (ValueProp)0, null!, false);
                    break;
                case Kind3.Личинка:
                case Kind3.Феникс:
                    // воскрешение после погашенного смертельного удара
                    if (s.Revived && !s.ReviveApplied)
                    {
                        s.ReviveApplied = true;
                        decimal heal = kind == Kind3.Феникс ? e.MaxHp : e.MaxHp / 2m;
                        await CreatureCmd.Heal(e, heal, true);
                        await Give<StrengthPower>(e, 2);
                        Log.Info($"[kolya_mod] {kind} {e.Monster?.Id} переродился");
                    }
                    break;
                case Kind3.Коллекционер:
                    if (s.TurnCounter % 3 == 0)
                    {
                        foreach (var p in players)
                        {
                            await PowerCmd.Apply<VulnerablePower>(new ThrowingPlayerChoiceContext(), p, 1, e, null);
                            await PowerCmd.Apply<WeakPower>(new ThrowingPlayerChoiceContext(), p, 1, e, null);
                        }
                        await Give<StrengthPower>(e, 1);
                    }
                    break;
                case Kind3.Хрономан:
                    if (s.TurnCounter % 3 == 0)
                    {
                        await CreatureCmd.GainBlock(e, 2 * (6 + act), (ValueProp)0, null!, false);
                        s.Charged = true;
                    }
                    break;
                case Kind3.ПожирательЭнергии:
                    if (s.TurnCounter % 2 == 0 && combat.Players.Count > 0)
                    {
                        var rich = combat.Players.MaxBy(p => p.MaxEnergy);
                        if (rich != null)
                            await PowerCmd.Apply<EnergyNextTurnPower>(new ThrowingPlayerChoiceContext(), rich.Creature, -1, e, null);
                    }
                    break;
                case Kind3.КрадущийТалант:
                    if (s.TurnCounter % 3 == 0)
                        foreach (var p in players)
                        {
                            var buffs = p.Powers.Where(x => x.Type == PowerType.Buff && x.Amount > 0).ToList();
                            if (buffs.Count == 0) continue;
                            var steal = buffs[s.TurnCounter % buffs.Count];
                            await PowerCmd.Remove(steal);
                            await Give<StrengthPower>(e, 1);
                        }
                    break;
                case Kind3.Жнец:
                    // ходы 1,4,7…: новая отметка; перед ней — казнь предыдущей
                    if ((s.TurnCounter - 1) % 3 == 0)
                    {
                        if (s.Mark != null && s.Mark.IsAlive
                            && s.Mark.CurrentHp * 4 <= s.Mark.MaxHp)
                        {
                            await CreatureCmd.Damage(new ThrowingPlayerChoiceContext(), s.Mark, 9999m, (ValueProp)0, e);
                            Log.Info($"[kolya_mod] жнец казнил отмеченного героя");
                        }
                        if (players.Count > 0)
                            s.Mark = players.MinBy(p => (decimal)p.CurrentHp / p.MaxHp);
                    }
                    break;
            }
        }

        static async Task TurnBoss(Creature e, State s, BossKind kind, int act, ICombatState combat, List<Creature> players)
        {
            switch (kind)
            {
                case BossKind.Полководец:
                    switch ((s.TurnCounter - 1) % 3)
                    {
                        case 0:
                            foreach (var nb in combat.Enemies.Where(x => x.IsAlive))
                                await CreatureCmd.GainBlock(nb, 3, (ValueProp)0, null!, false);
                            break;
                        case 1:
                            foreach (var nb in combat.Enemies.Where(x => x.IsAlive))
                                await Give<StrengthPower>(nb, 1);
                            break;
                        case 2:
                            foreach (var p in players)
                            {
                                await PowerCmd.Apply<WeakPower>(new ThrowingPlayerChoiceContext(), p, 1, e, null);
                                await PowerCmd.Apply<VulnerablePower>(new ThrowingPlayerChoiceContext(), p, 1, e, null);
                            }
                            break;
                    }
                    break;
                case BossKind.ГнилоеЦарство:
                    if (s.TurnCounter % 2 == 0)
                    {
                        foreach (var p in players)
                            await PowerCmd.Apply<HexPower>(new ThrowingPlayerChoiceContext(), p, 1, e, null);
                        await CreatureCmd.Heal(e, 5 + act, true);
                    }
                    break;
                case BossKind.Лавина:
                    {
                        int phase = s.TurnCounter % 4;
                        if (phase == 0)
                            foreach (var p in players)
                                await CreatureCmd.Damage(new ThrowingPlayerChoiceContext(), p, 15 + 3 * act, (ValueProp)0, e);
                        else
                            await CreatureCmd.GainBlock(e, 8 + 2 * act, (ValueProp)0, null!, false);
                        break;
                    }
                case BossKind.ТеневойКонтракт:
                    if (s.TurnCounter % 3 == 0)
                    {
                        foreach (var p in players)
                            if (s.Banked > 0)
                                await CreatureCmd.Damage(new ThrowingPlayerChoiceContext(), p, Math.Min(40m, s.Banked), (ValueProp)0, e);
                        s.Banked = 0;
                    }
                    break;
                case BossKind.Жертвенник:
                    if (s.TurnCounter % 3 == 0)
                    {
                        var victim = combat.Enemies.Where(x => x.IsAlive && !ReferenceEquals(x, e)).MinBy(x => x.CurrentHp);
                        if (victim != null)
                        {
                            await CreatureCmd.Damage(new ThrowingPlayerChoiceContext(), victim, 9999m, (ValueProp)0, e);
                            await CreatureCmd.Heal(e, victim.MaxHp, true);
                            await Give<StrengthPower>(e, 2);
                        }
                        else
                            await Give<StrengthPower>(e, 1);
                    }
                    break;
                case BossKind.КроваваяЛуна:
                    {
                        int phase = s.TurnCounter % 3;
                        if (phase == 0)
                        {
                            // отдых: уязвим (обработка в ModifyDamage), неистовство кончилось
                            s.Rage = false;
                        }
                        else
                        {
                            s.Rage = true;
                            await Give<StrengthPower>(e, 2);
                        }
                        break;
                    }
                case BossKind.ПожирательДуш:
                    if (players.Any(p => p.CurrentHp * 2 <= p.MaxHp))
                    {
                        await Give<StrengthPower>(e, 1);
                        await CreatureCmd.Heal(e, 6, true);
                    }
                    break;
                case BossKind.Перевертыш:
                    if (s.TurnCounter % 2 == 0 && players.Count > 0)
                    {
                        var best = players.MaxBy(p => p.Block)!;
                        int stolen = best.Block;
                        if (stolen > 0)
                        {
                            await CreatureCmd.LoseBlock(best, stolen);
                            await Give<StrengthPower>(e, Math.Max(1, stolen / 5));
                        }
                    }
                    break;
            }
        }
    }

    // ---------- урон: кламп смерти, усиления, контрурон, банк контракта, лечение неистовства ----------
    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyDamage))]
    static class OnDamage
    {
        static void Postfix(ref decimal __result, Creature target, Creature? dealer,
            MegaCrit.Sts2.Core.Entities.Cards.CardPreviewMode previewMode)
        {
            if (previewMode != CardPreviewMode.None) return;
            if (__result <= 0 || dealer == null) return;
            // личинка/феникс: гасим первый смертельный удар — враг остаётся с 1 ОЗ
            if (dealer.IsPlayer && Marked.TryGetValue(target, out var ts) && !ts.IsBoss
                && (ts.KindIdx == (int)Kind3.Личинка || ts.KindIdx == (int)Kind3.Феникс)
                && !ts.Revived && __result >= target.CurrentHp && target.CurrentHp > 0)
            {
                __result = target.CurrentHp - 1;
                ts.Revived = true;
                return;
            }
            // лавина в фазе копления получает +50% урона
            if (dealer.IsPlayer && Marked.TryGetValue(target, out var ls) && ls.IsBoss
                && ls.KindIdx == (int)BossKind.Лавина && ls.TurnCounter % 4 != 0)
            {
                __result = decimal.Round(__result * 1.5m);
                return;
            }
            // кровавая луна в отдыхе получает двойной урон
            if (dealer.IsPlayer && Marked.TryGetValue(target, out var rs) && rs.IsBoss
                && rs.KindIdx == (int)BossKind.КроваваяЛуна && rs.TurnCounter % 3 == 0)
            {
                __result *= 2;
                return;
            }
            if (dealer.IsPlayer || !target.IsPlayer) return;
            // атаки врага по героям:
            if (Marked.TryGetValue(dealer, out var s) && !s.IsBoss)
            {
                if (s.KindIdx == (int)Kind3.Зачарованный)
                {
                    s.Attacks++;
                    if (s.Attacks % 2 == 0) __result *= 2;   // каждая вторая атака — двойная
                }
                else if (s.KindIdx == (int)Kind3.Хрономан && s.Charged)
                {
                    s.Charged = false;
                    __result *= 2;
                }
                return;
            }
            if (Marked.TryGetValue(dealer, out var bs) && bs.IsBoss
                && bs.KindIdx == (int)BossKind.КроваваяЛуна && bs.Rage)
            {
                // лечение от урона — в AfterHit (async)
            }
        }

        // ---------- после удара: контрурон зеркала, банк контракта, лечение кровавой луны ----------
        [HarmonyPatch(typeof(Hook), nameof(Hook.AfterDamageGiven))]
        static class AfterHit
        {
            static void Postfix(PlayerChoiceContext choiceContext, ICombatState combatState, Creature? dealer,
                DamageResult results, ValueProp props, Creature target, CardModel? cardSource,
                ref Task __result)
            {
                if (dealer == null || results.TotalDamage <= 0) return;
                // зеркало: 50% урона обратно атакующему
                if (dealer.IsPlayer && !target.IsPlayer && Marked.TryGetValue(target, out var zs) && !zs.IsBoss
                    && zs.KindIdx == (int)Kind3.Зеркало)
                {
                    var back = decimal.Round(results.TotalDamage * 0.5m);
                    if (back > 0) __result = ReflectBack(__result, choiceContext, dealer, target, back);
                }
                // теневой контракт: банкуем недобранный урон по героям
                if (!dealer.IsPlayer && target.IsPlayer && Marked.TryGetValue(dealer, out var cs) && cs.IsBoss
                    && cs.KindIdx == (int)BossKind.ТеневойКонтракт)
                    cs.Banked += results.UnblockedDamage;
                // кровавая луна в неистовстве лечится от нанесённого
                if (!dealer.IsPlayer && target.IsPlayer && Marked.TryGetValue(dealer, out var rs) && rs.IsBoss
                    && rs.KindIdx == (int)BossKind.КроваваяЛуна && rs.Rage)
                {
                    var heal = decimal.Round(results.TotalDamage * 0.5m);
                    if (heal > 0) __result = Leech(__result, dealer, heal);
                }
            }

            static async Task ReflectBack(Task before, PlayerChoiceContext ctx, Creature dealer, Creature source, decimal dmg)
            {
                await before;
                if (dealer.IsAlive)
                    await CreatureCmd.Damage(ctx, dealer, dmg, (ValueProp)0, source);
            }

            static async Task Leech(Task before, Creature self, decimal heal)
            {
                await before;
                if (self.IsAlive)
                    await CreatureCmd.Heal(self, heal, true);
            }
        }
    }
}
