using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace KolyaMod;

// Совместная игра: имена сохраняемых полей передаются по сети номерами из общего словаря игры.
// Регистрируем наше поле с дополнительными зачарованиями и пересчитываем ширину номера.
// (Патч игры 17.11.2026: SavedPropertiesTypeCache заменён на ModelIdSerializationCache,
// регистрация — через CacheSavedPropertiesForTypeDebug.)
public class KolyaSavedFields
{
    [SavedProperty]
    public string KolyaExtraEnchantments { get; set; } = "";

    // Вложенная вторая карта слияния и постоянная кастомная базовая цена.
    [SavedProperty]
    public string KolyaMerged { get; set; } = "";

    [SavedProperty]
    public int KolyaCustomCost { get; set; }

    // Уровни улучшения реликвий (RelicUpgrade). Без регистрации имени сеть не может
    // сопоставить SavedProperty net ID — синхронизация игрока падает, комната не грузится
    // (тёмный экран при входе в бой с улучшенными реликвиями в коопе).
    [SavedProperty]
    public string KolyaRelicUpgrade { get; set; } = "";

    public static void Register()
    {
        ModelIdSerializationCache.CacheSavedPropertiesForTypeDebug(typeof(KolyaSavedFields));
        int count = 0;
        while (true)
        {
            try { ModelIdSerializationCache.GetPropertyNameForNetId(count); count++; }
            catch (ArgumentOutOfRangeException) { break; }
        }
        AccessTools.PropertySetter(typeof(ModelIdSerializationCache), nameof(ModelIdSerializationCache.PropertyIdBitSize))
            .Invoke(null, new object[] { (int)Math.Ceiling(Math.Log2(count)) });
    }
}

// Регистрация откладывается до инициализации кэша игрой: моды грузятся раньше
// (OneTimeInitialization.ExecuteVeryEarly), а кэш — позже (ExecuteEssential).
// Постфикс Init вызывается у всех клиентов одинаково — наши имена добавляются
// к базовым после идентичного набора, номера свойств совпадают у всех игроков.
[HarmonyPatch(typeof(ModelIdSerializationCache), nameof(ModelIdSerializationCache.Init))]
public static class RegisterSavedFields
{
    static bool _done;

    static void Postfix()
    {
        if (_done) return;
        _done = true;
        KolyaSavedFields.Register();
    }
}
