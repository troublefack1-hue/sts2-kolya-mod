using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace KolyaMod;

[ModInitializer(nameof(Init))]
public static class ModEntry
{
    public static void Init()
    {
        KolyaSavedFields.Register();
        new Harmony("kolya.kolya_mod").PatchAll();
        Log.Info("[kolya_mod] заготовка загружена");
    }
}
