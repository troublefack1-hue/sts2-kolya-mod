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
// Ярус II (0.3.42, docs/mutation_library.md): 13 мутаций для элиток актов 3–5. В отличие от
// яруса I правила сложнее и часто многосоставные, поэтому часть реализована упрощённо
// (фиксируем в заметках релиза): «Плащ отражения» гасит удар на 50% вместо отражения,
// «Дезертир» впадает в отчаяние вместо побега, «Командир роты» растит собственную силу.
public static class BehaviorMutations
{
    public enum Kind
    {
        // ярус I — элитки актов 1–2
        Колючий, ЯдовитыйКлинок, ШипастыйПанцирь, ЗверинаяЯрость, Хищник, ЖивойЩит,
        Кровопийца, Берсерк, КостянаяБроня, Летун, Регенератор, ЖестокиеУдары, Заражённый,
        // ярус II — элитки актов 3–5
        ПожирательСил, КомандирРоты, Щитоносец, Стая, ПлащОтражения, Дезертир, Пронзающий,
        ГлухаяОборона, Паразит, ВолчьяСтая, ГниющаяАура, Капкан, Мститель,
    }

    public const int TierCount = 13;         // мутаций в каждом ярусе
    public static bool IsTier2(int index) => index >= TierCount;

    class State
    {
        public Kind Type;
        public bool Seen;            // инициализация на первом ходу боя
        public bool TookDamage;      // звериная ярость
        public decimal Dealt;        // кровопийца: урон за прошлый раунд
        public decimal ArmorLeft;    // костяная броня: остаток поглощения за раунд
        public int BerserkGiven;     // берсерк: уже выдано единиц силы
        public bool Exploded;        // заражённый: взорвался ли уже
        // ярус II
        public int CloakLeft;        // плащ отражения: осталось гашений в этом раунде
        public int TurnCounter;      // глухая оборона / капкан: счётчик ходов
        public int Desperation;      // дезертир: сколько порогов здоровья пересёк (0..3)
        public int Vengeful;         // мститель: выдано сил за этот раунд (макс 3)
        public bool Struck;          // пожиратель сил: ранил героев в прошлом раунде
    }

    static readonly ConditionalWeakTable<Creature, State> Marked = new();

    public static int Count => 2 * TierCount;

    // Детерминированный выбор мутации комнаты: подсказка на карте и бой обязаны совпадать,
    // поэтому вместо rng в начале боя — хэш (сид забега, координаты элитки). В коопе сид
    // общий, у всех клиентов одна и та же мутация на одну и ту же комнату.
    // Возвращает ИНДЕКС ВНУТРИ ЯРУСА (0..12): ярус определяется актом у вызывающего,
    // прибавляя TierCount для актов 3–5. Модуль — от TierCount, а не от Count: расчёт для
    // актов 1–2 (ярус I) не должен съезжать при добавлении новых ярусов.
    public static int IndexFor(uint seed, int row, int col)
    {
        unchecked
        {
            uint h = seed ^ 0x9E3779B9u;
            h = (h * 2654435761u) ^ ((uint)(row + 1) * 40503u);
            h = (h * 2246822519u) ^ ((uint)(col + 11) * 92821u);
            h ^= h >> 13;
            return (int)(h % (uint)TierCount);
        }
    }

    // Название и КОРОТКОЕ описание только механики (числа — под акт, 0-based), без контр-игры.
    public static (string Name, string Desc) Info(int index, int act) => ((Kind)index) switch
    {
        Kind.Колючий          => ("Колючий", $"Возвращает {4 + 3 * act} урона за каждую атаку по нему."),
        Kind.ЯдовитыйКлинок   => ("Ядовитый клинок", "Каждая его атака накладывает 2 яда."),
        Kind.ШипастыйПанцирь  => ("Шипастый панцирь", $"Атака, поглощённая его блоком, ранит атакующего на {2 * (act + 1)}."),
        Kind.ЗверинаяЯрость   => ("Звериная ярость", "Получает +2 силы за каждый ход, в который по нему не прошёл урон."),
        Kind.Хищник           => ("Хищник", $"Каждый ход атакует самого раненого героя на {3 + 2 * act} урона."),
        Kind.ЖивойЩит         => ("Живой щит", $"Пока живы его союзники, первые {1 + act / 2} атаки по нему за ход не наносят урона."),
        Kind.Кровопийца       => ("Кровопийца", "Лечится на 30% урона, нанесённого героям за прошлый раунд."),
        Kind.Берсерк          => ("Берсерк", "+1 сила за каждые 15% потерянного здоровья."),
        Kind.КостянаяБроня    => ("Костяная броня", $"Первые {4 + act} урона по нему за ваш ход не наносятся."),
        Kind.Летун            => ("Летун", "Первая атака по нему за ход промахивается."),
        Kind.Регенератор      => ("Регенератор", $"Восстанавливает {3 + act} здоровья в начале хода."),
        Kind.ЖестокиеУдары    => ("Жестокие удары", "Незаблокированный урон оставляет ранения в колоде героя."),
        Kind.Заражённый       => ("Заражённый", "При смерти наносит 10 яда каждому герою."),
        // ---- ярус II (акты 3–5) ----
        Kind.ПожирательСил   => ("Пожиратель сил", "После раунда, в который он ранил героев: +1 силы себе и −1 энергии каждому герою в следующий ход."),
        Kind.КомандирРоты    => ("Командир роты", "В начале хода получает +1 силы. Сила копится, пока он жив."),
        Kind.Щитоносец       => ("Щитоносец", $"В начале хода получает {6 + 2 * act} блока и делится половиной с соседними врагами."),
        Kind.Стая            => ("Стая", "Зовёт подмогу того же вида."),
        Kind.ПлащОтражения   => ("Плащ отражения", "Первые 2 удара по нему за раунд наносятся наполовину."),
        Kind.Дезертир        => ("Дезертир", "При падении ниже 75%, 50% и 25% здоровья: +2 силы и 1 артефакт."),
        Kind.Пронзающий      => ("Пронзающий", $"{30 + 5 * Math.Max(0, act - 2)}% урона его атак проходит сквозь блок."),
        Kind.ГлухаяОборона   => ("Глухая оборона", $"Два хода наращивает блок, на третий бьёт каждого героя на {8 + 2 * act}."),
        Kind.Паразит         => ("Паразит", "Лечит всех врагов на 50% урона, нанесённого героям."),
        Kind.ВолчьяСтая      => ("Волчья стая", "В начале хода +2 силы и +3 блока за каждого живого врага."),
        Kind.ГниющаяАура     => ("Гниющая аура", $"В начале хода сжигает по {4 + act} блока каждому герою."),
        Kind.Капкан          => ("Капкан", "В начале боя: 2 уязвимости и 2 слабости; каждый 3-й ход — ещё по 1."),
        Kind.Мститель        => ("Мститель", "Удар по нему больше 10% его здоровья: +1 силы (до 3 раз за раунд)."),
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
            case Kind.Летун:
            case Kind.ЖивойЩит:
                // буфер должен стоять ДО первого хода игрока, а не ждать начала хода врага,
                // иначе первая атака проходит, а «промах» появляется только со второй
                await Give<BufferPower>(enemy, 1);
                break;
            case Kind.Капкан:
                // первый ход: всем героям 2 уязвимости и 2 слабости (как CrushPlayers в Mutations)
                var players = enemy.CombatState?.Players;
                if (players != null)
                    foreach (var p in players)
                    {
                        await PowerCmd.Apply<VulnerablePower>(new ThrowingPlayerChoiceContext(), p.Creature, 2, enemy, null);
                        await PowerCmd.Apply<WeakPower>(new ThrowingPlayerChoiceContext(), p.Creature, 2, enemy, null);
                    }
                break;
            // остальные — чисто хуками ниже
        }
        return Info(index, act).Name;
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
                s.CloakLeft = 2;         // плащ отражения: новый раунд — снова 2 гашения
                s.Vengeful = 0;          // мститель: лимит силы на новый раунд
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
                    // ---------- ярус II ----------
                    case Kind.ПожирательСил:
                        // ранил героев в прошлом раунде → растёт и крадёт энергию следующего хода
                        if (s.Struck)
                        {
                            await Give<StrengthPower>(e, 1);
                            foreach (var p in players)
                                await PowerCmd.Apply<EnergyNextTurnPower>(new ThrowingPlayerChoiceContext(), p, -1, e, null);
                            s.Struck = false;
                        }
                        break;
                    case Kind.КомандирРоты:
                        await Give<StrengthPower>(e, 1);   // стакается каждый ход, пока жив
                        break;
                    case Kind.Щитоносец:
                        {
                            // блок всегда растёт (соло-элитки тоже), половина — соседям
                            int blk = 6 + 2 * act;
                            await CreatureCmd.GainBlock(e, blk, (ValueProp)0, null!, false);
                            var allies = combat.Enemies.Where(x => x.IsAlive && !ReferenceEquals(x, e)).ToList();
                            if (allies.Count > 0)
                            {
                                var half = decimal.Round(blk / 2m);
                                foreach (var nb in allies)
                                    await CreatureCmd.GainBlock(nb, half, (ValueProp)0, null!, false);
                            }
                            break;
                        }
                    case Kind.Стая:
                        Summoner.Mark(e);   // призыв подмоги того же вида (с ванильными лимитами)
                        break;
                    case Kind.ГлухаяОборона:
                        s.TurnCounter++;
                        if (s.TurnCounter % 3 == 0)
                        {
                            foreach (var p in players)
                                await CreatureCmd.Damage(new ThrowingPlayerChoiceContext(), p, 8 + 2 * act, (ValueProp)0, e);
                        }
                        else
                            await CreatureCmd.GainBlock(e, 6 + 2 * act, (ValueProp)0, null!, false);
                        break;
                    case Kind.Капкан:
                        // давление не заканчивается: каждый 3-й ход — ещё по 1 уязвимости и слабости
                        s.TurnCounter++;
                        if (s.TurnCounter % 3 == 0)
                            foreach (var p in players)
                            {
                                await PowerCmd.Apply<VulnerablePower>(new ThrowingPlayerChoiceContext(), p, 1, e, null);
                                await PowerCmd.Apply<WeakPower>(new ThrowingPlayerChoiceContext(), p, 1, e, null);
                            }
                        break;
                    case Kind.ВолчьяСтая:
                        {
                            int n = combat.Enemies.Count(x => x.IsAlive);
                            await Give<StrengthPower>(e, 2);
                            await CreatureCmd.GainBlock(e, 3 * n, (ValueProp)0, null!, false);
                            break;
                        }
                    case Kind.ГниющаяАура:
                        foreach (var p in players)
                            await CreatureCmd.LoseBlock(p, 4 + act);
                        break;
                    case Kind.Дезертир:
                        // отчаяние нарастает: пороги 75/50/25%, на каждом +2 силы и 1 артефакт
                        {
                            decimal frac = (decimal)e.CurrentHp / e.MaxHp;
                            int crossed = frac <= 0.75m ? (frac <= 0.5m ? (frac <= 0.25m ? 3 : 2) : 1) : 0;
                            if (crossed > s.Desperation)
                            {
                                int diff = crossed - s.Desperation;
                                s.Desperation = crossed;
                                await Give<StrengthPower>(e, 2 * diff);
                                await Give<ArtifactPower>(e, diff);
                            }
                            break;
                        }
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
            if (players.Count > 0 && s.Type == Kind.ПожирательСил)
            {
                __result = TrackStrike(__result, dealer);
                return;
            }
            if (dealer.IsPlayer && s.Type == Kind.Кровопийца)
                __result = TrackDealt(__result, dealer);
        }

        static async Task<IEnumerable<DamageResult>> TrackStrike(Task<IEnumerable<DamageResult>> before, Creature dealer)
        {
            var results = await before;
            if (dealer.IsAlive && results.Sum(r => r.TotalDamage) > 0)
                Of(dealer).Struck = true;
            return results;
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
            // паразит: урон героям лечит его союзников (dealer мутирован, target — игрок)
            if (!dealer.IsPlayer && target.IsPlayer && results.TotalDamage > 0
                && Marked.TryGetValue(dealer, out var ds) && ds.Type == Kind.Паразит)
            {
                __result = Leech(__result, combatState, dealer, results.TotalDamage);
                return;
            }
            if (!Marked.TryGetValue(target, out var s)) return;
            int act = combatState.RunState?.CurrentActIndex ?? 0;
            if (dealer.IsPlayer && s.Type == Kind.ШипастыйПанцирь && results.BlockedDamage > 0)
            {
                __result = Counter(__result, choiceContext, dealer, target, 2 * (act + 1));
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
            // мститель: сильный удар по нему разжигает ярость (до 3 раз за раунд)
            if (dealer.IsPlayer && s.Type == Kind.Мститель && s.Vengeful < 3
                && results.TotalDamage >= (decimal)target.MaxHp * 0.1m)
            {
                s.Vengeful++;
                __result = Vengeance(__result, target);
            }
        }

        static async Task Vengeance(Task before, Creature self)
        {
            await before;
            await Give<StrengthPower>(self, 1);
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

    // ---------- паразит: урон героям лечит всех врагов, включая самого паразита ----------
    static async Task Leech(Task before, ICombatState combat, Creature dealer, decimal dealt)
    {
        await before;
        var heal = decimal.Round(dealt * 0.5m);
        if (heal <= 0) return;
        foreach (var nb in combat.Enemies.Where(x => x.IsAlive))
            await CreatureCmd.Heal(nb, heal, true);
    }

    // ---------- костяная броня: первые N урона за раунд не проходят ----------
    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyDamage))]
    static class BoneArmor
    {
        static void Postfix(ref decimal __result, Creature target, Creature? dealer,
            MegaCrit.Sts2.Core.Entities.Cards.CardPreviewMode previewMode)
        {
            if (previewMode != CardPreviewMode.None) return;
            if (dealer == null || __result <= 0) return;
            if (dealer.IsPlayer && Marked.TryGetValue(target, out var s))
            {
                if (s.Type == Kind.КостянаяБроня && s.ArmorLeft > 0)
                {
                    var absorb = Math.Min(__result, s.ArmorLeft);
                    __result -= absorb;
                    s.ArmorLeft -= absorb;
                }
                // плащ отражения: первые 2 удара по нему за раунд на 50% слабее
                else if (s.Type == Kind.ПлащОтражения && s.CloakLeft > 0)
                {
                    s.CloakLeft--;
                    __result = decimal.Round(__result * 0.5m);
                }
                return;
            }
            // пронзающий: часть урона по игроку проходит сквозь его блок (растёт с актом)
            if (!dealer.IsPlayer && target.IsPlayer && Marked.TryGetValue(dealer, out var ds)
                && ds.Type == Kind.Пронзающий)
            {
                int act = target.CombatState?.RunState?.CurrentActIndex ?? 0;
                int pct = 30 + 5 * Math.Max(0, act - 2);
                __result += decimal.Round(target.Block * pct / 100m);
            }
        }
    }
}
