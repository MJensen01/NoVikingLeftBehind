using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// Runtime UI construction, in Valheim's own style, with **no shipped assets**.
    ///
    /// The approach is the one <see cref="SlotsUi"/> and <see cref="PowerRing"/> already use in
    /// this mod: never author art, borrow it. On the first call <see cref="Discover"/> walks the
    /// live Settings menu and remembers one of each control it finds - a Toggle, a Slider, a
    /// Button, a TMP_Text - and every widget below is a clone of one of those. That means the
    /// tab inherits the game's fonts, colours, parchment, hover sounds and UI scale for free, and
    /// keeps inheriting them if a texture pack or a game update changes them.
    ///
    /// Two things must be scrubbed off every clone:
    ///   * its UnityEvents. Instantiate keeps *persistent* (prefab-authored) listeners, and their
    ///     targets are components OUTSIDE the cloned subtree - so a cloned "Enable console"
    ///     toggle would still call GameplaySettings.OnConsoleToggle on the real page. Assigning a
    ///     fresh event object is the only way to drop persistent calls.
    ///   * its tooltip components, which point at in-scene objects belonging to the page they
    ///     came from.
    /// </summary>
    internal static class UiKit
    {
        private static TMP_Text _fontDonor;
        private static Toggle _toggleDonor;
        private static Slider _sliderDonor;
        private static Button _buttonDonor;
        private static Image _panelDonor;

        public static bool Ready { get { return _fontDonor != null; } }

        /// <summary>Text colour taken from the donor label, so a reskin carries over.</summary>
        public static Color TextColor = new Color(1f, 0.94f, 0.8f, 1f);
        public static Color DimColor = new Color(1f, 0.94f, 0.8f, 0.45f);
        public static Color HintColor = new Color(0.78f, 0.72f, 0.6f, 0.85f);
        public static float BaseFontSize = 16f;

        // ---- discovery -------------------------------------------------------------------------

        /// <summary>
        /// Find one of each vanilla control under the live Settings object. Includes inactive
        /// children: only one settings page is active at a time, and the richest donors (the
        /// Accessibility page's eight toggles and its slider) are usually on a hidden one.
        /// </summary>
        public static void Discover(GameObject settingsRoot)
        {
            _fontDonor = null; _toggleDonor = null; _sliderDonor = null; _buttonDonor = null; _panelDonor = null;
            if (settingsRoot == null) return;

            // The font donor decides BaseFontSize, and BaseFontSize decides whether a label fits
            // the rect it was given. Up to 0.7.3 this took the FIRST TMP_Text in the hierarchy,
            // which is the page's big "Settings" heading (~34px). Every label then came out at
            // 0.72-0.95 of that - 25 to 32px - inside rows 22 to 28px tall, and TMP with
            // TextOverflowModes.Ellipsis draws NOTHING at all when even the first line will not
            // fit its rect. That, and not draw order or rect width, is why row labels, hints,
            // module names and the search placeholder were blank while the taller labels (the
            // theme headings, the greyed reason, the audit strip, the tooltip) came through.
            //
            // So take the MEDIAN size of the ordinary labels - the page's body text, which the
            // one big heading cannot skew - and clamp it, because nothing on this page is laid
            // out for text bigger than 22px.
            var labels = new System.Collections.Generic.List<TMP_Text>();
            foreach (var t in settingsRoot.GetComponentsInChildren<TMP_Text>(true))
            {
                if (t == null || string.IsNullOrEmpty(t.text)) continue;
                if (t.GetComponentInParent<Toggle>() != null) continue;   // prefer a standalone label
                if (t.fontSize <= 1f) continue;
                labels.Add(t);
            }
            if (labels.Count > 0)
            {
                labels.Sort(delegate (TMP_Text a, TMP_Text b) { return a.fontSize.CompareTo(b.fontSize); });
                _fontDonor = labels[labels.Count / 2];
            }
            if (_fontDonor == null)
            {
                var any = settingsRoot.GetComponentsInChildren<TMP_Text>(true);
                if (any.Length > 0) _fontDonor = any[0];
            }

            foreach (var t in settingsRoot.GetComponentsInChildren<Toggle>(true)) { _toggleDonor = t; break; }
            foreach (var s in settingsRoot.GetComponentsInChildren<Slider>(true)) { _sliderDonor = s; break; }
            foreach (var b in settingsRoot.GetComponentsInChildren<Button>(true)) { _buttonDonor = b; break; }
            foreach (var i in settingsRoot.GetComponentsInChildren<Image>(true))
            {
                if (i == null || i.sprite == null) continue;
                _panelDonor = i;
                break;
            }

            if (_fontDonor != null)
            {
                TextColor = _fontDonor.color;
                float raw = _fontDonor.fontSize > 1 ? _fontDonor.fontSize : 16f;
                BaseFontSize = Mathf.Clamp(raw, 14f, 22f);
                DimColor = new Color(TextColor.r, TextColor.g, TextColor.b, 0.4f);
                HintColor = new Color(TextColor.r * 0.88f, TextColor.g * 0.85f, TextColor.b * 0.78f, 0.85f);

                NoVikingLeftBehindPlugin.Log.LogInfo("[SettingsMenu] font donor '" + _fontDonor.name +
                    "' text=\"" + Clip(_fontDonor.text, 18) + "\" size=" + raw.ToString("0.#") +
                    " (median of " + labels.Count + " labels) -> BaseFontSize=" + BaseFontSize.ToString("0.#") +
                    ", colour=" + ColorUtility.ToHtmlStringRGBA(TextColor));
            }
        }

        private static string Clip(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\n", " ");
            return s.Length <= n ? s : s.Substring(0, n) + "...";
        }

        // ---- primitives ---------------------------------------------------------------------------

        public static RectTransform Panel(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            rt.localScale = Vector3.one;
            return rt;
        }

        /// <summary>Anchor a rect to a corner with an explicit size (top-left origin, y down).</summary>
        public static RectTransform Place(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
            return rt;
        }

        public static TMP_Text Label(Transform parent, string text, float size, TextAlignmentOptions align,
                                     Color? color = null)
        {
            if (_fontDonor == null) return null;
            var clone = UnityEngine.Object.Instantiate(_fontDonor.gameObject, parent, false);
            clone.name = "NVLB_Label";
            Scrub(clone);
            var t = clone.GetComponent<TMP_Text>();
            t.text = text;
            t.fontSize = size;
            t.alignment = align;
            t.color = color.HasValue ? color.Value : TextColor;
            t.enableAutoSizing = false;
            t.richText = false;
            t.raycastTarget = false;
            t.overflowMode = TextOverflowModes.Ellipsis;
            // Single line by default. With wrapping ON, Ellipsis makes TMP draw nothing at all
            // when the wrapped text is taller than the rect - one label slightly too big for its
            // row and the whole row goes blank. With wrapping OFF it truncates sideways instead,
            // which is what a settings row wants anyway. The tooltip turns it back on.
            t.enableWordWrapping = false;
            var rt = (RectTransform)clone.transform;
            rt.localScale = Vector3.one;
            return t;
        }

        public static Toggle Toggle(Transform parent)
        {
            if (_toggleDonor == null) return null;
            var clone = UnityEngine.Object.Instantiate(_toggleDonor.gameObject, parent, false);
            clone.name = "NVLB_Toggle";
            Scrub(clone);
            var t = clone.GetComponent<Toggle>();
            t.onValueChanged = new Toggle.ToggleEvent();
            t.group = null;
            // A vanilla settings toggle is a bare checkbox, but if the donor carried a caption,
            // blank it - our own label sits to the left of the control.
            foreach (var txt in clone.GetComponentsInChildren<TMP_Text>(true)) txt.text = "";
            ((RectTransform)clone.transform).localScale = Vector3.one;
            return t;
        }

        public static Slider Slider(Transform parent)
        {
            if (_sliderDonor == null) return null;
            var clone = UnityEngine.Object.Instantiate(_sliderDonor.gameObject, parent, false);
            clone.name = "NVLB_Slider";
            Scrub(clone);
            var s = clone.GetComponent<Slider>();
            s.onValueChanged = new Slider.SliderEvent();
            s.wholeNumbers = false;
            foreach (var txt in clone.GetComponentsInChildren<TMP_Text>(true)) txt.text = "";
            ((RectTransform)clone.transform).localScale = Vector3.one;
            return s;
        }

        /// <param name="fontSize">
        /// Explicit caption size. Left at 0 the caption auto-sizes down to fit, which is right for
        /// a one-glyph reset button but wrong for a worded button: the donor's own auto-size floor
        /// shrank "Undo last change" to about 11px, out of step with every other label on the page.
        /// </param>
        public static Button Button(Transform parent, string caption, float fontSize = 0f)
        {
            if (_buttonDonor == null) return null;
            var clone = UnityEngine.Object.Instantiate(_buttonDonor.gameObject, parent, false);
            clone.name = "NVLB_Button";
            Scrub(clone);
            var b = clone.GetComponent<Button>();
            b.onClick = new Button.ButtonClickedEvent();
            b.interactable = true;
            foreach (var txt in clone.GetComponentsInChildren<TMP_Text>(true))
            {
                txt.text = caption;
                if (fontSize > 0f)
                {
                    txt.enableAutoSizing = false;
                    txt.fontSize = fontSize;
                }
                else
                {
                    txt.enableAutoSizing = true;
                    txt.fontSizeMin = 8f;
                }
                txt.overflowMode = TextOverflowModes.Ellipsis;
                txt.enableWordWrapping = false;
            }
            ((RectTransform)clone.transform).localScale = Vector3.one;
            return b;
        }

        /// <summary>A plain tinted rectangle - separators, row stripes, the tooltip backdrop.</summary>
        public static Image Fill(Transform parent, Color color)
        {
            var go = new GameObject("NVLB_Fill", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            // Deliberately flat: the donor sprites on a settings page are checkboxes and slider
            // tracks, and stretching one of those behind a tooltip looks like a bug.
            var img = go.GetComponent<Image>();
            img.color = color;
            img.raycastTarget = false;
            ((RectTransform)go.transform).localScale = Vector3.one;
            return img;
        }

        /// <summary>
        /// A text field. There is no InputField anywhere on the vanilla settings pages (checked
        /// across Gameplay / Audio / Graphics / Accessibility), so this one is assembled from
        /// primitives and a cloned label, then styled from the same donor.
        /// </summary>
        public static TMP_InputField Input(Transform parent, float width, float height)
        {
            if (_fontDonor == null) return null;

            var root = new GameObject("NVLB_Input", typeof(RectTransform), typeof(Image));
            root.transform.SetParent(parent, false);
            root.SetActive(false);                       // build it whole before TMP_InputField wakes
            var rootRt = (RectTransform)root.transform;
            rootRt.sizeDelta = new Vector2(width, height);
            rootRt.localScale = Vector3.one;

            var bg = root.GetComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.35f);
            bg.raycastTarget = true;

            var viewport = new GameObject("TextArea", typeof(RectTransform), typeof(RectMask2D));
            viewport.transform.SetParent(root.transform, false);
            var vpRt = (RectTransform)viewport.transform;
            vpRt.anchorMin = Vector2.zero;
            vpRt.anchorMax = Vector2.one;
            vpRt.offsetMin = new Vector2(6f, 2f);
            vpRt.offsetMax = new Vector2(-6f, -2f);

            var text = Label(viewport.transform, "", BaseFontSize * 0.9f, TextAlignmentOptions.MidlineLeft);
            text.name = "Text";
            text.raycastTarget = false;
            text.overflowMode = TextOverflowModes.Overflow;
            Stretch((RectTransform)text.transform);

            var placeholder = Label(viewport.transform, "", BaseFontSize * 0.9f,
                                    TextAlignmentOptions.MidlineLeft, DimColor);
            placeholder.name = "Placeholder";
            placeholder.raycastTarget = false;
            Stretch((RectTransform)placeholder.transform);

            var input = root.AddComponent<TMP_InputField>();
            input.textViewport = vpRt;
            input.textComponent = text;
            input.placeholder = placeholder;
            input.lineType = TMP_InputField.LineType.SingleLine;
            input.richText = false;
            input.caretWidth = 2;
            input.customCaretColor = true;
            input.caretColor = TextColor;
            input.selectionColor = new Color(TextColor.r, TextColor.g, TextColor.b, 0.3f);
            input.restoreOriginalTextOnEscape = true;

            root.SetActive(true);
            return input;
        }

        public static RectTransform Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            return rt;
        }

        /// <summary>A vertical scroll area. Returns the content rect to fill with rows.</summary>
        public static ScrollRect Scroll(Transform parent, string name, out RectTransform content)
        {
            var root = new GameObject(name, typeof(RectTransform), typeof(ScrollRect), typeof(RectMask2D));
            root.transform.SetParent(parent, false);
            ((RectTransform)root.transform).localScale = Vector3.one;

            var viewport = new GameObject("Viewport", typeof(RectTransform));
            viewport.transform.SetParent(root.transform, false);
            var vp = Stretch((RectTransform)viewport.transform);

            var contentGo = new GameObject("Content", typeof(RectTransform));
            contentGo.transform.SetParent(viewport.transform, false);
            content = (RectTransform)contentGo.transform;
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0f, 1f);
            content.offsetMin = new Vector2(0f, 0f);
            content.offsetMax = new Vector2(0f, 0f);

            var sr = root.GetComponent<ScrollRect>();
            sr.viewport = vp;
            sr.content = content;
            sr.horizontal = false;
            sr.vertical = true;
            sr.movementType = ScrollRect.MovementType.Clamped;
            sr.scrollSensitivity = 30f;
            return sr;
        }

        /// <summary>Total height of a content rect's children, so we can size it for scrolling.</summary>
        public static void FitContent(RectTransform content, float height)
        {
            content.sizeDelta = new Vector2(content.sizeDelta.x, Mathf.Max(0f, height));
        }

        // ---- cleaning clones -------------------------------------------------------------------------

        /// <summary>
        /// Strip everything a clone should not have brought with it: tooltips that point at the
        /// donor page's in-scene objects, and any behaviour that would write a vanilla setting.
        /// </summary>
        private static void Scrub(GameObject clone)
        {
            foreach (var t in clone.GetComponentsInChildren<Valheim.SettingsGui.SettingsTooltip>(true))
                UnityEngine.Object.DestroyImmediate(t);
            foreach (var t in clone.GetComponentsInChildren<UITooltip>(true))
                UnityEngine.Object.DestroyImmediate(t);
        }

        // ---- hover tooltip ----------------------------------------------------------------------------

        /// <summary>
        /// A tooltip that needs nothing from the scene. Vanilla's <c>UITooltip</c> only works when
        /// it can be handed an <c>m_tooltipPrefab</c> from another live tooltip, and the main menu
        /// does not reliably have one - so the tab carries its own panel, built from the same
        /// cloned label and backdrop as everything else, and shows it under the cursor.
        /// </summary>
        internal sealed class Hover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
        {
            public string Text;
            public static RectTransform Panel;

            /// <summary>
            /// A page-local rect the tooltip must not cover - the search box, so a hover on the
            /// first row cannot hide what you are typing. Same coordinate space as the panel's
            /// anchoredPosition: x right from the page's left edge, y negative downward from its top.
            /// </summary>
            public static Rect Avoid = new Rect(0f, 0f, 0f, 0f);

            private static TMP_Text _panelText;
            private static float _showAt;
            private static Hover _current;

            public void OnPointerEnter(PointerEventData eventData)
            {
                _current = this;
                _showAt = Time.unscaledTime + 0.25f;
            }

            public void OnPointerExit(PointerEventData eventData)
            {
                if (_current == this) { _current = null; HidePanel(); }
            }

            private void OnDisable()
            {
                if (_current == this) { _current = null; HidePanel(); }
            }

            /// <summary>Build the shared panel once, as a child of the page.</summary>
            public static void EnsurePanel(Transform pageRoot)
            {
                if (Panel != null) return;
                var img = Fill(pageRoot, new Color(0.06f, 0.05f, 0.04f, 0.96f));
                img.gameObject.name = "NVLB_Tooltip";
                Panel = (RectTransform)img.transform;
                Panel.pivot = new Vector2(0f, 1f);
                Panel.anchorMin = new Vector2(0f, 1f);
                Panel.anchorMax = new Vector2(0f, 1f);
                Panel.sizeDelta = new Vector2(420f, 120f);

                _panelText = Label(Panel, "", Mathf.Clamp(BaseFontSize * 0.85f, 12f, 18f),
                                   TextAlignmentOptions.TopLeft);
                var trt = (RectTransform)_panelText.transform;
                Stretch(trt);
                trt.offsetMin = new Vector2(10f, 10f);
                trt.offsetMax = new Vector2(-10f, -8f);
                _panelText.overflowMode = TextOverflowModes.Overflow;
                _panelText.enableWordWrapping = true;

                Panel.gameObject.SetActive(false);
            }

            public static void HidePanel()
            {
                if (Panel != null) Panel.gameObject.SetActive(false);
            }

            /// <summary>Driven from the tab's Update so there is exactly one ticking behaviour.</summary>
            public static void Tick()
            {
                if (Panel == null) return;
                if (_current == null || string.IsNullOrEmpty(_current.Text)) { HidePanel(); return; }
                if (Time.unscaledTime < _showAt) return;

                if (!Panel.gameObject.activeSelf)
                {
                    _panelText.text = _current.Text;
                    Panel.gameObject.SetActive(true);
                    Panel.SetAsLastSibling();
                    _panelText.ForceMeshUpdate();
                    float wanted = Mathf.Clamp(_panelText.GetPreferredValues(_current.Text, 400f, 0f).y + 20f, 40f, 400f);
                    Panel.sizeDelta = new Vector2(420f, wanted);
                }

                var parent = Panel.parent as RectTransform;
                if (parent == null) return;
                Vector2 local;
                var cam = ScreenCamera(parent);
                if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                        parent, UnityEngine.Input.mousePosition, cam, out local)) return;

                // ScreenPointToLocalPointInRectangle answers in coordinates relative to the
                // parent's PIVOT, but this panel is anchored to the parent's TOP-LEFT corner.
                // Feeding one straight into the other is what pinned the tooltip in the corner of
                // the screen instead of putting it under the cursor. Convert once, here.
                float w = parent.rect.width, h = parent.rect.height;
                float px = local.x + w * parent.pivot.x;            // distance right of the left edge
                float py = local.y - h * (1f - parent.pivot.y);     // distance below the top edge (<= 0)

                float tw = Panel.sizeDelta.x, th = Panel.sizeDelta.y;
                float x = px + 18f;
                if (x + tw > w) x = px - 18f - tw;                  // flip to the cursor's left
                x = Mathf.Clamp(x, 0f, Mathf.Max(0f, w - tw));
                float y = py - 12f;
                if (y - th < -h) y = py + 12f + th;                 // flip above the cursor
                y = Mathf.Clamp(y, Mathf.Min(0f, -h + th), 0f);

                // Never sit on top of something the player is reading or typing into.
                if (Avoid.width > 0f && new Rect(x, y - th, tw, th).Overlaps(Avoid))
                {
                    float below = Avoid.yMin - 4f;
                    y = (below - th >= -h) ? below : Avoid.yMax + 4f + th;
                }

                Panel.anchoredPosition = new Vector2(x, y);
            }

            private static Camera ScreenCamera(RectTransform rt)
            {
                var canvas = rt.GetComponentInParent<Canvas>();
                if (canvas == null) return null;
                return canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            }
        }

        /// <summary>Attach hover text to anything with a raycast target.</summary>
        public static void Tip(GameObject go, string text)
        {
            if (go == null) return;
            if (string.IsNullOrEmpty(text))
            {
                var old = go.GetComponent<Hover>();
                if (old != null) old.Text = null;      // clear it, do not leave a stale tooltip
                return;
            }
            var img = go.GetComponent<Image>();
            if (img == null)
            {
                img = go.AddComponent<Image>();
                img.color = new Color(0f, 0f, 0f, 0f);   // invisible, but hit-testable
            }
            img.raycastTarget = true;
            var h = go.GetComponent<Hover>();
            if (h == null) h = go.AddComponent<Hover>();
            h.Text = text;
        }
    }
}
