using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

namespace KolyaMod;

// Подсказка на карте: наводишь курсор на элитку (акты 1–3) — видишь, какая поведенческая
// мутация её ждёт. Мутация выбирается хэшем (сид забега, координаты комнаты) — та же самая
// применяется в бою (BehaviorMutations.IndexFor, Mutations.cs), поэтому подсказка не врёт.
// Реализация: свой Godot-узел добавляется к NMapScreen в его _Ready; каждый кадр смотрит,
// какой Control под мышью (GuiGetHoveredControl) — если это NMapPoint элитки, показывает
// панель рядом с курсором. Сид и акт подхватываются из Initialize/SetMap экрана карты.
public static class MutationMapTip
{
    static uint _seed;
    static int _act = -1;   // -1 = неизвестно, подсказку показываем без фильтра по акту

    [HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.Initialize))]
    static class GrabAct
    {
        static void Prefix(MegaCrit.Sts2.Core.Runs.RunState runState) =>
            _act = runState.CurrentActIndex;
    }

    [HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.SetMap))]
    static class GrabSeed
    {
        static void Prefix(uint seed) => _seed = seed;
    }

    [HarmonyPatch(typeof(NMapScreen), "_Ready")]
    static class Attach
    {
        static void Postfix(NMapScreen __instance)
        {
            __instance.AddChild(new TipNode());
            Log.Info("[kolya_mod] тултип мутаций подключён к карте");
        }
    }

    class TipNode : Node
    {
        static readonly FieldInfo? CoordField = typeof(MapPoint).GetField("coord",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Control _panel = null!;
        Label _title = null!;
        Label _desc = null!;
        NMapPoint? _for;

        public override void _Ready()
        {
            _title = new Label { Text = "Мутация" };
            _title.AddThemeFontSizeOverride("font_size", 18);
            _desc = new Label { CustomMinimumSize = new Vector2(340, 0) };
            _desc.AutowrapMode = TextServer.AutowrapMode.Word;
            _desc.AddThemeFontSizeOverride("font_size", 14);
            var box = new VBoxContainer();
            box.AddChild(_title);
            box.AddChild(_desc);
            _panel = new PanelContainer();
            _panel.AddChild(box);
            _panel.Visible = false;
            AddChild(_panel);
        }

        public override void _Process(double delta)
        {
            var vp = GetViewport();
            var hovered = vp.GuiGetHoveredControl() as NMapPoint;
            // показываем только элитки непройденных комнат; мутации есть на актах 1–3
            if (hovered?.Point == null
                || hovered.Point.PointType != MapPointType.Elite
                || hovered.State == MapPointState.Traveled
                || _act >= 4)
            {
                HideTip();
                return;
            }
            if (_for != hovered)
            {
                if (CoordField?.GetValue(hovered.Point) is not MapCoord coord)
                {
                    HideTip();
                    return;
                }
                var idx = BehaviorMutations.IndexFor(_seed, coord.row, coord.col);
                var (name, desc) = BehaviorMutations.Info(idx);
                _title.Text = "⚠ Мутация: " + name;
                _desc.Text = desc;
                _for = hovered;
            }
            _panel.Visible = true;
            var pos = vp.GetMousePosition() + new Vector2(18, 14);
            var size = _panel.GetCombinedMinimumSize();
            var win = vp.GetVisibleRect().Size;
            if (pos.X + size.X > win.X) pos.X -= size.X + 36;
            if (pos.Y + size.Y > win.Y) pos.Y -= size.Y + 28;
            _panel.Position = pos;
        }

        void HideTip()
        {
            _panel.Visible = false;
            _for = null;
        }
    }
}
