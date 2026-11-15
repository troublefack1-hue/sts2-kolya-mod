using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

namespace KolyaMod;

// Подсказка на карте: наводишь курсор на элитку (акты 1–5) — видишь, какая поведенческая
// мутация её ждёт (акты 1–2 — ярус I, акты 3–5 — ярус II). Мутация выбирается хэшем
// (сид забега, координаты комнаты) — та же самая применяется в бою
// (BehaviorMutations.IndexFor, Mutations.cs), поэтому подсказка не врёт.
//
// ВАЖНО про устройство: мод собирается БЕЗ Godot source generator (обычный Microsoft.NET.Sdk),
// поэтому свой класс-наследник Node с override _Ready/_Process НЕ ПОЛУЧАЕТ привязок и его
// методы движок не вызывает. Поэтому всё делается патчем ВАНИЛЬНОГО _Process экрана карты:
// он вызывается каждый кадр, а наш UI — обычные PanelContainer/Label (встроенные классы Godot,
// инстанцируются без генератора), которые мы лениво создаём и кладём в дерево один раз.
public static class MutationMapTip
{
    static uint _seed;
    static int _act = -1;   // -1 = неизвестно, подсказку показываем без фильтра по акту

    [HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.Initialize))]
    static class GrabAct
    {
        static void Prefix(MegaCrit.Sts2.Core.Runs.RunState runState)
        {
            _act = runState.CurrentActIndex;
            // сид берём ТОЛЬКО отсюда: SetMap получает сид карты, который может отличаться
            // от сида забега, — тогда хэш подсказки и хэш боя разъезжаются
            _seed = runState.Rng.Seed;
        }
    }

    [HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.SetMap))]
    static class Attach
    {
        static void Postfix(NMapScreen __instance) => Ui.Ensure(__instance);
    }

    // и рабочая логика — на каждом кадре экрана карты
    [HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen._Process))]
    static class Tick
    {
        static int _frames;
        static void Postfix(NMapScreen __instance, double delta)
        {
            try
            {
                _frames++;
                Ui.Update(__instance, _frames);
            }
            catch (System.Exception e)
            {
                if (_frames % 300 == 0)
                    Log.Error($"[kolya_mod] тултип: {e.GetType().Name}: {e.Message}");
            }
        }
    }

    static class Ui
    {
        // поле coord ПУБЛИЧНОЕ (искали только NonPublic — получали null, координат «не было»)
        static readonly FieldInfo? CoordField = typeof(MapPoint).GetField("coord",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        static Control? _panel;
        static Label? _title, _desc;
        static NMapPoint? _for;

        public static void Ensure(NMapScreen screen)
        {
            if (_panel != null && GodotObject.IsInstanceValid(_panel)) return;
            var title = new Label();
            title.AddThemeFontSizeOverride("font_size", 18);
            title.AddThemeColorOverride("font_color", new Color(1f, 0.82f, 0.35f));
            var desc = new Label { CustomMinimumSize = new Vector2(340, 0) };
            desc.AutowrapMode = TextServer.AutowrapMode.Word;
            desc.AddThemeFontSizeOverride("font_size", 14);
            var box = new VBoxContainer();
            box.AddChild(title);
            box.AddChild(desc);
            var panel = new PanelContainer();
            panel.Name = "KolyaMutationTip";
            panel.AddChild(box);
            panel.Visible = false;
            // поверх остального содержимого экрана
            panel.ZIndex = 100;
            screen.AddChild(panel);
            _panel = panel; _title = title; _desc = desc;
            Log.Info("[kolya_mod] тултип мутаций подключён к карте");
        }

        public static void Update(NMapScreen screen, int frames)
        {
            if (_panel == null || !GodotObject.IsInstanceValid(_panel)) return;
            var vp = screen.GetViewport();
            // контрол под мышью может быть дочерним элементом точки — поднимаемся по родителям;
            // если карта в SubViewport, смотрим и внутрь него
            var root = vp.GuiGetHoveredControl();
            if (root is SubViewportContainer svc && svc.GetChild(0) is SubViewport inner)
                root = inner.GuiGetHoveredControl();
            Control? c = root;
            NMapPoint? hovered = null;
            while (c != null)
            {
                if (c is NMapPoint np) { hovered = np; break; }
                c = c.GetParent() as Control;
            }
            // диагностика: раз в ~10 секунд пишем, что реально под мышью
            if (frames % 600 == 0)
            {
                var shown = root;
                Log.Info($"[kolya_mod] тултип: под мышью {(shown == null ? "ничего" : shown.GetType().Name)} (акт {_act})");
            }
            // элитки и боссы непройденных комнат: элитки — ярус по акту (I на 1–2, II на 3–5,
            // III на 6+), боссы — босс-пул с 4-го акта
            if (hovered != null && frames % 300 == 0)
                Log.Info($"[kolya_mod] тултип: точка тип={hovered.Point?.PointType} стейт={hovered.State} coord={(CoordField?.GetValue(hovered.Point) is MapCoord mc ? $"{mc.row},{mc.col}" : "нет")}");
            bool isElite = hovered?.Point?.PointType == MapPointType.Elite;
            bool isBoss = hovered?.Point?.PointType == MapPointType.Boss;
            if (hovered?.Point == null
                || (!isElite && !isBoss)
                || hovered.State == MapPointState.Traveled)
            {
                Hide();
                return;
            }
            var actForTip = _act < 0 ? 0 : _act;
            if (isBoss && actForTip < 3)
            {
                Hide();
                return;
            }
            if (_for != hovered)
            {
                if (CoordField?.GetValue(hovered.Point) is not MapCoord coord)
                {
                    Hide();
                    return;
                }
                string name, desc;
                if (isBoss)
                {
                    var bidx = HeavyMutations.IndexForBoss(_seed, coord.row, coord.col);
                    (name, desc) = HeavyMutations.InfoBoss(bidx, actForTip);
                }
                else if (actForTip >= 5)
                {
                    var iidx = HeavyMutations.IndexFor3(_seed, coord.row, coord.col);
                    (name, desc) = HeavyMutations.Info3(iidx, actForTip);
                }
                else
                {
                    var idx = BehaviorMutations.IndexFor(_seed, coord.row, coord.col);
                    if (actForTip >= 2) idx += BehaviorMutations.TierCount;   // акты 3–5 — ярус II
                    (name, desc) = BehaviorMutations.Info(idx, actForTip);
                }
                _title!.Text = "⚠ Мутация: " + name;
                _desc!.Text = desc;
                _for = hovered;
            }
            _panel.Visible = true;
            var size = _panel.GetCombinedMinimumSize();
            var win = vp.GetVisibleRect().Size;
            // панель — НАД курсором, чтобы не залезать под него
            var pos = vp.GetMousePosition() + new Vector2(16, -size.Y - 14);
            if (pos.Y < 0) pos.Y = vp.GetMousePosition().Y + 20;   // у верхнего края — под курсор
            if (pos.X + size.X > win.X) pos.X -= size.X + 32;
            _panel.Position = pos;
        }

        static void Hide()
        {
            if (_panel != null) _panel.Visible = false;
            _for = null;
        }
    }
}
