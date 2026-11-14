using System.Linq;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace KolyaMod;

// Награда за мутированную элиту как КНОПКА на экране наград за бой (рядом с золотом, картами,
// реликвиями) — жалоба Коли: отдельное окно выбора после боя игроки не понимали и закрывали,
// не выбрав карту. Теперь игрок сам жмёт кнопку «Трофей мутации», и только тогда открывается
// выбор карты колоды с поясняющим текстом. Кнопку можно проигнорировать — она просто останется
// на экране (OnSelect возвращает false, награда не списывается).
public class EnchantReward : Reward
{
    // маркер в SerializableReward.PredeterminedModelId — по нему награда восстанавливается
    // из сохранения (ванильный FromSerializable неизвестный тип вернёт null — postfix ниже).
    public static readonly ModelId MarkerId = new("kolya_mod", "ENCHANT_REWARD");

    static string? _iconPath;

    public EnchantReward(Player player) : base(player) { }

    protected override RewardType RewardType => RewardType.None;
    public override int RewardsSetIndex => 30;   // после ванильных кнопок (23–27)
    public override LocString Description => new("rewards_ui", "KOLYA_ENCHANT_REWARD");
    public override bool IsPopulated => true;
    public override void Populate() { }
    public override void MarkContentAsSeen() { }

    public override Control CreateIcon()
    {
        _iconPath ??= ModelDb.Enchantment<Glam>().IconPath;
        return new TextureRect
        {
            Texture = GD.Load<Texture2D>(_iconPath),
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = new Vector2(64, 64),
        };
    }

    protected override async Task<bool> OnSelect()
    {
        var picks = await CardSelectCmd.FromDeckGeneric(Player,
            new CardSelectorPrefs(new LocString("rest_site_ui", "KOLYA_ENCHANT_REWARD_PROMPT"), 1)
            { Cancelable = true },
            c => FifthUpgradeEnchant.Options(c).Count > 0);
        var card = picks.FirstOrDefault();
        if (card == null) return false;   // отмена — кнопка остаётся на экране наград
        var pick = FifthUpgradeEnchant.GiveRandom(card);
        Log.Info($"[kolya_mod] трофей мутации: {card.Id} получает {pick?.Id}");
        return true;
    }

    public override SerializableReward ToSerializable()
    {
        var save = base.ToSerializable();          // RewardType.None
        save.PredeterminedModelId = MarkerId;
        return save;
    }
}

// Восстановление кнопки из сохранения: ванильный FromSerializable для неизвестного типа
// возвращает null (с записью в лог) — подменяем на нашу награду по маркеру.
[HarmonyPatch(typeof(Reward), nameof(Reward.FromSerializable))]
public static class EnchantRewardLoad
{
    static void Postfix(SerializableReward save, Player player, ref Reward __result)
    {
        if (__result == null && save.PredeterminedModelId == EnchantReward.MarkerId)
            __result = new EnchantReward(player);
    }
}
