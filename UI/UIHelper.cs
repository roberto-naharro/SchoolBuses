using ColossalFramework.UI;
using UnityEngine;

namespace SchoolBuses.UI
{
    // Small UI construction helpers in the vanilla style. Mirrors the button/checkbox
    // recipes used by IPT's UIUtils but sources the font from the active UIView so it
    // works without any transport panel being open.
    internal static class UIHelper
    {
        private static UIFont _font;

        internal static UIFont Font
        {
            get
            {
                if (_font == null)
                {
                    UIView view = UIView.GetAView();
                    if (view != null)
                        _font = view.defaultFont;
                }
                return _font;
            }
        }

        internal static readonly Color32 White = new Color32(255, 255, 255, 255);
        internal static readonly Color32 Green = new Color32(90, 200, 90, 255);
        internal static readonly Color32 Amber = new Color32(240, 180, 40, 255);
        internal static readonly Color32 Red = new Color32(220, 80, 80, 255);

        internal static UIButton CreateButton(UIComponent parent)
        {
            UIButton b = parent.AddUIComponent<UIButton>();
            b.font = Font;
            b.textPadding = new RectOffset(8, 8, 4, 0);
            b.normalBgSprite = "ButtonMenu";
            b.disabledBgSprite = "ButtonMenuDisabled";
            b.hoveredBgSprite = "ButtonMenuHovered";
            b.focusedBgSprite = "ButtonMenu";
            b.pressedBgSprite = "ButtonMenuPressed";
            b.textColor = White;
            b.disabledTextColor = new Color32(120, 120, 120, 255);
            b.hoveredTextColor = White;
            b.focusedTextColor = White;
            b.pressedTextColor = new Color32(200, 200, 200, 255);
            b.textScale = 0.8125f;
            return b;
        }

        internal static UICheckBox CreateCheckBox(UIComponent parent)
        {
            UICheckBox cb = parent.AddUIComponent<UICheckBox>();
            cb.height = 18f;
            cb.clipChildren = true;

            UISprite uncheck = cb.AddUIComponent<UISprite>();
            uncheck.spriteName = "check-unchecked";
            uncheck.size = new Vector2(16f, 16f);
            uncheck.relativePosition = Vector3.zero;

            cb.checkedBoxObject = uncheck.AddUIComponent<UISprite>();
            ((UISprite)cb.checkedBoxObject).spriteName = "check-checked";
            cb.checkedBoxObject.size = new Vector2(16f, 16f);
            cb.checkedBoxObject.relativePosition = Vector3.zero;

            cb.label = cb.AddUIComponent<UILabel>();
            cb.label.font = Font;
            cb.label.textColor = White;
            cb.label.textScale = 0.8125f;
            cb.label.relativePosition = new Vector3(22f, 2f);
            return cb;
        }

        internal static UILabel CreateLabel(UIComponent parent, float textScale)
        {
            UILabel l = parent.AddUIComponent<UILabel>();
            l.font = Font;
            l.textColor = White;
            l.textScale = textScale;
            l.autoSize = false;
            l.wordWrap = true;
            return l;
        }
    }
}
