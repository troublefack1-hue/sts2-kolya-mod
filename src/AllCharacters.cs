using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Unlocks;

namespace KolyaMod;

// В профиле модов прогресс начинается с нуля и открыт один персонаж; открываем всех.
[HarmonyPatch(typeof(UnlockState), nameof(UnlockState.Characters), MethodType.Getter)]
public static class AllCharacters
{
    static IEnumerable<CharacterModel> Postfix(IEnumerable<CharacterModel> __result) => ModelDb.AllCharacters;
}
