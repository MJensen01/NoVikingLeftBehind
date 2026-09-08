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

            // A cloned vanilla toggle answers a click anywhere on any of its graphics, and the
            // donor's are as wide as the row it came from - so picking a module by name kept
            // flipping its checkbox instead. Take the raycast off all of them and give the toggle
            // exactly one hit patch, over the box itself with a few pixels of grace. The graphics
            // still tint and still show the tick; they simply stop catching clicks meant for the row.
            foreach (var g in clone.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;

            var hit = new GameObject("NVLB_ToggleHit", typeof(RectTransform), typeof(Image));
            var hrt = (RectTransform)hit.transform;
            // targetGraphic first: that is the box itself, always on screen. The checkmark is
            // only faded in and out, but it is the one a skin is most likely to switch off
            // outright - and a hit patch parented to something switched off is not clickable.
            var box = (t.targetGraphic != null ? t.targetGraphic : t.graphic);
            if (box != null)
            {
                hrt.SetParent(box.transform, false);       // exactly where the box is drawn
                Stretch(hrt);
                hrt.offsetMin = new Vector2(-4f, -4f);
                hrt.offsetMax = new Vector2(4f, 4f);
            }
            else
            {
                hrt.SetParent(clone.transform, false);     // no box to find: a 28px patch, centred
                hrt.anchorMin = new Vector2(0.5f, 0.5f);
                hrt.anchorMax = new Vector2(0.5f, 0.5f);
                hrt.pivot = new Vector2(0.5f, 0.5f);
                hrt.anchoredPosition = Vector2.zero;
                hrt.sizeDelta = new Vector2(28f, 28f);
            }
            hrt.localScale = Vector3.one;
            var hitImg = hit.GetComponent<Image>();
            hitImg.color = new Color(0f, 0f, 0f, 0f);
            hitImg.raycastTarget = true;

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

            // Same treatment as the toggle, for the same reason. The donor is a whole vanilla
            // settings row, so its children reach left of the track - and a Slider answers a
            // press ANYWHERE on its own graphics by jumping the value to wherever that press
            // maps to, which off the left end of the track means the minimum. That is why
            // clicking a setting's NAME kept dragging its slider to the far left: the click was
            // never on the label at all, it was on a piece of the slider stretched out underneath
            // it. Take the raycast off every one of them and give the slider exactly one patch,
            // over the handle's own container - which is the track, and is what Slider maps a
            // press against, so clicking and dragging still land on the right value.
            foreach (var g in clone.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;

            var track = (s.handleRect != null && s.handleRect.parent is RectTransform)
                            ? (RectTransform)s.handleRect.parent
                            : (s.fillRect != null && s.fillRect.parent is RectTransform)
                                ? (RectTransform)s.fillRect.parent
                                : (RectTransform)clone.transform;

            var hit = new GameObject("NVLB_SliderHit", typeof(RectTransform), typeof(Image));
            var hrt = (RectTransform)hit.transform;
            hrt.SetParent(track, false);
            Stretch(hrt);
            hrt.offsetMin = new Vector2(0f, -9f);          // a little taller than the track itself,
            hrt.offsetMax = new Vector2(0f, 9f);           // so it is not fiddly to grab
            hrt.localScale = Vector3.one;
            var hitImg = hit.GetComponent<Image>();
            hitImg.color = new Color(0f, 0f, 0f, 0f);
            hitImg.raycastTarget = true;

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
                if (fontSize > 0f)
                {
                    // The donor's caption rect can be barely taller than its own auto-sized text.
                    // Pinning a real font size into a rect that short makes TMP draw nothing at
                    // all, which is exactly how the footer buttons came out blank - so give the
                    // caption the whole button to sit in before setting the size.
                    var crt = (RectTransform)txt.transform;
                    Stretch(crt);
                    crt.offsetMin = new Vector2(8f, 3f);
                    crt.offsetMax = new Vector2(-8f, -3f);
                    txt.enableAutoSizing = false;
                    txt.fontSize = fontSize;
                    txt.alignment = TextAlignmentOptions.Center;
                }
                else
                {
                    txt.enableAutoSizing = true;
                    txt.fontSizeMin = 8f;
                }
                txt.overflowMode = TextOverflowModes.Ellipsis;
                txt.enableWordWrapping = false;
                txt.text = caption;          // last, so nothing above can clear it
            }
            ((RectTransform)clone.transform).localScale = Vector3.one;
            return b;
        }

        /// <summary>
        /// The small patch that actually takes a toggle's clicks. Anything hung on a toggle -
        /// a tooltip, most of all - must go here rather than on the toggle's own object, or it
        /// switches the raycast back on across the whole widget and the row becomes one big
        /// checkbox again.
        /// </summary>
        public static GameObject HitAreaOf(Toggle t)
        {
            if (t == null) return null;
            var found = t.transform.Find("NVLB_ToggleHit");
            if (found == null)
                foreach (var rt in t.GetComponentsInChildren<RectTransform>(true))
                    if (rt != null && rt.name == "NVLB_ToggleHit") { found = rt; break; }
            return found != null ? found.gameObject : t.gameObject;
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
            var root = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(ScrollRect), typeof(RectMask2D));
            root.transform.SetParent(parent, false);
            ((RectTransform)root.transform).localScale = Vector3.one;

            // An invisible but hit-testable backing, so the pane is something the pointer can be
            // "over" even in the gaps between rows.
            var bg = root.GetComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0f);
            bg.raycastTarget = true;

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
            sr.inertia = false;                 // vanilla's settings pages do not glide either
            // Unity's own OnScroll is switched off deliberately: it only fires when the pointer
            // lands on a raycast target that bubbles up to here, and the amount it moves depends
            // on whatever the game's input module puts in scrollDelta - which Valheim scales by
            // 0.15 (ZInput's "MouseScrollDelta" action), so a notch moved the page by a couple of
            // pixels. TickScroll below reads the wheel itself and moves by a whole row.
            sr.scrollSensitivity = 0f;
            return sr;
        }

        /// <summary>
        /// A slim "there is more below" indicator down the right edge of a pane. Not draggable -
        /// it is there so nobody has to guess that 39 modules do not fit - and it hides itself
        /// when everything already fits. Returns the knob, to hand back to <see cref="TickScroll"/>.
        /// </summary>
        public static RectTransform ScrollBar(ScrollRect sr)
        {
            if (sr == null) return null;

            var track = Fill(sr.transform, new Color(TextColor.r, TextColor.g, TextColor.b, 0.10f));
            track.gameObject.name = "NVLB_ScrollTrack";
            var trt = (RectTransform)track.transform;
            trt.anchorMin = new Vector2(1f, 0f);
            trt.anchorMax = new Vector2(1f, 1f);
            trt.pivot = new Vector2(1f, 0.5f);
            trt.offsetMin = new Vector2(-5f, 2f);
            trt.offsetMax = new Vector2(-1f, -2f);

            var knob = Fill(track.transform, new Color(TextColor.r, TextColor.g, TextColor.b, 0.45f));
            knob.gameObject.name = "NVLB_ScrollKnob";
            var krt = (RectTransform)knob.transform;
            krt.anchorMin = new Vector2(0f, 1f);
            krt.anchorMax = new Vector2(1f, 1f);
            krt.pivot = new Vector2(0.5f, 1f);
            krt.offsetMin = new Vector2(0f, 0f);
            krt.offsetMax = new Vector2(0f, 0f);
            return krt;
        }

        /// <summary>
        /// One wheel notch, one row - and keep the indicator in step. Only the SIGN of the wheel
        /// is used, so it does not matter whether the value arrives as Unity's 1.0, Windows' 120
        /// or ZInput's 0.15: the pane always moves by exactly <paramref name="step"/> pixels.
        /// </summary>
        public static void TickScroll(ScrollRect sr, RectTransform knob, float step)
        {
            if (sr == null || sr.content == null || sr.viewport == null) return;

            float viewH = sr.viewport.rect.height;
            float contentH = sr.content.rect.height;
            float max = Mathf.Max(0f, contentH - viewH);

            if (max > 0f)
            {
                float wheel = UnityEngine.Input.mouseScrollDelta.y;
                if (Mathf.Abs(wheel) < 0.001f) wheel = ZInput.GetMouseScrollWheel();
                if (Mathf.Abs(wheel) > 0.001f &&
                    RectTransformUtility.RectangleContainsScreenPoint(
                        sr.viewport, UnityEngine.Input.mousePosition, CamFor(sr.viewport)))
                {
                    // Content is pivoted top-left, so scrolling DOWN moves it up: y increases.
                    var p = sr.content.anchoredPosition;
                    p.y = Mathf.Clamp(p.y - Mathf.Sign(wheel) * step, 0f, max);
                    sr.content.anchoredPosition = p;
                }
            }

            if (knob == null) return;
            var track = knob.parent as RectTransform;
            if (track == null) return;

            bool needed = max > 0.5f;
            if (track.gameObject.activeSelf != needed) track.gameObject.SetActive(needed);
            if (!needed) return;

            float trackH = track.rect.height;
            float knobH = Mathf.Clamp(trackH * (viewH / Mathf.Max(1f, contentH)), 20f, trackH);
            float t = Mathf.Clamp01(sr.content.anchoredPosition.y / max);
            knob.sizeDelta = new Vector2(0f, knobH);
            knob.anchoredPosition = new Vector2(0f, -t * (trackH - knobH));
        }

        private static Camera CamFor(RectTransform rt)
        {
            var canvas = rt.GetComponentInParent<Canvas>();
            if (canvas == null) return null;
            return canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
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
        /// <summary>
        /// The hover hit-area. It also implements the pointer-press interfaces and does nothing
        /// in them, on purpose: a click on a row's label or hint must be a dead click. Without
        /// that, the press has no handler here and the event system keeps looking, and a click on
        /// the "Window days" label was landing on the slider and slamming it to its minimum.
        ///
        /// Swallowing the press is right for the settings pane and wrong for the module list: a
        /// label is the most obvious thing on a row to click, and there the click has somewhere
        /// sensible to go. <see cref="OnClick"/> is that opt-in - null by default, so every label
        /// that does not ask for it keeps the dead click exactly as before.
        /// </summary>
        internal sealed class Hover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler,
                                      IPointerDownHandler, IPointerUpHandler, IPointerClickHandler
        {
            /// <summary>
            /// What a click here should do, or null for "nothing at all". Left null - which is
            /// what every right-hand-pane label and hint leaves it - a click on this object is
            /// still swallowed and cannot reach the control behind it.
            /// </summary>
            public Action OnClick;

            public void OnPointerDown(PointerEventData eventData) { }
            public void OnPointerUp(PointerEventData eventData) { }

            public void OnPointerClick(PointerEventData eventData)
            {
                if (OnClick == null) return;                    // the 0.7.5 behaviour, unchanged
                if (eventData != null && eventData.button != PointerEventData.InputButton.Left) return;
                try { OnClick(); }
                catch (Exception e)
                {
                    NoVikingLeftBehindPlugin.Log.LogError("[SettingsMenu] hover click: " + e);
                }
            }

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

        /// <summary>
        /// Attach hover text to anything with a raycast target, and - only when asked - make a
        /// click on it do something.
        /// </summary>
        /// <param name="onClick">
        /// Left at its default the object keeps 0.7.5's behaviour exactly: the press is swallowed
        /// and nothing happens, so a click on a setting's name cannot fall through onto the
        /// slider behind it. Passed an action, that swallowed click runs the action instead -
        /// which is how a module row's name became clickable without giving the press back to the
        /// event system.
        /// </param>
        public static void Tip(GameObject go, string text, Action onClick = null)
        {
            if (go == null) return;
            if (string.IsNullOrEmpty(text) && onClick == null)
            {
                var old = go.GetComponent<Hover>();
                if (old != null) { old.Text = null; old.OnClick = null; }  // no stale tooltip, no stale click
                return;
            }
            // The hover needs something the graphic raycaster can hit. It used to add an Image
            // whenever there was no Image - but UnityEngine.UI.Graphic is [DisallowMultipleComponent]
            // and that attribute covers the whole family, so AddComponent<Image>() on an object
            // that already carries a TMP_Text returns NULL. Putting a tooltip on a label in 0.7.5
            // therefore threw inside here and took the entire page down with it. Use whatever
            // Graphic is already on the object, and only add one when there is none at all.
            var graphic = go.GetComponent<Graphic>();
            if (graphic == null)
            {
                var img = go.AddComponent<Image>();
                if (img == null) return;                  // no way to hit-test it: no tooltip, no throw
                img.color = new Color(0f, 0f, 0f, 0f);    // invisible, but hit-testable
                graphic = img;
            }
            graphic.raycastTarget = true;

            var h = go.GetComponent<Hover>();
            if (h == null) h = go.AddComponent<Hover>();
            if (h == null) return;
            h.Text = string.IsNullOrEmpty(text) ? null : text;
            h.OnClick = onClick;
        }

        /// <summary>
        /// A tooltip on something a CHILD already makes hit-testable - a toggle, whose one small
        /// patch does the hitting for it. It deliberately switches no raycast target on: doing
        /// that to a cloned toggle is what made its whole donor-sized widget catch clicks again.
        ///
        /// Putting it on the toggle rather than on the patch matters for more than tidiness.
        /// Unity chooses a click's handler by walking UP from whatever was hit and stopping at
        /// the first object that implements the interface at all - so a Hover sitting on the
        /// patch answered the click itself and the Toggle underneath never heard it, which is
        /// exactly why the module checkboxes went dead in 0.8.3. On the toggle's own object the
        /// two components share a GameObject, Unity runs both, and the box still ticks.
        /// </summary>
        public static void TipOnly(GameObject go, string text)
        {
            if (go == null) return;
            var h = go.GetComponent<Hover>();
            if (h == null) h = go.AddComponent<Hover>();
            if (h == null) return;
            h.Text = string.IsNullOrEmpty(text) ? null : text;
        }

        /// <summary>Screen-space corners of a rect, for the "why can I not click this" log lines.</summary>
        public static string ScreenRect(RectTransform rt)
        {
            if (rt == null) return "<null>";
            var c = new Vector3[4];
            rt.GetWorldCorners(c);
            return "x " + c[0].x.ToString("0") + ".." + c[2].x.ToString("0") +
                   "  y " + c[0].y.ToString("0") + ".." + c[2].y.ToString("0") +
                   "  (" + (c[2].x - c[0].x).ToString("0") + "x" + (c[2].y - c[0].y).ToString("0") + ")";
        }
    }
}
