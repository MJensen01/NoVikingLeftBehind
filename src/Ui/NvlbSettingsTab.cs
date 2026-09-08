using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Valheim.SettingsGui;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// The **NoVikingLeftBehind** tab inside Valheim's own Settings menu.
    ///
    /// How it gets there
    /// -----------------
    /// <c>Settings.Awake</c> calls <c>InitializeTabs()</c>, which builds its <c>SettingsTabs</c>
    /// list by asking every <c>TabHandler.Tab</c>'s page for an <see cref="ISettingsTab"/> - with
    /// no null check - and later indexes that list by tab number. So the tab must be added
    /// *before* <c>InitializeTabs</c> runs and its page must carry an <c>ISettingsTab</c>: hence a
    /// **prefix** on <c>Settings.Awake</c> and this class implementing that interface. Done that
    /// way, vanilla drives us exactly as it drives its own pages - Initialize, OnTabOpen, OnOk,
    /// OnBack, Terminate - and nothing needs to be re-implemented.
    ///
    /// The whole Settings object is destroyed on close (<c>CloseSettings</c> -> <c>Destroy</c>)
    /// and re-instantiated from a prefab on every open, from both <c>Menu.OnSettings</c> (pause
    /// menu) and <c>FejdStartup.OnButtonSettings</c> (main menu), so everything here is rebuilt
    /// each time and nothing is cached across opens.
    ///
    /// Nothing is shipped: every control is a clone of a vanilla one found on the other settings
    /// pages (see <see cref="UiKit"/>), so the tab inherits the game's font, colours and scaling.
    ///
    /// What a row can do
    /// -----------------
    /// A **synced** setting is never written here. The row asks the server through
    /// <see cref="TweakDoor"/>, the server decides, writes its own cfg and pushes the value back
    /// over ServerSync - so the row updates because the *value changed*, not because it was
    /// clicked. A **local** setting is written straight to this machine's cfg. Rows the player may
    /// not change are shown greyed with the reason, rather than hidden, so everyone can see what
    /// exists.
    /// </summary>
    internal sealed class NvlbSettingsTab : MonoBehaviour, ISettingsTab
    {
        public const string TabTitle = "NoVikingLeftBehind";

        // ISettingsTab requires this event. Nothing here participates in vanilla's shared
        // settings (ToggleRun and friends), so it is never raised - but it must exist and be
        // subscribable, because Settings.InitializeTabs adds a handler to every tab it finds.
        private Action<string, int> _sharedSettingChanged;
        public event Action<string, int> SharedSettingChanged
        {
            add { _sharedSettingChanged += value; }
            remove { _sharedSettingChanged -= value; }
        }

        private Settings _settings;
        private RectTransform _page;

        private RectTransform _leftContent, _rightContent;
        private ScrollRect _leftScroll, _rightScroll;
        private RectTransform _leftKnob, _rightKnob;
        private TMP_InputField _search;
        private TMP_Text _accessText, _auditText, _statusText;
        private Button _undoButton, _resetButton;

        private readonly List<Row> _rows = new List<Row>();
        private readonly List<ModuleRow> _moduleRows = new List<ModuleRow>();
        private FeatureModule _selected;

        /// <summary>The Network (SmoothServer) panel is selected: a handful of another mod's rows.</summary>
        private bool _network;
        private Button _netReset;
        private float _netConfirmUntil;

        private const string NetResetIdle = "Reset network to Default";
        private const string NetResetConfirm = "Click again to confirm";

        private string _filter = "";
        private bool _suppress;          // set while we write a control from a value, not vice versa
        private bool _built;
        private float _lastWidth;
        private string _resetGlyph = "Reset";

        private const float RowH = 54f;
        private const float ModuleRowH = 30f;
        private const float ThemeRowH = 26f;
        private const float HeaderH = 104f;
        private const float FooterH = 44f;
        private const float LeftW = 300f;

        /// <summary>How much of a row's right-hand side belongs to the control and the reset button.</summary>
        private const float RightInset = 344f;

        /// <summary>The control column proper: nothing that only shows text may reach into it.</summary>
        private const float ControlColumnW = 340f;

        /// <summary>The live tab, for <c>nvlb.uidump</c>. There is only ever one Settings object.</summary>
        internal static NvlbSettingsTab Current;

        // ---- installation (called from the hooks in SettingsMenuModule) ---------------------------

        /// <summary>
        /// Add the tab if it is not already there. Returns the new component when it added one,
        /// and null when it did not - and in that case it says out loud, in the log, exactly
        /// which check stopped it, because a tab that silently fails to appear is the worst kind
        /// of bug to chase from a screenshot.
        ///
        /// Several hooks call this (see <see cref="SettingsMenuModule"/>) so that the tab exists
        /// whichever of them fires first; the <c>NVLB_Page</c> check below is what makes that
        /// safe - the second and third callers find the page and do nothing.
        /// </summary>
        /// <param name="via">Which hook is calling, for the log.</param>
        public static NvlbSettingsTab Install(Settings settings, string via)
        {
            var log = NoVikingLeftBehindPlugin.Log;

            if (settings == null)
            {
                log.LogWarning("[SettingsMenu] install (" + via + "): the Settings object is null");
                return null;
            }

            var handler = PickTabHandler(settings, via);
            if (handler == null)
            {
                log.LogWarning("[SettingsMenu] install (" + via + "): no TabHandler anywhere under '" +
                               PathOf(settings.transform) + "' - the settings menu changed shape");
                return null;
            }
            if (handler.m_tabs == null || handler.m_tabs.Count == 0)
            {
                log.LogWarning("[SettingsMenu] install (" + via + "): TabHandler '" + PathOf(handler.transform) +
                               "' has " + (handler.m_tabs == null ? "a null" : "an empty") +
                               " m_tabs list - nothing to hang a tab off");
                return null;
            }

            foreach (var t in handler.m_tabs)
                if (t != null && t.m_page != null && t.m_page.name == "NVLB_Page")
                {
                    log.LogInfo("[SettingsMenu] install (" + via + "): the tab is already there - nothing to do");
                    return null;
                }

            // The last entry in m_tabs is NOT necessarily a tab you can see. On Matt's build the
            // list has seven entries: the six visible tabs plus a seventh with a page and NO
            // button - a platform tab the game hides on PC. 0.7.2 took m_tabs[Count-1] as the
            // donor, found no button on it and gave up, which is exactly what its own new warning
            // said four times over. So: walk backwards to the last entry that has both a live
            // button and a page, and only then forwards, before giving up.
            int donorIndex = -1;
            for (int i = handler.m_tabs.Count - 1; i >= 0; i--)
                if (Usable(handler.m_tabs[i])) { donorIndex = i; break; }

            // Second pass without the "on screen" test, in case this runs before the tab bar has
            // been switched on: a button that exists but is not active yet still clones correctly.
            if (donorIndex < 0)
                for (int i = handler.m_tabs.Count - 1; i >= 0; i--)
                {
                    var t = handler.m_tabs[i];
                    if (t != null && t.m_button != null && t.m_page != null) { donorIndex = i; break; }
                }

            if (donorIndex < 0)
            {
                var shape = "";
                for (int i = 0; i < handler.m_tabs.Count; i++)
                {
                    var t = handler.m_tabs[i];
                    shape += (shape.Length > 0 ? ", " : "") + i + ":" +
                             (t == null ? "null" : "button=" + (t.m_button != null) + " page=" + (t.m_page != null));
                }
                log.LogWarning("[SettingsMenu] install (" + via + "): not one of the " + handler.m_tabs.Count +
                               " vanilla tabs is usable as a donor (" + shape + ")");
                return null;
            }

            var donor = handler.m_tabs[donorIndex];
            log.LogInfo("[SettingsMenu] install (" + via + "): donor is tab " + donorIndex + " of " +
                        handler.m_tabs.Count + ", button '" + donor.m_button.gameObject.name +
                        "', page '" + donor.m_page.name + "'");

            // ---- the page ---------------------------------------------------------------------
            var pageGo = new GameObject("NVLB_Page", typeof(RectTransform));
            var page = (RectTransform)pageGo.transform;
            page.SetParent(donor.m_page.parent, false);
            CopyRect(donor.m_page, page);
            pageGo.SetActive(false);

            var tab = pageGo.AddComponent<NvlbSettingsTab>();
            tab._settings = settings;
            tab._page = page;
            Current = tab;

            // ---- the tab button ----------------------------------------------------------------
            var buttonGo = UnityEngine.Object.Instantiate(donor.m_button.gameObject,
                                                          donor.m_button.transform.parent, false);
            buttonGo.name = "NVLB_Tab";
            var button = buttonGo.GetComponent<Button>();

            // Instantiate keeps prefab-authored onClick calls, whose target (the TabHandler) is
            // outside the cloned subtree - the clone would switch to the donor's tab. Assigning a
            // fresh event is the only way to drop persistent listeners.
            button.onClick = new Button.ButtonClickedEvent();

            foreach (var txt in buttonGo.GetComponentsInChildren<TMP_Text>(true))
            {
                txt.text = TabTitle;
                txt.enableAutoSizing = true;
                txt.fontSizeMin = 8f;
                txt.overflowMode = TextOverflowModes.Ellipsis;
            }

            // Tab buttons are positioned in the prefab, not by a layout group on every skin, so
            // step along by the gap between the donor and the usable tab before it when there is
            // no layout group to do it - and, when there is no second one to measure against, by
            // the donor button's own width. The step must be measured between two tabs that
            // actually have buttons: a buttonless entry has no position to subtract.
            var brt = (RectTransform)buttonGo.transform;
            var bar = donor.m_button.transform.parent;
            var layout = bar != null ? bar.GetComponent<LayoutGroup>() : null;
            if (layout == null)
            {
                var a = (RectTransform)donor.m_button.transform;
                RectTransform b = null;
                for (int i = donorIndex - 1; i >= 0; i--)
                    if (Usable(handler.m_tabs[i])) { b = (RectTransform)handler.m_tabs[i].m_button.transform; break; }

                brt.anchoredPosition = (b != null)
                    ? a.anchoredPosition + (a.anchoredPosition - b.anchoredPosition)
                    : a.anchoredPosition + new Vector2(a.rect.width + 4f, 0f);
            }
            brt.SetSiblingIndex(donor.m_button.transform.GetSiblingIndex() + 1);
            buttonGo.SetActive(true);

            int index = handler.m_tabs.Count;
            handler.m_tabs.Add(new TabHandler.Tab
            {
                m_button = button,
                m_page = page,
                m_default = false,
                m_onClick = new UnityEngine.Events.UnityEvent()
            });
            button.onClick.AddListener(delegate { handler.SetActiveTab(index); });

            log.LogInfo("[SettingsMenu] tab added at index " + index + " of " + handler.m_tabs.Count +
                        " (via " + via + "), handler='" + PathOf(handler.transform) + "'");
            log.LogInfo("[SettingsMenu] tab button: parent='" + (bar == null ? "none" : PathOf(bar)) +
                        "' layout=" + (layout == null ? "none" : layout.GetType().Name) +
                        " active=" + buttonGo.activeInHierarchy +
                        " pos=" + brt.anchoredPosition + " size=" + brt.rect.size +
                        "; page='" + PathOf(page) + "' parent='" +
                        (page.parent == null ? "none" : PathOf(page.parent)) + "'");
            return tab;
        }

        /// <summary>
        /// The same TabHandler vanilla drives. <c>InitializeTabs()</c> finds it with
        /// <c>GetComponentInChildren&lt;TabHandler&gt;()</c> - **active children only** - so that is what
        /// this asks for first. 0.7.1 searched inactive children too, which is a superset and can
        /// therefore land on a *different* tab bar: one buried in a settings page that happens to
        /// come first in the hierarchy, whose <c>m_tabs</c> is empty, at which point the install
        /// gave up without a word. The inactive search is kept as a fallback and takes whichever
        /// handler has the most tabs.
        /// </summary>
        private static TabHandler PickTabHandler(Settings settings, string via)
        {
            var log = NoVikingLeftBehindPlugin.Log;

            var handler = settings.GetComponentInChildren<TabHandler>();   // exactly vanilla's lookup
            if (handler != null && handler.m_tabs != null && handler.m_tabs.Count > 0) return handler;

            TabHandler best = null;
            foreach (var h in settings.GetComponentsInChildren<TabHandler>(true))
            {
                if (h == null || h.m_tabs == null) continue;
                if (best == null || h.m_tabs.Count > best.m_tabs.Count) best = h;
            }

            log.LogInfo("[SettingsMenu] install (" + via + "): vanilla's active-only lookup gave " +
                        (handler == null
                             ? "nothing"
                             : "'" + PathOf(handler.transform) + "' with " +
                               (handler.m_tabs == null ? 0 : handler.m_tabs.Count) + " tabs") +
                        ", falling back to " +
                        (best == null
                             ? "nothing"
                             : "'" + PathOf(best.transform) + "' with " + best.m_tabs.Count + " tabs"));
            return best;
        }

        /// <summary>
        /// A tab we can clone a button from and hang a page beside: one with both parts, and with
        /// its button actually on screen. The seventh entry on Matt's build has a page and no
        /// button at all, so "the last tab" and "the last tab you can see" are not the same thing.
        /// </summary>
        private static bool Usable(TabHandler.Tab t)
        {
            return t != null && t.m_button != null && t.m_page != null &&
                   t.m_button.gameObject.activeInHierarchy;
        }

        // ---- nvlb.uidump -----------------------------------------------------------------------

        /// <summary>
        /// Every piece of text on the built page, with the numbers that decide whether it draws:
        /// is it active, is the component enabled, how big is the font, how opaque is the colour,
        /// how big is the rect it has to fit in, where is it on the screen, and is an ancestor
        /// CanvasGroup fading it out. A blank label is nearly always one of those, and a
        /// screenshot cannot tell you which.
        /// </summary>
        internal static void Dump(Action<string> write)
        {
            var tab = Current;
            if (tab == null || !tab._built)
            {
                write("[uidump] the tab is not built yet - open Settings and the NoVikingLeftBehind tab, " +
                      "then run this again (the settings menu is rebuilt on every open).");
                return;
            }

            try
            {
                write("[uidump] BaseFontSize=" + UiKit.BaseFontSize.ToString("0.#") +
                      " text=#" + ColorUtility.ToHtmlStringRGBA(UiKit.TextColor) +
                      " hint=#" + ColorUtility.ToHtmlStringRGBA(UiKit.HintColor) +
                      " dim=#" + ColorUtility.ToHtmlStringRGBA(UiKit.DimColor) +
                      "  rows=" + tab._rows.Count + " modules=" + tab._moduleRows.Count +
                      " selected=" + (tab._network ? "Network" : (tab._selected == null ? "none" : tab._selected.Name)));
                write("[uidump] page " + Fmt(tab._page) + " at '" + PathOf(tab._page) + "'");
                write("[uidump] left content " + Fmt(tab._leftContent));
                write("[uidump] right content " + Fmt(tab._rightContent));
                write("[uidump] tooltip panel " + Fmt(UiKit.Hover.Panel) +
                      " active=" + (UiKit.Hover.Panel != null && UiKit.Hover.Panel.gameObject.activeSelf));

                var texts = tab._page.GetComponentsInChildren<TMP_Text>(true);
                write("[uidump] " + texts.Length + " TMP_Text under the page:");

                var corners = new Vector3[4];
                foreach (var t in texts)
                {
                    if (t == null) continue;
                    var rt = (RectTransform)t.transform;
                    rt.GetWorldCorners(corners);

                    float cg = 1f;
                    for (var p = t.transform; p != null; p = p.parent)
                    {
                        var g = p.GetComponent<CanvasGroup>();
                        if (g != null) cg *= g.alpha;
                    }

                    write("  \"" + Clip(t.text, 20) + "\"" +
                          " act=" + t.gameObject.activeInHierarchy +
                          " en=" + t.enabled +
                          " font=" + t.fontSize.ToString("0.#") +
                          " a=" + t.color.a.ToString("0.##") +
                          " wrap=" + t.enableWordWrapping +
                          " ovf=" + t.overflowMode +
                          " sd=" + V(rt.sizeDelta) +
                          " rect=" + rt.rect.width.ToString("0") + "x" + rt.rect.height.ToString("0") +
                          " pos=" + V(rt.anchoredPosition) +
                          " screen=" + V(corners[0]) + "-" + V(corners[2]) +
                          " sib=" + rt.GetSiblingIndex() +
                          (cg < 0.999f ? " CANVASGROUP=" + cg.ToString("0.##") : "") +
                          " in " + Parent2(rt));
                }
            }
            catch (Exception e) { write("[uidump] failed: " + e); }
        }

        private static string Fmt(RectTransform rt)
        {
            if (rt == null) return "<null>";
            return "rect=" + rt.rect.width.ToString("0") + "x" + rt.rect.height.ToString("0") +
                   " sd=" + V(rt.sizeDelta) + " pos=" + V(rt.anchoredPosition) +
                   " anch=" + V(rt.anchorMin) + "/" + V(rt.anchorMax) + " piv=" + V(rt.pivot);
        }

        private static string V(Vector2 v) { return "(" + v.x.ToString("0.#") + "," + v.y.ToString("0.#") + ")"; }
        private static string V(Vector3 v) { return "(" + v.x.ToString("0") + "," + v.y.ToString("0") + ")"; }

        private static string Clip(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\n", "\\n");
            return s.Length <= n ? s : s.Substring(0, n) + "...";
        }

        /// <summary>Two levels of parent, which is enough to tell a row from a module row.</summary>
        private static string Parent2(Transform t)
        {
            var p = t.parent;
            if (p == null) return "<no parent>";
            var gp = p.parent;
            return (gp == null ? "" : gp.name + "/") + p.name;
        }

        /// <summary>Full hierarchy path of a transform - for the log, when something is not where we expect.</summary>
        private static string PathOf(Transform t)
        {
            if (t == null) return "null";
            var s = t.name;
            for (var p = t.parent; p != null; p = p.parent) s = p.name + "/" + s;
            return s;
        }

        private static void CopyRect(RectTransform from, RectTransform to)
        {
            to.anchorMin = from.anchorMin;
            to.anchorMax = from.anchorMax;
            to.pivot = from.pivot;
            to.anchoredPosition = from.anchoredPosition;
            to.sizeDelta = from.sizeDelta;
            to.offsetMin = from.offsetMin;
            to.offsetMax = from.offsetMax;
            to.localScale = Vector3.one;
        }

        // ---- ISettingsTab ---------------------------------------------------------------------------

        public void Initialize()
        {
            NoVikingLeftBehindPlugin.Log.LogInfo("[SettingsMenu] Initialize() called (built=" + _built + ")");
            if (_built) return;   // vanilla's loop and a late hook can both reach here

            try
            {
                UiKit.Discover(_settings != null ? _settings.gameObject : gameObject);
                if (!UiKit.Ready)
                {
                    NoVikingLeftBehindPlugin.Log.LogWarning(
                        "[SettingsMenu] no vanilla controls to clone - the tab will stay empty");
                    return;
                }
                Build();
                _built = true;
                NoVikingLeftBehindPlugin.Log.LogInfo("[SettingsMenu] Build() done rows=" + _rows.Count +
                                                     " modules=" + _moduleRows.Count);
            }
            catch (Exception e)
            {
                NoVikingLeftBehindPlugin.Log.LogError("[SettingsMenu] could not build the tab: " + e);
            }
        }

        public void OnTabOpen(Button backButton, Button okButton)
        {
            try
            {
                TweakDoor.RequestAudit();
                RefreshAll();
            }
            catch (Exception e) { NoVikingLeftBehindPlugin.Log.LogError("[SettingsMenu] OnTabOpen: " + e); }
        }

        /// <summary>
        /// Changes here are applied the moment they are made (the server has already written
        /// them), so OK has nothing to save. Vanilla waits for the callback before closing.
        /// </summary>
        public void OnOkAsync(OkActionCompletedHandler okActionCompletedCallback)
        {
            if (okActionCompletedCallback != null) okActionCompletedCallback();
        }

        /// <summary>Back does NOT revert: a change was already announced to everyone.</summary>
        public void OnBack() { }

        public void Terminate()
        {
            try
            {
                if (NoVikingLeftBehindPlugin.Cfg != null)
                    NoVikingLeftBehindPlugin.Cfg.SettingChanged -= OnAnySettingChanged;
                if (SmoothServerBridge.Available)
                    SmoothServerBridge.Cfg.SettingChanged -= OnForeignSettingChanged;
                TweakDoor.Result -= OnDoorResult;
                TweakDoor.AuditChanged -= OnAuditChanged;
                UiKit.Hover.HidePanel();
                UiKit.Hover.Panel = null;
            }
            catch { /* closing down */ }
        }

        public void OnSharedSettingChanged(string setting, int value) { }

        private void OnDestroy()
        {
            if (Current == this) Current = null;
            Terminate();
        }

        private void Update()
        {
            if (!_built) return;
            UiKit.Hover.Tick();

            // One notch of the wheel moves one row in whichever pane the pointer is over.
            UiKit.TickScroll(_leftScroll, _leftKnob, ModuleRowH + 2f);
            UiKit.TickScroll(_rightScroll, _rightKnob, RowH);

            // The "reset the network" confirmation lapses on its own, so a stray first click
            // never leaves a loaded button behind.
            if (_netConfirmUntil > 0f && Time.unscaledTime > _netConfirmUntil)
            {
                _netConfirmUntil = 0f;
                SetCaption(_netReset, NetResetIdle);
            }

            // The page's width is only real once the canvas has laid out; re-flow once it settles.
            float w = _page != null ? _page.rect.width : 0f;
            if (w > 100f && Mathf.Abs(w - _lastWidth) > 1f)
            {
                _lastWidth = w;
                ReflowRowLabels();
                LayoutRebuilder.MarkLayoutForRebuild(_page);
            }
        }

        /// <summary>
        /// A row label spans from a 12px left inset to <see cref="RightInset"/> short of the right
        /// edge, leaving room for the control and the reset button. On a narrow page that leaves
        /// the label nothing at all - a zero or negative width, which TMP draws as nothing - so
        /// the right inset gives way first and a label is never allowed below 120px.
        /// </summary>
        private void ReflowRowLabels()
        {
            float rw = _rightContent != null ? _rightContent.rect.width : 0f;
            if (rw <= 0f) return;
            // Give ground on the right inset so the label keeps 120px, but never below the width
            // of the control column itself: a label that reached under the slider would overlap
            // the one thing on the row that must own its own clicks.
            float right = Mathf.Clamp(RightInset, ControlColumnW, Mathf.Max(ControlColumnW, rw - 12f - 120f));

            foreach (var r in _rows)
            {
                if (r.Label != null) SetRightInset((RectTransform)r.Label.transform, right);
                if (r.Hint != null) SetRightInset((RectTransform)r.Hint.transform, right);
            }
        }

        private static void SetRightInset(RectTransform rt, float right)
        {
            var om = rt.offsetMax;
            if (Mathf.Abs(om.x + right) > 0.5f) rt.offsetMax = new Vector2(-right, om.y);
        }

        // ---- building ----------------------------------------------------------------------------------

        private void Build()
        {
            if (UiKit.Ready)
            {
                var fontAsset = ResolveFont();
                if (fontAsset != null && fontAsset.HasCharacter('↺')) _resetGlyph = "↺";
            }

            UiKit.Hover.EnsurePanel(_page);

            // ---- header --------------------------------------------------------------------
            var header = UiKit.Panel("Header", _page);
            header.anchorMin = new Vector2(0f, 1f);
            header.anchorMax = new Vector2(1f, 1f);
            header.pivot = new Vector2(0.5f, 1f);
            header.offsetMin = new Vector2(0f, -HeaderH);
            header.offsetMax = new Vector2(0f, 0f);
            header.anchoredPosition = new Vector2(0f, 0f);

            var searchLabel = UiKit.Label(header, "Search", UiKit.BaseFontSize * 0.9f,
                                          TextAlignmentOptions.MidlineLeft);
            UiKit.Place((RectTransform)searchLabel.transform, 8f, 6f, 70f, 26f);

            _search = UiKit.Input(header, 300f, 26f);
            if (_search != null)
            {
                UiKit.Place((RectTransform)_search.transform, 80f, 6f, 300f, 26f);
                if (_search.placeholder != null)
                    ((TMP_Text)_search.placeholder).text = "name, key, hint or description";
                // Same space the tooltip is positioned in: x right from the page's left edge,
                // y negative downward from its top.
                UiKit.Hover.Avoid = new Rect(72f, -36f, 316f, 32f);
                _search.onValueChanged.AddListener(delegate (string s)
                {
                    _filter = (s ?? "").Trim();
                    RebuildRight();
                });
            }

            _accessText = UiKit.Label(header, "", UiKit.BaseFontSize * 0.85f,
                                      TextAlignmentOptions.MidlineLeft, UiKit.HintColor);
            var art = (RectTransform)_accessText.transform;
            art.anchorMin = new Vector2(0f, 1f);
            art.anchorMax = new Vector2(1f, 1f);
            art.pivot = new Vector2(0f, 1f);
            art.offsetMin = new Vector2(8f, -60f);
            art.offsetMax = new Vector2(-420f, -36f);

            _auditText = UiKit.Label(header, "", UiKit.BaseFontSize * 0.72f,
                                     TextAlignmentOptions.TopRight, UiKit.HintColor);
            var aurt = (RectTransform)_auditText.transform;
            aurt.anchorMin = new Vector2(1f, 1f);
            aurt.anchorMax = new Vector2(1f, 1f);
            aurt.pivot = new Vector2(1f, 1f);
            aurt.anchoredPosition = new Vector2(-8f, -4f);
            aurt.sizeDelta = new Vector2(400f, HeaderH - 8f);
            _auditText.enableWordWrapping = false;

            var rule = UiKit.Fill(header, new Color(UiKit.TextColor.r, UiKit.TextColor.g,
                                                    UiKit.TextColor.b, 0.18f));
            UiKit.Place((RectTransform)rule.transform, 0f, HeaderH - 2f, 4000f, 1f);

            // ---- left: the module list ------------------------------------------------------
            _leftScroll = UiKit.Scroll(_page, "Modules", out _leftContent);
            var lrt = (RectTransform)_leftScroll.transform;
            lrt.anchorMin = new Vector2(0f, 0f);
            lrt.anchorMax = new Vector2(0f, 1f);
            lrt.pivot = new Vector2(0f, 0.5f);
            lrt.offsetMin = new Vector2(4f, FooterH);
            lrt.offsetMax = new Vector2(LeftW, -HeaderH);
            lrt.sizeDelta = new Vector2(LeftW - 4f, lrt.sizeDelta.y);
            _leftKnob = UiKit.ScrollBar(_leftScroll);

            // ---- right: the selected module's settings --------------------------------------
            _rightScroll = UiKit.Scroll(_page, "Settings", out _rightContent);
            var rrt = (RectTransform)_rightScroll.transform;
            rrt.anchorMin = new Vector2(0f, 0f);
            rrt.anchorMax = new Vector2(1f, 1f);
            rrt.pivot = new Vector2(0.5f, 0.5f);
            rrt.offsetMin = new Vector2(LeftW + 8f, FooterH);
            rrt.offsetMax = new Vector2(-8f, -HeaderH);
            _rightKnob = UiKit.ScrollBar(_rightScroll);

            // ---- footer ----------------------------------------------------------------------
            var footer = UiKit.Panel("Footer", _page);
            footer.anchorMin = new Vector2(0f, 0f);
            footer.anchorMax = new Vector2(1f, 0f);
            footer.pivot = new Vector2(0.5f, 0f);
            footer.offsetMin = new Vector2(0f, 0f);
            footer.offsetMax = new Vector2(0f, FooterH);

            _undoButton = UiKit.Button(footer, "Undo last change", UiKit.BaseFontSize * 0.9f);
            if (_undoButton != null)
            {
                // Place() measures DOWN from the top of the footer band, which is the bottom
                // FooterH pixels of the page. Passing FooterH-6 put the buttons 24px BELOW the
                // page, on top of vanilla's own Back/OK row.
                UiKit.Place((RectTransform)_undoButton.transform, 8f, 2f, 200f, 34f);
                _undoButton.onClick.AddListener(delegate { TweakDoor.RequestUndo(); });
                UiKit.Tip(_undoButton.gameObject,
                    "Put the most recent change on this server back to what it was. " +
                    "The server keeps the last 20 changes per setting.");
            }

            _resetButton = UiKit.Button(footer, "Reset module to defaults", UiKit.BaseFontSize * 0.9f);
            if (_resetButton != null)
            {
                UiKit.Place((RectTransform)_resetButton.transform, 216f, 2f, 240f, 34f);
                _resetButton.onClick.AddListener(delegate
                {
                    if (_selected != null) TweakDoor.RequestResetModule(_selected.Section);
                });
                UiKit.Tip(_resetButton.gameObject,
                    "Put every setting in the selected module back to the value it ships with.");
            }

            _statusText = UiKit.Label(footer, "", UiKit.BaseFontSize * 0.85f,
                                      TextAlignmentOptions.MidlineRight, UiKit.HintColor);
            var srt = (RectTransform)_statusText.transform;
            srt.anchorMin = new Vector2(1f, 0f);
            srt.anchorMax = new Vector2(1f, 0f);
            srt.pivot = new Vector2(1f, 0f);
            srt.anchoredPosition = new Vector2(-8f, 8f);
            srt.sizeDelta = new Vector2(520f, 26f);

            // ---- wiring ------------------------------------------------------------------------
            NoVikingLeftBehindPlugin.Cfg.SettingChanged += OnAnySettingChanged;
            // The Network panel's rows live in SmoothServer's config, so they move when *its*
            // ServerSync pushes a value - a different ConfigFile, the same live-refresh idea.
            if (SmoothServerBridge.Available)
                SmoothServerBridge.Cfg.SettingChanged += OnForeignSettingChanged;
            TweakDoor.Result += OnDoorResult;
            TweakDoor.AuditChanged += OnAuditChanged;

            BuildModuleList();
            if (_selected == null && _moduleRows.Count > 0) _selected = _moduleRows[0].Module;
            RebuildRight();
            RefreshHeader();
        }

        private TMP_FontAsset ResolveFont()
        {
            var probe = UiKit.Label(_page, "", 10f, TextAlignmentOptions.Left);
            if (probe == null) return null;
            var font = probe.font;
            UnityEngine.Object.Destroy(probe.gameObject);
            return font;
        }

        // ---- left column -------------------------------------------------------------------------------

        private sealed class ModuleRow
        {
            public FeatureModule Module;
            public TMP_Text Label;
            public Toggle Enabled;
            public SettingInfo EnabledInfo;
            public GameObject Root;
        }

        private void BuildModuleList()
        {
            float y = 4f;
            string theme = null;

            foreach (var module in ConfigCatalog.ModulesForUi())
            {
                if (module.Theme != theme)
                {
                    // Air above every heading but the first, so a theme reads as starting a new
                    // group rather than belonging to the row above it.
                    if (theme != null) y += 8f;
                    theme = module.Theme;
                    var head = UiKit.Label(_leftContent, theme.ToUpperInvariant(),
                                           UiKit.BaseFontSize * 0.72f, TextAlignmentOptions.BottomLeft,
                                           UiKit.HintColor);
                    UiKit.Place((RectTransform)head.transform, 6f, y, LeftW - 20f, ThemeRowH);
                    y += ThemeRowH;
                }

                var rowGo = new GameObject("Mod_" + module.Name, typeof(RectTransform));
                rowGo.transform.SetParent(_leftContent, false);
                var rowRt = UiKit.Place((RectTransform)rowGo.transform, 0f, y, LeftW - 12f, ModuleRowH);

                var name = UiKit.Label(rowRt, module.Name, UiKit.BaseFontSize * 0.92f,
                                       TextAlignmentOptions.MidlineLeft);
                UiKit.Place((RectTransform)name.transform, 14f, 2f, LeftW - 70f, ModuleRowH - 4f);

                var picker = new GameObject("Pick", typeof(RectTransform));
                picker.transform.SetParent(rowRt, false);
                UiKit.Stretch((RectTransform)picker.transform);
                var mod = module;
                var pickBtn = picker.AddComponent<Button>();
                pickBtn.transition = Selectable.Transition.None;
                pickBtn.onClick.AddListener(delegate { Select(mod); });
                // Attached after the Button, and to the name as well: the toggle sits on top of
                // the right-hand end of this row, so hovering there used to reach nothing at all.
                UiKit.Tip(picker, ModuleTooltip(mod));
                UiKit.Tip(name.gameObject, ModuleTooltip(mod));

                var mr = new ModuleRow { Module = module, Label = name, Root = rowGo };

                mr.EnabledInfo = ConfigCatalog.Find(module.Section, "Enabled");
                var toggle = UiKit.Toggle(rowRt);
                if (toggle != null)
                {
                    var trt = (RectTransform)toggle.transform;
                    trt.anchorMin = new Vector2(1f, 1f);
                    trt.anchorMax = new Vector2(1f, 1f);
                    trt.pivot = new Vector2(1f, 1f);
                    trt.anchoredPosition = new Vector2(-6f, -3f);
                    var info = mr.EnabledInfo;
                    toggle.onValueChanged.AddListener(delegate (bool on)
                    {
                        if (_suppress || info == null) return;
                        Apply(info, on ? "true" : "false");
                    });
                    mr.Enabled = toggle;
                    // The toggle is the topmost thing over its corner of the row, so it has to
                    // carry the tooltip itself or hovering the checkbox says nothing.
                    UiKit.Tip(toggle.gameObject, ModuleTooltip(mod));
                }

                _moduleRows.Add(mr);
                y += ModuleRowH + 2f;
            }

            // The Network panel, only when SmoothServer is actually loaded on this machine.
            // It is not one of our modules and has no Enabled toggle of its own - it is a window
            // onto five of another mod's settings, so it gets its own theme heading and one row.
            if (SmoothServerBridge.Available && SmoothServerBridge.Rows().Count > 0)
            {
                var netHead = UiKit.Label(_leftContent, SmoothServerBridge.PanelTheme.ToUpperInvariant(),
                                          UiKit.BaseFontSize * 0.72f, TextAlignmentOptions.BottomLeft,
                                          UiKit.HintColor);
                UiKit.Place((RectTransform)netHead.transform, 6f, y, LeftW - 20f, ThemeRowH);
                y += ThemeRowH;

                var netGo = new GameObject("Sec_Network", typeof(RectTransform));
                netGo.transform.SetParent(_leftContent, false);
                var netRt = UiKit.Place((RectTransform)netGo.transform, 0f, y, LeftW - 12f, ModuleRowH);
                var netName = UiKit.Label(netRt, SmoothServerBridge.PanelLabel, UiKit.BaseFontSize * 0.92f,
                                          TextAlignmentOptions.MidlineLeft);
                UiKit.Place((RectTransform)netName.transform, 14f, 2f, LeftW - 30f, ModuleRowH - 4f);

                var netPick = new GameObject("Pick", typeof(RectTransform));
                netPick.transform.SetParent(netRt, false);
                UiKit.Stretch((RectTransform)netPick.transform);
                UiKit.Tip(netPick, SmoothServerBridge.PanelHint + "\n\nSmoothServer " +
                    SmoothServerBridge.Version + " is installed on this machine. These are its own " +
                    "settings, not this mod's - only the few that are worth reaching for mid-game " +
                    "are here; the rest stay in its config file.");
                var netBtn = netPick.AddComponent<Button>();
                netBtn.transition = Selectable.Transition.None;
                netBtn.onClick.AddListener(delegate { SelectNetwork(); });

                _moduleRows.Add(new ModuleRow { Module = null, Label = netName, Root = netGo });
                y += ModuleRowH + 2f;
            }

            // Plugin-level settings that belong to no module: [General], [Frontier], [Tiers].
            var orphans = ConfigCatalog.Orphans();
            if (orphans.Count > 0)
            {
                var head = UiKit.Label(_leftContent, "THE WHOLE MOD", UiKit.BaseFontSize * 0.72f,
                                       TextAlignmentOptions.BottomLeft, UiKit.HintColor);
                UiKit.Place((RectTransform)head.transform, 6f, y, LeftW - 20f, ThemeRowH);
                y += ThemeRowH;

                foreach (var section in OrphanSections(orphans))
                {
                    var rowGo = new GameObject("Sec_" + section, typeof(RectTransform));
                    rowGo.transform.SetParent(_leftContent, false);
                    var rowRt = UiKit.Place((RectTransform)rowGo.transform, 0f, y, LeftW - 12f, ModuleRowH);
                    var name = UiKit.Label(rowRt, section, UiKit.BaseFontSize * 0.92f,
                                           TextAlignmentOptions.MidlineLeft);
                    UiKit.Place((RectTransform)name.transform, 14f, 2f, LeftW - 30f, ModuleRowH - 4f);

                    var picker = new GameObject("Pick", typeof(RectTransform));
                    picker.transform.SetParent(rowRt, false);
                    UiKit.Stretch((RectTransform)picker.transform);
                    string sec = section;
                    UiKit.Tip(picker, "Settings that apply to the whole mod rather than one feature.");
                    var pickBtn = picker.AddComponent<Button>();
                    pickBtn.transition = Selectable.Transition.None;
                    pickBtn.onClick.AddListener(delegate { SelectSection(sec); });

                    _moduleRows.Add(new ModuleRow { Module = null, Label = name, Root = rowGo });
                    _orphanSectionOf[rowGo] = section;
                    y += ModuleRowH + 2f;
                }
            }

            UiKit.FitContent(_leftContent, y + 8f);
        }

        private readonly Dictionary<GameObject, string> _orphanSectionOf = new Dictionary<GameObject, string>();
        private string _selectedSection;

        private static List<string> OrphanSections(List<SettingInfo> orphans)
        {
            var seen = new List<string>();
            foreach (var s in orphans) if (!seen.Contains(s.Section)) seen.Add(s.Section);
            seen.Sort(StringComparer.Ordinal);
            return seen;
        }

        private static string ModuleTooltip(FeatureModule m)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(m.Name);
            if (!string.IsNullOrEmpty(m.Theme)) sb.Append("   -   ").Append(m.Theme);
            sb.Append('\n');
            if (!string.IsNullOrEmpty(m.Hint)) sb.Append('\n').Append(m.Hint).Append('\n');

            var enabled = ConfigCatalog.Find(m.Section, "Enabled");
            if (enabled != null && !string.IsNullOrEmpty(enabled.Description))
                sb.Append('\n').Append(enabled.Description).Append('\n');

            sb.Append("\nOn: ").Append(m.BootEnabled ? "live" : "needs a restart")
              .Append("   Off: live");
            sb.Append("\nSection [").Append(m.Section).Append("]   runs on: ").Append(m.Side)
              .Append("\nState on this machine: ").Append(m.Status);
            return sb.ToString();
        }

        private void Select(FeatureModule module)
        {
            _selected = module;
            _selectedSection = module != null ? module.Section : null;
            _network = false;
            RebuildRight();
        }

        private void SelectSection(string section)
        {
            _selected = null;
            _selectedSection = section;
            _network = false;
            RebuildRight();
        }

        private void SelectNetwork()
        {
            _selected = null;
            _selectedSection = null;
            _network = true;
            RebuildRight();
        }

        // ---- right column ----------------------------------------------------------------------------------

        private sealed class Row
        {
            public SettingInfo Info;
            public GameObject Root;
            public TMP_Text Label, Hint, Note;
            public Toggle Toggle;
            public Slider Slider;
            public TMP_InputField Input;
            public TMP_Text CycleText;
            public Button Left, Right, Reset;
            public bool Editable;
        }

        private void RebuildRight()
        {
            foreach (var r in _rows) if (r.Root != null) UnityEngine.Object.Destroy(r.Root);
            _rows.Clear();
            for (int i = _rightContent.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(_rightContent.GetChild(i).gameObject);

            _netReset = null;
            _netConfirmUntil = 0f;

            var wanted = Wanted();
            float y = 4f;
            string section = null;

            foreach (var info in wanted)
            {
                // Ours are grouped by cfg section. The five SmoothServer rows sit under one
                // heading naming the mod and its version instead: five headings for five rows
                // would be noise, and which of that mod's sections a row lives in is in its
                // tooltip anyway.
                string group = info.Foreign ? SmoothServerBridge.GroupHeader : "[" + info.Section + "]";
                if (group != section)
                {
                    section = group;
                    var head = UiKit.Label(_rightContent, group,
                                           UiKit.BaseFontSize * 0.8f, TextAlignmentOptions.BottomLeft,
                                           UiKit.HintColor);
                    var hrt = (RectTransform)head.transform;
                    hrt.anchorMin = new Vector2(0f, 1f);
                    hrt.anchorMax = new Vector2(1f, 1f);
                    hrt.pivot = new Vector2(0f, 1f);
                    hrt.offsetMin = new Vector2(8f, -(y + ThemeRowH));
                    hrt.offsetMax = new Vector2(-8f, -y);
                    y += ThemeRowH;
                }

                _rows.Add(BuildRow(info, y));
                y += RowH;
            }

            if (_network && string.IsNullOrEmpty(_filter) && wanted.Count > 0)
                y = BuildNetworkReset(y);

            if (wanted.Count == 0)
            {
                var none = UiKit.Label(_rightContent,
                    string.IsNullOrEmpty(_filter) ? "Nothing to show." : "Nothing matches \"" + _filter + "\".",
                    UiKit.BaseFontSize, TextAlignmentOptions.TopLeft, UiKit.HintColor);
                UiKit.Place((RectTransform)none.transform, 12f, 12f, 600f, 30f);
                y += 40f;
            }

            UiKit.FitContent(_rightContent, y + 12f);
            _rightScroll.verticalNormalizedPosition = 1f;
            RefreshAll();
        }

        /// <summary>
        /// A big, plain button under the five rows: put the network back to the shipped defaults.
        /// Two clicks, because it changes three things at once for everybody on the server.
        /// </summary>
        private float BuildNetworkReset(float y)
        {
            _netReset = UiKit.Button(_rightContent, NetResetIdle);
            if (_netReset == null) return y;

            var brt = (RectTransform)_netReset.transform;
            brt.anchorMin = new Vector2(0f, 1f);
            brt.anchorMax = new Vector2(0f, 1f);
            brt.pivot = new Vector2(0f, 1f);
            brt.anchoredPosition = new Vector2(12f, -(y + 8f));
            brt.sizeDelta = new Vector2(240f, 30f);

            _netReset.onClick.AddListener(NetworkResetClicked);
            UiKit.Tip(_netReset.gameObject,
                "Put the network back where it ships: preset Default, compression on, shared map on.\n\n" +
                "Three ordinary changes, announced and undoable like any other. Reach for it when " +
                "someone has been experimenting and the server feels worse than it did.");

            var note = UiKit.Label(_rightContent,
                "Everything above is SmoothServer's, not this mod's. The rest of its settings stay " +
                "in its own config file.",
                UiKit.BaseFontSize * 0.75f, TextAlignmentOptions.MidlineLeft, UiKit.HintColor);
            var nrt = (RectTransform)note.transform;
            nrt.anchorMin = new Vector2(0f, 1f);
            nrt.anchorMax = new Vector2(1f, 1f);
            nrt.pivot = new Vector2(0f, 1f);
            nrt.offsetMin = new Vector2(262f, -(y + 38f));
            nrt.offsetMax = new Vector2(-12f, -(y + 8f));

            return y + 46f;
        }

        private void NetworkResetClicked()
        {
            if (Time.unscaledTime > _netConfirmUntil)
            {
                _netConfirmUntil = Time.unscaledTime + 6f;
                SetCaption(_netReset, NetResetConfirm);
                SetStatus(true, "This sets the preset to Default and turns compression and the " +
                                "shared map on, for everyone. Click again to confirm.");
                return;
            }

            _netConfirmUntil = 0f;
            SetCaption(_netReset, NetResetIdle);
            SmoothServerBridge.RequestResetToDefault();
        }

        private static void SetCaption(Button button, string caption)
        {
            if (button == null) return;
            foreach (var txt in button.GetComponentsInChildren<TMP_Text>(true)) txt.text = caption;
        }

        /// <summary>Which settings the right column should show right now.</summary>
        private List<SettingInfo> Wanted()
        {
            var list = new List<SettingInfo>();
            string needle = _filter.ToLowerInvariant();

            if (needle.Length > 0)
            {
                foreach (var s in ConfigCatalog.All)
                {
                    if (!ConfigCatalog.Matches(s, needle)) continue;
                    if (!Visible(s)) continue;
                    list.Add(s);
                    if (list.Count >= 80) break;      // a search is for finding, not for browsing
                }
                list.Sort(delegate (SettingInfo a, SettingInfo b)
                {
                    int c = string.CompareOrdinal(a.Section, b.Section);
                    return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
                });

                // The Network panel's rows are searchable too, appended in panel order so they
                // stay together under their own heading rather than scattered among ours.
                foreach (var s in SmoothServerBridge.Rows())
                {
                    if (!ConfigCatalog.Matches(s, needle)) continue;
                    if (!Visible(s)) continue;
                    list.Add(s);
                }
                return list;
            }

            if (_network)
            {
                foreach (var s in SmoothServerBridge.Rows())
                    if (Visible(s)) list.Add(s);
                return list;
            }

            foreach (var s in ConfigCatalog.All)
            {
                if (!Visible(s)) continue;
                if (_selected != null) { if (s.Owner == _selected) list.Add(s); }
                else if (_selectedSection != null && s.Owner == null && s.Section == _selectedSection) list.Add(s);
            }
            return list;
        }

        /// <summary>
        /// [SettingsMenu] ShowUnavailable, on by default: a row you cannot change is still listed
        /// (greyed, with the reason) so everyone can see what the mod can do. Turn it off and the
        /// menu only shows what you personally can act on.
        /// </summary>
        private static bool Visible(SettingInfo s)
        {
            if (SettingsMenuModule.ShowUnavailable) return true;
            return WhyNot(s, null) == null;
        }

        private Row BuildRow(SettingInfo info, float y)
        {
            var row = new Row { Info = info };

            var go = new GameObject("Row_" + info.Key, typeof(RectTransform));
            go.transform.SetParent(_rightContent, false);
            row.Root = go;
            var rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.offsetMin = new Vector2(0f, -(y + RowH));
            rt.offsetMax = new Vector2(0f, -y);

            row.Label = UiKit.Label(rt, info.Label, UiKit.BaseFontSize * 0.95f,
                                    TextAlignmentOptions.MidlineLeft);
            Span((RectTransform)row.Label.transform, 12f, RightInset, 4f, 26f);

            row.Hint = UiKit.Label(rt, info.Hint ?? "", UiKit.BaseFontSize * 0.78f,
                                   TextAlignmentOptions.MidlineLeft, UiKit.HintColor);
            Span((RectTransform)row.Hint.transform, 12f, RightInset, 26f, 22f);

            // The full description is the hover tooltip; the hint is the one-liner under the label.
            var hot = new GameObject("Hot", typeof(RectTransform));
            hot.transform.SetParent(rt, false);
            Span((RectTransform)hot.transform, 8f, RightInset, 2f, RowH - 4f);
            UiKit.Tip(hot, Tooltip(info));

            // ---- the control ------------------------------------------------------------------
            var control = new GameObject("Control", typeof(RectTransform));
            control.transform.SetParent(rt, false);
            var crt = (RectTransform)control.transform;
            crt.anchorMin = new Vector2(1f, 1f);
            crt.anchorMax = new Vector2(1f, 1f);
            crt.pivot = new Vector2(1f, 1f);
            crt.anchoredPosition = new Vector2(-52f, -8f);
            crt.sizeDelta = new Vector2(280f, 28f);

            switch (info.TypeName)
            {
                case "bool": BuildBool(row, crt); break;
                case "enum": BuildCycle(row, crt, info.Choices); break;
                case "string":
                    if (info.Choices != null && info.Choices.Length > 0) BuildCycle(row, crt, info.Choices);
                    else BuildText(row, crt);
                    break;
                default: BuildNumber(row, crt); break;
            }

            // ---- reset to default -----------------------------------------------------------
            row.Reset = UiKit.Button(rt, _resetGlyph);
            if (row.Reset != null)
            {
                var brt = (RectTransform)row.Reset.transform;
                brt.anchorMin = new Vector2(1f, 1f);
                brt.anchorMax = new Vector2(1f, 1f);
                brt.pivot = new Vector2(1f, 1f);
                brt.anchoredPosition = new Vector2(-8f, -8f);
                brt.sizeDelta = new Vector2(40f, 28f);
                var captured = info;
                row.Reset.onClick.AddListener(delegate { Apply(captured, captured.DefaultString); });
                UiKit.Tip(row.Reset.gameObject, "Put this back to its default, " + info.DefaultString + ".");
            }

            // ---- the "you cannot change this" note --------------------------------------------
            row.Note = UiKit.Label(rt, "", UiKit.BaseFontSize * 0.78f,
                                   TextAlignmentOptions.MidlineRight, UiKit.DimColor);
            var nrt = (RectTransform)row.Note.transform;
            nrt.anchorMin = new Vector2(1f, 1f);
            nrt.anchorMax = new Vector2(1f, 1f);
            nrt.pivot = new Vector2(1f, 1f);
            nrt.anchoredPosition = new Vector2(-52f, -8f);
            nrt.sizeDelta = new Vector2(280f, 28f);
            row.Note.gameObject.SetActive(false);

            return row;
        }

        /// <summary>Stretch horizontally between a left inset and a right inset, at a fixed y/height.</summary>
        private static void Span(RectTransform rt, float left, float right, float top, float height)
        {
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.offsetMin = new Vector2(left, -(top + height));
            rt.offsetMax = new Vector2(-right, -top);
        }

        private static string Tooltip(SettingInfo info)
        {
            var sb = new System.Text.StringBuilder();
            if (info.Foreign)
                sb.Append("SmoothServer  [").Append(info.Section).Append("] ").Append(info.Key).Append('\n');
            else
                sb.Append(info.Section).Append('.').Append(info.Key).Append('\n');
            if (!string.IsNullOrEmpty(info.Description)) sb.Append('\n').Append(info.Description).Append('\n');
            sb.Append('\n').Append("Default: ").Append(info.DefaultString);
            sb.Append("   Type: ").Append(info.TypeName);
            if (info.HasRange) sb.Append("   Range: ").Append(info.Min).Append(" to ").Append(info.Max);
            sb.Append('\n').Append(info.IsLocal
                ? "Saved on your own machine; nobody else is affected."
                : "Shared with everyone on the server.");
            if (info.Tier == SettingTier.Admin) sb.Append("   Admins only.");
            if (!info.Live) sb.Append("   Needs a server restart.");
            return sb.ToString();
        }

        // ---- the four control kinds ------------------------------------------------------------------

        private void BuildBool(Row row, RectTransform control)
        {
            row.Toggle = UiKit.Toggle(control);
            if (row.Toggle == null) return;
            var trt = (RectTransform)row.Toggle.transform;
            trt.anchorMin = new Vector2(0f, 0.5f);
            trt.anchorMax = new Vector2(0f, 0.5f);
            trt.pivot = new Vector2(0f, 0.5f);
            trt.anchoredPosition = new Vector2(0f, 0f);
            var info = row.Info;
            row.Toggle.onValueChanged.AddListener(delegate (bool on)
            {
                if (_suppress) return;
                Apply(info, on ? "true" : "false");
            });
        }

        private void BuildNumber(Row row, RectTransform control)
        {
            var info = row.Info;

            row.Input = UiKit.Input(control, 84f, 26f);
            if (row.Input != null)
            {
                var irt = (RectTransform)row.Input.transform;
                irt.anchorMin = new Vector2(1f, 0.5f);
                irt.anchorMax = new Vector2(1f, 0.5f);
                irt.pivot = new Vector2(1f, 0.5f);
                irt.anchoredPosition = new Vector2(0f, 0f);
                row.Input.contentType = info.TypeName == "int"
                    ? TMP_InputField.ContentType.IntegerNumber
                    : TMP_InputField.ContentType.DecimalNumber;
                row.Input.onEndEdit.AddListener(delegate (string s)
                {
                    if (_suppress) return;
                    Apply(info, s);
                });
            }

            if (!info.HasRange) return;

            row.Slider = UiKit.Slider(control);
            if (row.Slider == null) return;
            var srt = (RectTransform)row.Slider.transform;
            srt.anchorMin = new Vector2(0f, 0.5f);
            srt.anchorMax = new Vector2(1f, 0.5f);
            srt.pivot = new Vector2(0.5f, 0.5f);
            srt.offsetMin = new Vector2(0f, -10f);
            srt.offsetMax = new Vector2(-92f, 10f);

            row.Slider.minValue = (float)info.Min;
            row.Slider.maxValue = (float)info.Max;
            row.Slider.wholeNumbers = info.TypeName == "int";

            // Show the number as it is dragged, but only ask the server when the drag ends -
            // otherwise one sweep of the slider would eat the whole rate limit.
            row.Slider.onValueChanged.AddListener(delegate (float v)
            {
                if (_suppress || row.Input == null) return;
                row.Input.SetTextWithoutNotify(Quantise(info, v));
            });
            var commit = row.Slider.gameObject.AddComponent<SliderCommit>();
            commit.OnCommit = delegate
            {
                if (_suppress) return;
                Apply(info, Quantise(info, row.Slider.value));
            };
        }

        private static string Quantise(SettingInfo info, float v)
        {
            if (info.TypeName == "int")
                return ((long)Mathf.Round(v)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            double step = info.Step > 0 ? info.Step : 0.01;
            double snapped = Math.Round(v / step) * step;
            return ((float)snapped).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        }

        private void BuildText(Row row, RectTransform control)
        {
            var info = row.Info;
            row.Input = UiKit.Input(control, 280f, 26f);
            if (row.Input == null) return;
            UiKit.Stretch((RectTransform)row.Input.transform);
            row.Input.onEndEdit.AddListener(delegate (string s)
            {
                if (_suppress) return;
                Apply(info, s);
            });
        }

        /// <summary>
        /// A pick-one. There is no plain Dropdown on any vanilla settings page (the graphics page
        /// uses GUIFramework's GuiDropdown, from an assembly the mod does not reference), so this
        /// mirrors what vanilla itself does for Language and the graphics preset: a value with a
        /// left and right arrow.
        /// </summary>
        private void BuildCycle(Row row, RectTransform control, string[] choices)
        {
            var info = row.Info;
            if (choices == null || choices.Length == 0) { BuildText(row, control); return; }

            row.Left = UiKit.Button(control, "<");
            row.Right = UiKit.Button(control, ">");
            row.CycleText = UiKit.Label(control, "", UiKit.BaseFontSize * 0.9f, TextAlignmentOptions.Midline);

            if (row.Left != null)
            {
                var lrt = (RectTransform)row.Left.transform;
                lrt.anchorMin = new Vector2(0f, 0.5f); lrt.anchorMax = new Vector2(0f, 0.5f);
                lrt.pivot = new Vector2(0f, 0.5f);
                lrt.anchoredPosition = Vector2.zero; lrt.sizeDelta = new Vector2(30f, 26f);
                row.Left.onClick.AddListener(delegate { Cycle(info, choices, -1); });
            }
            if (row.Right != null)
            {
                var rrt = (RectTransform)row.Right.transform;
                rrt.anchorMin = new Vector2(1f, 0.5f); rrt.anchorMax = new Vector2(1f, 0.5f);
                rrt.pivot = new Vector2(1f, 0.5f);
                rrt.anchoredPosition = Vector2.zero; rrt.sizeDelta = new Vector2(30f, 26f);
                row.Right.onClick.AddListener(delegate { Cycle(info, choices, 1); });
            }
            var trt = (RectTransform)row.CycleText.transform;
            trt.anchorMin = new Vector2(0f, 0.5f); trt.anchorMax = new Vector2(1f, 0.5f);
            trt.pivot = new Vector2(0.5f, 0.5f);
            trt.offsetMin = new Vector2(34f, -13f);
            trt.offsetMax = new Vector2(-34f, 13f);
        }

        private void Cycle(SettingInfo info, string[] choices, int delta)
        {
            string current = info.CurrentString;
            int at = 0;
            for (int i = 0; i < choices.Length; i++)
                if (string.Equals(choices[i], current, StringComparison.OrdinalIgnoreCase)) { at = i; break; }
            int next = ((at + delta) % choices.Length + choices.Length) % choices.Length;
            Apply(info, choices[next]);
        }

        /// <summary>Fires the callback when a drag on the slider ends, not on every frame of it.</summary>
        internal sealed class SliderCommit : MonoBehaviour, IPointerUpHandler, IDeselectHandler
        {
            public Action OnCommit;
            public void OnPointerUp(PointerEventData eventData) { if (OnCommit != null) OnCommit(); }
            public void OnDeselect(BaseEventData eventData) { if (OnCommit != null) OnCommit(); }
        }

        // ---- applying and refreshing ---------------------------------------------------------------------

        private void Apply(SettingInfo info, string value)
        {
            if (info == null) return;
            var why = WhyNot(info, value);
            if (why != null) { SetStatus(false, why); RefreshRow(FindRow(info)); return; }
            TweakDoor.Request(info, value);
        }

        /// <summary>The client-side half of the permission check: enough to grey a row honestly.
        /// The server checks again and has the last word.</summary>
        private static string WhyNot(SettingInfo info, string proposed)
        {
            bool connected = ZNet.instance != null;
            bool admin = !connected || LocalIsAdmin();

            // A SmoothServer row. This client cannot know whether the *server* runs SmoothServer
            // at all - only the server can answer that, and it does, in one sentence in the status
            // strip. So the row stays live and the honest local checks are the ones below.
            if (info.Foreign)
            {
                if (info.IsLocal) return null;
                if (!connected) return "Join a server to change this";
                if (info.Tier == SettingTier.Admin && !admin) return "Only a server admin can change this";
                if (AccessModule.AdminsOnly && !admin) return "Only server admins can change settings here";
                if (!AccessModule.DoorOpen) return "Switched off on this server";
                return null;
            }

            if (!info.Live) return "Needs a server restart";
            if (info.IsLocal)
            {
                if (connected && info.Tier == SettingTier.Admin && !admin)
                    return "Only a server admin can change this";
                return null;
            }

            if (!connected) return "Join a server to change this";
            if (info.Tier == SettingTier.Admin && !admin) return "Only a server admin can change this";
            if (AccessModule.AdminsOnly && !admin) return "Only server admins can change settings here";
            if (!AccessModule.DoorOpen) return "Switched off on this server";

            if (info.IsEnabledToggle && info.Owner != null && !info.Owner.BootEnabled)
            {
                bool wantOn;
                if (proposed == null) { if (!info.Owner.Enabled) return "Needs a server restart"; }
                else if (SettingValue.TryParseBool(proposed, out wantOn) && wantOn) return "Needs a server restart";
            }
            return null;
        }

        private static bool LocalIsAdmin()
        {
            try { return ZNet.instance != null && ZNet.instance.LocalPlayerIsAdminOrHost(); }
            catch { return false; }
        }

        private Row FindRow(SettingInfo info)
        {
            foreach (var r in _rows) if (r.Info == info) return r;
            return null;
        }

        private void OnAnySettingChanged(object sender, SettingChangedEventArgs e)
        {
            try
            {
                var info = ConfigCatalog.Find(e.ChangedSetting.Definition.Section,
                                              e.ChangedSetting.Definition.Key);
                if (info == null) return;
                RefreshRow(FindRow(info));
                RefreshModuleRow(info);
                RefreshHeader();
            }
            catch (Exception ex)
            {
                NoVikingLeftBehindPlugin.Log.LogWarning("[SettingsMenu] live refresh: " + ex.Message);
            }
        }

        /// <summary>SmoothServer's own config changed (its ServerSync push, its watcher, or us).</summary>
        private void OnForeignSettingChanged(object sender, SettingChangedEventArgs e)
        {
            try
            {
                var info = SmoothServerBridge.Find(e.ChangedSetting.Definition.Section,
                                                   e.ChangedSetting.Definition.Key);
                if (info == null) return;      // one of the ~55 knobs this panel does not show
                RefreshRow(FindRow(info));
            }
            catch (Exception ex)
            {
                NoVikingLeftBehindPlugin.Log.LogWarning("[SettingsMenu] network refresh: " + ex.Message);
            }
        }

        private void OnDoorResult(bool ok, string message) { SetStatus(ok, message); }

        private void OnAuditChanged() { RefreshHeader(); }

        private void SetStatus(bool ok, string message)
        {
            if (_statusText == null) return;
            _statusText.text = message ?? "";
            _statusText.color = ok ? UiKit.HintColor : new Color(0.95f, 0.55f, 0.4f, 0.95f);
        }

        private void RefreshAll()
        {
            foreach (var r in _rows) RefreshRow(r);
            foreach (var mr in _moduleRows) RefreshModuleRowDirect(mr);
            RefreshHeader();
        }

        private void RefreshHeader()
        {
            if (_accessText != null)
            {
                string who = ZNet.instance == null
                    ? "Not on a server: only your own per-player settings can be changed here."
                    : AccessModule.WhoMayTweakText() + "  Changes apply live to everyone.";
                _accessText.text = who;
            }
            if (_auditText != null)
            {
                var lines = TweakDoor.AuditLines;
                if (lines == null || lines.Count == 0) { _auditText.text = ""; UiKit.Tip(_auditText.gameObject, null); }
                else
                {
                    // The strip is narrow, so each line is cut at 60 characters with an ellipsis;
                    // the whole thing, uncut, is on the tooltip.
                    var shown = new System.Text.StringBuilder("Recent changes\n");
                    var full = new System.Text.StringBuilder("Recent changes on this server\n\n");
                    for (int i = 0; i < lines.Count; i++)
                    {
                        var line = lines[i] ?? "";
                        shown.Append(line.Length > 60 ? line.Substring(0, 59) + "…" : line).Append('\n');
                        full.Append(line).Append('\n');
                    }
                    _auditText.text = shown.ToString();
                    UiKit.Tip(_auditText.gameObject, full.ToString());
                }
            }
            if (_resetButton != null) _resetButton.interactable = _selected != null;
        }

        private void RefreshModuleRow(SettingInfo info)
        {
            if (info == null || !info.IsEnabledToggle) return;
            foreach (var mr in _moduleRows)
                if (mr.EnabledInfo == info) { RefreshModuleRowDirect(mr); return; }
        }

        private void RefreshModuleRowDirect(ModuleRow mr)
        {
            if (mr == null || mr.Module == null || mr.Enabled == null || mr.EnabledInfo == null) return;
            _suppress = true;
            try
            {
                bool on = mr.Module.Enabled;
                mr.Enabled.isOn = on;
                string why = WhyNot(mr.EnabledInfo, on ? "false" : "true");
                mr.Enabled.interactable = why == null;
                mr.Label.color = on ? UiKit.TextColor : UiKit.DimColor;
            }
            finally { _suppress = false; }
        }

        private void RefreshRow(Row row)
        {
            if (row == null || row.Info == null) return;
            var info = row.Info;
            string why = WhyNot(info, null);
            bool editable = why == null;
            row.Editable = editable;

            _suppress = true;
            try
            {
                string current = info.CurrentString;

                if (row.Toggle != null) { row.Toggle.isOn = info.Entry.BoxedValue is bool && (bool)info.Entry.BoxedValue; }
                if (row.Input != null) row.Input.SetTextWithoutNotify(current);
                if (row.Slider != null)
                {
                    float v;
                    if (float.TryParse(current, System.Globalization.NumberStyles.Float,
                                       System.Globalization.CultureInfo.InvariantCulture, out v))
                        row.Slider.SetValueWithoutNotify(Mathf.Clamp(v, row.Slider.minValue, row.Slider.maxValue));
                }
                if (row.CycleText != null) row.CycleText.text = current;

                if (row.Toggle != null) row.Toggle.interactable = editable;
                if (row.Input != null) row.Input.interactable = editable;
                if (row.Slider != null) row.Slider.interactable = editable;
                if (row.Left != null) row.Left.interactable = editable;
                if (row.Right != null) row.Right.interactable = editable;
                if (row.Reset != null) row.Reset.interactable = editable && current != info.DefaultString;

                var tone = editable ? UiKit.TextColor : UiKit.DimColor;
                if (row.Label != null) row.Label.color = tone;
                if (row.Hint != null)
                    row.Hint.color = editable ? UiKit.HintColor
                                             : new Color(UiKit.HintColor.r, UiKit.HintColor.g,
                                                         UiKit.HintColor.b, 0.35f);

                // A row that cannot be changed shows the reason in place of its control.
                if (row.Note != null)
                {
                    bool showNote = !editable && why != null;
                    row.Note.text = why ?? "";
                    row.Note.gameObject.SetActive(showNote);
                    if (row.Toggle != null) row.Toggle.gameObject.SetActive(!showNote);
                    if (row.Slider != null) row.Slider.gameObject.SetActive(!showNote);
                    if (row.Input != null) row.Input.gameObject.SetActive(!showNote);
                    if (row.Left != null) row.Left.gameObject.SetActive(!showNote);
                    if (row.Right != null) row.Right.gameObject.SetActive(!showNote);
                    if (row.CycleText != null) row.CycleText.gameObject.SetActive(!showNote);
                }
            }
            finally { _suppress = false; }
        }
    }
}
