using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Rooms;

namespace KolyaMod;

// Забег удлинён до 10 актов (MoreActs), а ваниль умеет только 3: комната сокровищ на 4+ акте
// бросает ArgumentOutOfRangeException («must be between 0 and 2») и комната не создаётся —
// чёрный экран. Клампим actIndex до последнего ванильного (таблицы сокровищ 3-го акта).
[HarmonyPatch(typeof(TreasureRoom), MethodType.Constructor, new[] { typeof(int) })]
public static class TreasureRoomActClamp
{
    static void Prefix(ref int actIndex)
    {
        if (actIndex > 2)
        {
            Log.Info($"[kolya_mod] TreasureRoom: акт {actIndex + 1} клампится до 3");
            actIndex = 2;
        }
    }
}

// Костёр: анимация персонажа выбирается switch'ем по индексу акта (0..2), дефолт бросает
// InvalidOperationException («Unexpected act») — после неё не подключаются сигналы фокуса/мыши.
// Для актов 3+ используем анимацию 3-го акта (glory_loop).
[HarmonyPatch(typeof(NRestSiteCharacter), "_Ready")]
public static class RestSiteAnimActClamp
{
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var codes = instructions.ToList();
        // stloc, куда кейсы складывают имя анимации — берём его из кейса "overgrowth_loop"
        int caseIdx = codes.FindIndex(c => c.opcode == OpCodes.Ldstr && (string?)c.operand == "overgrowth_loop");
        CodeInstruction? stlocAnim = caseIdx >= 0 && caseIdx + 1 < codes.Count ? codes[caseIdx + 1] : null;

        int bad = codes.FindIndex(c => c.opcode == OpCodes.Ldstr && (string?)c.operand == "Unexpected act");
        if (bad >= 0 && stlocAnim != null && stlocAnim.opcode.Name!.StartsWith("stloc"))
        {
            // ldstr "Unexpected act"; newobj InvalidOperationException..ctor; throw
            //   -> ldstr "glory_loop"; stloc animName (падение на конец switch'а).
            // ВАЖНО: не клонируем инструкцию — ярлыки (метки переходов) нельзя дублировать,
            // иначе Harmony падает с «Bad label content in ILGenerator».
            codes[bad] = new CodeInstruction(OpCodes.Ldstr, "glory_loop") { labels = codes[bad].labels };
            codes[bad + 1] = new CodeInstruction(stlocAnim.opcode, stlocAnim.operand);
            codes.RemoveAt(bad + 2); // throw
        }
        return codes;
    }
}
