using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace GreatHeistAuto
{
    /// <summary>Small helpers to build uGUI from code, used by the menu and the HUD.</summary>
    public static class UiKit
    {
        static Font font;
        public static Font Font => font != null ? font : font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        public static Canvas MakeCanvas(string name, int order = 0)
        {
            var go = new GameObject(name);
            var c = go.AddComponent<Canvas>();
            c.renderMode = RenderMode.ScreenSpaceOverlay;
            c.sortingOrder = order;
            var s = go.AddComponent<CanvasScaler>();
            s.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            s.referenceResolution = new Vector2(1920, 1080);
            s.matchWidthOrHeight = 0.5f;
            go.AddComponent<GraphicRaycaster>();
            return c;
        }

        public static void EnsureEventSystem()
        {
            if (Object.FindAnyObjectByType<EventSystem>() != null) return;
            var go = new GameObject("EventSystem");
            go.AddComponent<EventSystem>();
            go.AddComponent<InputSystemUIInputModule>();
        }

        public static RectTransform Rect(Transform parent, string name, Vector2 anchor, Vector2 pos, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var r = go.GetComponent<RectTransform>();
            r.SetParent(parent, false);
            r.anchorMin = r.anchorMax = anchor;
            r.pivot = anchor;
            r.anchoredPosition = pos;
            r.sizeDelta = size;
            return r;
        }

        public static void Fill(RectTransform r)
        {
            r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one;
            r.offsetMin = Vector2.zero; r.offsetMax = Vector2.zero;
        }

        public static Image Img(Transform parent, string name, Vector2 anchor, Vector2 pos, Vector2 size, Color color)
        {
            var i = Rect(parent, name, anchor, pos, size).gameObject.AddComponent<Image>();
            i.color = color;
            i.raycastTarget = false;
            return i;
        }

        public static Text Label(Transform parent, string name, Vector2 anchor, Vector2 pos, Vector2 size, string text, int fontSize, TextAnchor align, Color color)
        {
            var t = Rect(parent, name, anchor, pos, size).gameObject.AddComponent<Text>();
            t.font = Font; t.text = text; t.fontSize = fontSize; t.alignment = align; t.color = color;
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            var o = t.gameObject.AddComponent<Shadow>();
            o.effectColor = new Color(0, 0, 0, 0.7f);
            o.effectDistance = new Vector2(2, -2);
            return t;
        }

        public static Button MakeButton(Transform parent, string name, Vector2 anchor, Vector2 pos, Vector2 size, string text, int fontSize)
        {
            var r = Rect(parent, name, anchor, pos, size);
            var img = r.gameObject.AddComponent<Image>();
            img.color = new Color(0.08f, 0.08f, 0.09f, 0.82f);
            var b = r.gameObject.AddComponent<Button>();
            var col = b.colors;
            col.normalColor = new Color(1, 1, 1, 1);
            col.highlightedColor = new Color(1f, 0.55f, 0.15f, 1f);
            col.selectedColor = new Color(1f, 0.55f, 0.15f, 1f);
            col.pressedColor = new Color(0.8f, 0.35f, 0.05f, 1f);
            col.fadeDuration = 0.08f;
            b.colors = col;
            var t = Label(r, "Text", new Vector2(.5f, .5f), Vector2.zero, size, text, fontSize, TextAnchor.MiddleCenter, Color.white);
            t.rectTransform.pivot = new Vector2(.5f, .5f);
            var outline = r.gameObject.AddComponent<Outline>();
            outline.effectColor = new Color(1f, 0.55f, 0.15f, 0.9f);
            outline.effectDistance = new Vector2(2, -2);
            return b;
        }
    }
}
