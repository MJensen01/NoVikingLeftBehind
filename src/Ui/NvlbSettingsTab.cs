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

        // ---- the pending-changes queue (0.8.2) -------------------------------------------------
        // Nothing a control does reaches the server until Save. The dictionary holds the queued
        // value per setting; the list keeps the order they were made in, so the audit reads the
        // way the player worked.
        private readonly Dictionary<SettingInfo, string> _pending = new Dictionary<SettingInfo, string>();
        private readonly List<SettingInfo> _pendingOrder = new List<SettingInfo>();
        private Button _saveButton, _discardButton;
        private RectTransform _unsavedDialog;

        /// <summary>The tint a queued row wears, so a page of edits can be read at a glance.</summary>
        private static readonly Color DirtyColor = new Color(1f, 0.82f, 0.42f, 1f);
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
            // OK means what it says on every other tab: apply. Since 0.8.2 this page holds its
            // edits until asked, so OK is the ask.
            try { if (_pendingOrder.Count > 0 || HasOpenEdit()) SaveChanges(); }
            catch (Exception e) { NoVikingLeftBehindPlugin.Log.LogError("[SettingsMenu] OnOk: " + e); }
            if (okActionCompletedCallback != null) okActionCompletedCallback();
        }

        /// <summary>
        /// Back drops whatever is still queued - it has not been sent, so there is nothing to
        /// revert. Anything already saved stays saved and was already announced to everyone; that
        /// is Undo's job, not this one. The prompt that offers a last chance to save lives in
        /// <see cref="AllowVanillaBack"/>, which runs before vanilla ever gets this far.
        /// </summary>
        public void OnBack()
        {
            _pending.Clear();
            _pendingOrder.Clear();
        }

        private bool HasOpenEdit()
        {
            foreach (var r in _rows)
            {
                if (r == null || r.Input == null || r.Info == null || !r.Editable) continue;
                if (!string.Equals(r.Input.text ?? "", Shown(r.Info) ?? "", StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        // ---- the unsaved-changes prompt ---------------------------------------------------------

        /// <summary>
        /// Called from a prefix on <c>Settings.OnBack</c> - which is both the Back button and the
        /// Escape key. Returning false stops vanilla closing the screen, so anything queued gets
        /// one question asked about it first. Anything that goes wrong here lets Back through:
        /// a bug in a prompt must never trap someone in the settings menu.
        /// </summary>
        internal static bool AllowVanillaBack()
        {
            var tab = Current;
            if (tab == null || !tab._built) return true;
            try
            {
                // Escape with the picker open means "close the picker", not "leave the settings".
                if (ListPicker.IsOpen) { ListPicker.Close(); return false; }

                if (tab._pendingOrder.Count == 0 && !tab.HasOpenEdit()) return true;
                tab.CommitOpenFields();
                if (tab._pendingOrder.Count == 0) return true;
                tab.ShowUnsavedDialog();
                return false;
            }
            catch (Exception e)
            {
                NoVikingLeftBehindPlugin.Log.LogError("[SettingsMenu] unsaved-changes prompt: " + e);
                return true;
            }
        }

        /// <summary>
        /// A dialog of our own, built from the same cloned parts as the rest of the page. Vanilla's
        /// popup needs a prefab handed to it that the settings screen does not reliably have, and
        /// three buttons on a dimmed panel is not worth a dependency.
        /// </summary>
        private void ShowUnsavedDialog()
        {
            if (_unsavedDialog != null)
            {
                RefreshUnsavedText();
                _unsavedDialog.gameObject.SetActive(true);
                _unsavedDialog.SetAsLastSibling();
                return;
            }

            var dim = UiKit.Fill(_page, new Color(0f, 0f, 0f, 0.72f));
            dim.raycastTarget = true;                       // swallow clicks on the page behind
            _unsavedDialog = (RectTransform)dim.transform;
            UiKit.Stretch(_unsavedDialog);

            var panel = UiKit.Fill(_unsavedDialog, new Color(0.09f, 0.08f, 0.06f, 0.98f));
            var prt = (RectTransform)panel.transform;
            prt.anchorMin = new Vector2(0.5f, 0.5f);
            prt.anchorMax = new Vector2(0.5f, 0.5f);
            prt.pivot = new Vector2(0.5f, 0.5f);
            prt.anchoredPosition = Vector2.zero;
            prt.sizeDelta = new Vector2(520f, 160f);

            var text = UiKit.Label(prt, "", UiKit.BaseFontSize, TextAlignmentOptions.Top);
            var txt = (RectTransform)text.transform;
            UiKit.Stretch(txt);
            txt.offsetMin = new Vector2(20f, 60f);
            txt.offsetMax = new Vector2(-20f, -18f);
            text.enableWordWrapping = true;
            text.overflowMode = TextOverflowModes.Overflow;
            _unsavedText = text;

            var save = UiKit.Button(prt, "Save", UiKit.BaseFontSize * 0.9f);
            if (save != null)
            {
                UiKit.Place((RectTransform)save.transform, 20f, 112f, 150f, 34f);
                save.onClick.AddListener(delegate { SaveChanges(); CloseDialogAndBack(); });
            }

            var discard = UiKit.Button(prt, "Discard", UiKit.BaseFontSize * 0.9f);
            if (discard != null)
            {
                UiKit.Place((RectTransform)discard.transform, 185f, 112f, 150f, 34f);
                discard.onClick.AddListener(delegate { DiscardChanges(); CloseDialogAndBack(); });
            }

            var cancel = UiKit.Button(prt, "Cancel", UiKit.BaseFontSize * 0.9f);
            if (cancel != null)
            {
                UiKit.Place((RectTransform)cancel.transform, 350f, 112f, 150f, 34f);
                cancel.onClick.AddListener(delegate { HideUnsavedDialog(); });
            }

            RefreshUnsavedText();
            _unsavedDialog.SetAsLastSibling();
        }

        private TMP_Text _unsavedText;

        private void RefreshUnsavedText()
        {
            if (_unsavedText == null) return;
            int n = _pendingOrder.Count;
            _unsavedText.text = "You have " + n + (n == 1 ? " unsaved change" : " unsaved changes") +
                                " on this page.\n\nSave sends them now. Discard throws them away. " +
                                "Cancel goes back to the page.";
        }

        private void HideUnsavedDialog()
        {
            if (_unsavedDialog != null) _unsavedDialog.gameObject.SetActive(false);
        }

        /// <summary>Close the prompt, then let vanilla do the Back it was asking to do.</summary>
        private void CloseDialogAndBack()
        {
            HideUnsavedDialog();
            try { if (_settings != null) _settings.OnBack(); }
            catch (Exception e) { NoVikingLeftBehindPlugin.Log.LogError("[SettingsMenu] closing: " + e); }
        }

        public void Terminate()
        {
            // Last chance to notice something typed and never committed. It only reaches the
            // queue, not the server - closing still needs an OK or the Save button.
            try { CommitOpenFields(); } catch { /* closing down */ }

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
                ListPicker.Forget();
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
            ListPicker.Tick();

            // One notch of the wheel moves one row in whichever pane the pointer is over.
            UiKit.TickScroll(_leftScroll, _leftKnob, ModuleRowH + 2f);
            UiKit.TickScroll(_rightScroll, _rightKnob, RowH);

            // A text field only raises its end-of-edit event on Enter or on a deselect, and on
            // this page focus can move to another row without either ever arriving - which is how
            // a typed value went missing entirely. Anything typed into a field nobody is in any
            // more gets queued here, once, the frame after the caret leaves.
            for (int i = 0; i < _rows.Count; i++)
            {
                var r = _rows[i];
                if (r == null || r.Info == null) continue;

                // A recorder waiting for a key needs a frame-by-frame look at the keyboard.
                if (r.Recorder != null) r.Recorder.Tick();

                if (r.Input == null || !r.Editable || r.Input.isFocused) continue;
                var typed = r.Input.text ?? "";
                if (!string.Equals(typed, Shown(r.Info) ?? "", StringComparison.Ordinal))
                    Apply(r.Info, typed);
            }

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
            _loggedHitRects = false;
            _loggedSliderRects = false;

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

            _saveButton = UiKit.Button(footer, "Save changes", UiKit.BaseFontSize * 0.9f);
            if (_saveButton != null)
            {
                UiKit.Place((RectTransform)_saveButton.transform, 8f, 2f, 190f, 34f);
                _saveButton.onClick.AddListener(delegate { SaveChanges(); });
                UiKit.Tip(_saveButton.gameObject,
                    "Send everything you have changed on this page, in the order you changed it. " +
                    "Nothing on this tab reaches the server until you press this.");
            }

            _discardButton = UiKit.Button(footer, "Discard", UiKit.BaseFontSize * 0.9f);
            if (_discardButton != null)
            {
                UiKit.Place((RectTransform)_discardButton.transform, 206f, 2f, 120f, 34f);
                _discardButton.onClick.AddListener(delegate { DiscardChanges(); });
                UiKit.Tip(_discardButton.gameObject,
                    "Throw the waiting changes away and put every control back to the value that " +
                    "is actually in force.");
            }

            _undoButton = UiKit.Button(footer, "Undo last change", UiKit.BaseFontSize * 0.9f);
            if (_undoButton != null)
            {
                // Place() measures DOWN from the top of the footer band, which is the bottom
                // FooterH pixels of the page. Passing FooterH-6 put the buttons 24px BELOW the
                // page, on top of vanilla's own Back/OK row.
                UiKit.Place((RectTransform)_undoButton.transform, 334f, 2f, 200f, 34f);
                _undoButton.onClick.AddListener(delegate { TweakDoor.RequestUndo(); });
                UiKit.Tip(_undoButton.gameObject,
                    "Put the most recent change on this server back to what it was. " +
                    "The server keeps the last 20 changes per setting.");
            }

            _resetButton = UiKit.Button(footer, "Reset module to defaults", UiKit.BaseFontSize * 0.9f);
            if (_resetButton != null)
            {
                UiKit.Place((RectTransform)_resetButton.transform, 542f, 2f, 240f, 34f);
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
            RefreshFooter();
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

            /// <summary>The bar behind the row, shown only while this row is the selected one.</summary>
            public Image Highlight;

            /// <summary>For the rows that are not a module: the cfg section this row stands for.</summary>
            public string Section;

            /// <summary>This is the SmoothServer panel's row, which stands for no section at all.</summary>
            public bool IsNetwork;
        }

        /// <summary>How strong the bar behind the selected row is, over the tab's own text colour.</summary>
        private const float SelectedBarAlpha = 0.12f;

        private static Color SelectedBarColor()
        {
            return new Color(UiKit.TextColor.r, UiKit.TextColor.g, UiKit.TextColor.b, SelectedBarAlpha);
        }

        /// <summary>The selected row's name, lifted out of the page's own text colour - not a new one.</summary>
        private static Color SelectedTint(Color tone) { return Color.Lerp(tone, Color.white, 0.35f); }

        /// <summary>
        /// The faint parchment bar that marks the selected row. Built before anything else on the
        /// row so it sits behind the name, the pick area and the checkbox, and it never takes a
        /// click of its own - <see cref="UiKit.Fill"/> leaves raycastTarget off.
        /// </summary>
        private static Image SelectionBar(RectTransform rowRt)
        {
            var bar = UiKit.Fill(rowRt, SelectedBarColor());
            bar.gameObject.name = "NVLB_Selected";
            var brt = UiKit.Stretch((RectTransform)bar.transform);
            brt.offsetMin = new Vector2(2f, 1f);
            brt.offsetMax = new Vector2(-2f, -1f);
            brt.SetAsFirstSibling();
            bar.gameObject.SetActive(false);
            return bar;
        }

        /// <summary>Which of the left-hand rows the right-hand pane is currently showing.</summary>
        private bool IsSelectedRow(ModuleRow mr)
        {
            if (mr == null) return false;
            if (_network) return mr.IsNetwork;
            if (mr.IsNetwork) return false;
            if (_selected != null) return mr.Module == _selected;
            return mr.Module == null && !string.IsNullOrEmpty(_selectedSection) &&
                   mr.Section == _selectedSection;
        }

        private void BuildModuleList()
        {
            float y = 4f;
            string theme = null;

            foreach (var module in ConfigCatalog.ModulesForUi())
            {
              // One module that cannot be drawn must cost that module and nothing else. In 0.7.5
              // a single throw in here (UiKit.Tip, on the very first row) left the entire page
              // blank - the outer catch in Initialize is a backstop, not a plan.
              try
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
                    MakeHeaderClickable(head, theme, y, module);
                    y += ThemeRowH;
                }

                var rowGo = new GameObject("Mod_" + module.Name, typeof(RectTransform));
                rowGo.transform.SetParent(_leftContent, false);
                var rowRt = UiKit.Place((RectTransform)rowGo.transform, 0f, y, LeftW - 12f, ModuleRowH);
                var bar = SelectionBar(rowRt);

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
                // The name is the obvious thing to click and, up to 0.7.6, the one part of the row
                // that did nothing: the tooltip made it a raycast target, so the press landed on
                // the label rather than on the Pick button stretched underneath, and the label's
                // handler swallowed it. Only the bare strip between the name and the checkbox
                // still reached Pick. Give the label the same job instead of taking its tooltip
                // away - whichever of the two the pointer lands on now selects the module.
                UiKit.Tip(name.gameObject, ModuleTooltip(mod), delegate { Select(mod); });

                var mr = new ModuleRow
                {
                    Module = module,
                    Label = name,
                    Root = rowGo,
                    Highlight = bar,
                    Section = module.Section
                };

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
                    // On the toggle itself, with TipOnly - which attaches the hover without
                    // switching any raycast on. 0.8.3 put a full Tip on the hit patch instead,
                    // and the Hover that came with it answered the click before the Toggle ever
                    // saw it, which is why these boxes stopped responding entirely.
                    UiKit.TipOnly(toggle.gameObject, ModuleTooltip(mod));
                    LogHitRects("module '" + mod.Name + "'", toggle, name);
                }

                _moduleRows.Add(mr);
              }
              catch (Exception e)
              {
                  NoVikingLeftBehindPlugin.Log.LogError("[SettingsMenu] module row '" +
                      (module == null ? "?" : module.Name) + "' could not be built: " + e);
              }
              finally { y += ModuleRowH + 2f; }
            }

            // The Network panel, only when SmoothServer is actually loaded on this machine.
            // It is not one of our modules and has no Enabled toggle of its own - it is a window
            // onto five of another mod's settings, so it gets its own theme heading and one row.
            if (SmoothServerBridge.Available && SmoothServerBridge.Rows().Count > 0)
            {
              // Guarded like the module rows above: this row failing must not cost the list.
              try
              {
                var netHead = UiKit.Label(_leftContent, SmoothServerBridge.PanelTheme.ToUpperInvariant(),
                                          UiKit.BaseFontSize * 0.72f, TextAlignmentOptions.BottomLeft,
                                          UiKit.HintColor);
                UiKit.Place((RectTransform)netHead.transform, 6f, y, LeftW - 20f, ThemeRowH);
                y += ThemeRowH;

                var netGo = new GameObject("Sec_Network", typeof(RectTransform));
                netGo.transform.SetParent(_leftContent, false);
                var netRt = UiKit.Place((RectTransform)netGo.transform, 0f, y, LeftW - 12f, ModuleRowH);
                var netBar = SelectionBar(netRt);
                var netName = UiKit.Label(netRt, SmoothServerBridge.PanelLabel, UiKit.BaseFontSize * 0.92f,
                                          TextAlignmentOptions.MidlineLeft);
                UiKit.Place((RectTransform)netName.transform, 14f, 2f, LeftW - 30f, ModuleRowH - 4f);

                string netTip = SmoothServerBridge.PanelHint + "\n\nSmoothServer " +
                    SmoothServerBridge.Version + " is installed on this machine. These are its own " +
                    "settings, not this mod's - only the few that are worth reaching for mid-game " +
                    "are here; the rest stay in its config file.";

                var netPick = new GameObject("Pick", typeof(RectTransform));
                netPick.transform.SetParent(netRt, false);
                UiKit.Stretch((RectTransform)netPick.transform);
                UiKit.Tip(netPick, netTip);
                var netBtn = netPick.AddComponent<Button>();
                netBtn.transition = Selectable.Transition.None;
                netBtn.onClick.AddListener(delegate { SelectNetwork(); });
                // Same tooltip on the name, so making it clickable cannot cost the hover: the
                // label becomes a raycast target the moment it carries one, and would otherwise
                // hide the Pick area's tooltip along the whole width of the word.
                UiKit.Tip(netName.gameObject, netTip, delegate { SelectNetwork(); });

                _moduleRows.Add(new ModuleRow
                {
                    Module = null,
                    Label = netName,
                    Root = netGo,
                    Highlight = netBar,
                    IsNetwork = true
                });
              }
              catch (Exception e)
              {
                  NoVikingLeftBehindPlugin.Log.LogError("[SettingsMenu] the Network row could not be built: " + e);
              }
              finally { y += ModuleRowH + 2f; }
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
                  // Guarded like the module rows above: one section failing costs one row.
                  try
                  {
                    var rowGo = new GameObject("Sec_" + section, typeof(RectTransform));
                    rowGo.transform.SetParent(_leftContent, false);
                    var rowRt = UiKit.Place((RectTransform)rowGo.transform, 0f, y, LeftW - 12f, ModuleRowH);
                    var bar = SelectionBar(rowRt);
                    var name = UiKit.Label(rowRt, section, UiKit.BaseFontSize * 0.92f,
                                           TextAlignmentOptions.MidlineLeft);
                    UiKit.Place((RectTransform)name.transform, 14f, 2f, LeftW - 30f, ModuleRowH - 4f);

                    const string orphanTip = "Settings that apply to the whole mod rather than one feature.";

                    var picker = new GameObject("Pick", typeof(RectTransform));
                    picker.transform.SetParent(rowRt, false);
                    UiKit.Stretch((RectTransform)picker.transform);
                    string sec = section;
                    UiKit.Tip(picker, orphanTip);
                    var pickBtn = picker.AddComponent<Button>();
                    pickBtn.transition = Selectable.Transition.None;
                    pickBtn.onClick.AddListener(delegate { SelectSection(sec); });
                    // The name too - same tooltip, same action, so the whole row is one target.
                    UiKit.Tip(name.gameObject, orphanTip, delegate { SelectSection(sec); });

                    _moduleRows.Add(new ModuleRow
                    {
                        Module = null,
                        Label = name,
                        Root = rowGo,
                        Highlight = bar,
                        Section = section
                    });
                    _orphanSectionOf[rowGo] = section;
                  }
                  catch (Exception e)
                  {
                      NoVikingLeftBehindPlugin.Log.LogError("[SettingsMenu] section row '" +
                          section + "' could not be built: " + e);
                  }
                  finally { y += ModuleRowH + 2f; }
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

        /// <summary>
        /// A theme heading is a place in a long list, so make it behave like one: clicking it
        /// scrolls the list to that theme and selects the first module under it, and it lights up
        /// under the pointer so it reads as something you can press.
        /// </summary>
        private void MakeHeaderClickable(TMP_Text head, string theme, float y, FeatureModule first)
        {
            if (head == null) return;

            var hot = new GameObject("ThemeHit", typeof(RectTransform));
            hot.transform.SetParent(_leftContent, false);
            UiKit.Place((RectTransform)hot.transform, 0f, y, LeftW - 12f, ThemeRowH);

            var btn = hot.AddComponent<Button>();
            btn.transition = Selectable.Transition.None;
            float target = y;
            var firstModule = first;
            btn.onClick.AddListener(delegate
            {
                ScrollLeftTo(target);
                if (firstModule != null) Select(firstModule);
            });

            UiKit.Tip(hot, "Jump to " + theme);

            var tint = hot.AddComponent<HeaderTint>();
            tint.Target = head;
            tint.Normal = UiKit.HintColor;
            tint.Lit = UiKit.TextColor;
        }

        /// <summary>Lights a heading while the pointer is on it. Nothing else, deliberately.</summary>
        private sealed class HeaderTint : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
        {
            public TMP_Text Target;
            public Color Normal, Lit;
            public void OnPointerEnter(PointerEventData e) { if (Target != null) Target.color = Lit; }
            public void OnPointerExit(PointerEventData e) { if (Target != null) Target.color = Normal; }
            private void OnDisable() { if (Target != null) Target.color = Normal; }
        }

        /// <summary>
        /// Once per page build, print where the things you are supposed to be able to click
        /// actually ARE, on screen, in pixels. Two rounds were spent guessing at this from
        /// screenshots; a line in the log settles it.
        /// </summary>
        private static bool _loggedHitRects;

        private static void LogHitRects(string what, Toggle toggle, TMP_Text label)
        {
            if (_loggedHitRects || toggle == null) return;
            _loggedHitRects = true;
            try
            {
                var hit = UiKit.HitAreaOf(toggle);
                NoVikingLeftBehindPlugin.Log.LogInfo(
                    "[SettingsMenu] hit rects for " + what +
                    ": toggle " + UiKit.ScreenRect((RectTransform)toggle.transform) +
                    " | box " + (toggle.targetGraphic == null ? "<none>"
                                 : UiKit.ScreenRect(toggle.targetGraphic.rectTransform)) +
                    " | clickable patch " + (hit == null || hit == toggle.gameObject
                                 ? "<none - the whole toggle>"
                                 : UiKit.ScreenRect((RectTransform)hit.transform)) +
                    " | name " + (label == null ? "<none>" : UiKit.ScreenRect((RectTransform)label.transform)));
            }
            catch (Exception e) { NoVikingLeftBehindPlugin.Log.LogWarning("[SettingsMenu] hit-rect log: " + e.Message); }
        }

        private static bool _loggedSliderRects;

        private static void LogSliderRects(Row row)
        {
            if (_loggedSliderRects || row == null || row.Slider == null) return;
            _loggedSliderRects = true;
            try
            {
                var srt = (RectTransform)row.Slider.transform;
                string widest = "";
                float minX = float.MaxValue, maxX = float.MinValue;
                var corners = new Vector3[4];
                foreach (var g in row.Slider.GetComponentsInChildren<Graphic>(true))
                {
                    if (g == null) continue;
                    g.rectTransform.GetWorldCorners(corners);
                    if (corners[0].x < minX) { minX = corners[0].x; widest = g.gameObject.name; }
                    if (corners[2].x > maxX) maxX = corners[2].x;
                }
                NoVikingLeftBehindPlugin.Log.LogInfo(
                    "[SettingsMenu] slider rects for [" + row.Info.Section + "] " + row.Info.Key +
                    ": slider " + UiKit.ScreenRect(srt) +
                    " | its graphics span x " + minX.ToString("0") + ".." + maxX.ToString("0") +
                    " (leftmost '" + widest + "')" +
                    " | label " + (row.Label == null ? "<none>" : UiKit.ScreenRect((RectTransform)row.Label.transform)));
            }
            catch (Exception e) { NoVikingLeftBehindPlugin.Log.LogWarning("[SettingsMenu] slider-rect log: " + e.Message); }
        }

        /// <summary>Put a y offset from the top of the module list at the top of the visible pane.</summary>
        private void ScrollLeftTo(float y)
        {
            if (_leftScroll == null || _leftContent == null || _leftScroll.viewport == null) return;
            float max = Mathf.Max(0f, _leftContent.rect.height - _leftScroll.viewport.rect.height);
            var p = _leftContent.anchoredPosition;
            p.y = Mathf.Clamp(y - 4f, 0f, max);
            _leftContent.anchoredPosition = p;
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
            public KeyRecorder.Handle Recorder;
            public Button PickButton, RawButton;
            public bool Raw;
            public bool Editable;
        }

        /// <summary>
        /// True while the right-hand pane is being rebuilt. Building a row touches config values,
        /// refreshes and captions, any of which could reach back here - and a rebuild that starts
        /// another rebuild is not slow, it is fatal: unbounded recursion overflows the stack, and
        /// Mono cannot catch that. The process simply stops, with no exception, no crash dump and
        /// a log that ends mid-line. That is exactly what 0.8.5 did the first time anyone opened
        /// a module owning a list setting, and this guard is the cheap insurance against the next
        /// one of its kind.
        /// </summary>
        private bool _rebuilding;

        private void RebuildRight()
        {
            if (_rebuilding)
            {
                NoVikingLeftBehindPlugin.Log.LogWarning(
                    "[SettingsMenu] something asked to rebuild the settings pane while it was " +
                    "already being built - ignored. This is a bug, but a survivable one.");
                return;
            }
            _rebuilding = true;
            try { RebuildRightCore(); }
            finally { _rebuilding = false; }
        }

        private void RebuildRightCore()
        {
            // Read every open field BEFORE the pane is torn down. This rebuild runs on each
            // keystroke in the search box and on every click in the module list, and Unity's
            // Destroy does not run a focused field's deselect first - so an edit still being
            // typed when one of those happened was lost outright, with no write and no error.
            // Reading the text here does not depend on any event arriving in time.
            CommitOpenFields();

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

                // One row that cannot be built must cost that row and nothing else. Before 0.7.6
                // a single throw in here left the whole page blank.
                //
                // Named BEFORE it is built, and timed. A row that hangs - or overflows the stack,
                // which Mono cannot catch and which simply stops the process - leaves no
                // exception and no crash dump, so without this line there is nothing in the log
                // to say which row the game died on. That cost a whole release to find once.
                NoVikingLeftBehindPlugin.Log.LogInfo("[SettingsMenu] building row [" + info.Section +
                                                     "] " + info.Key);
                var started = Time.realtimeSinceStartup;
                try { _rows.Add(BuildRow(info, y)); }
                catch (Exception e)
                {
                    NoVikingLeftBehindPlugin.Log.LogError("[SettingsMenu] row [" + info.Section + "] " +
                                                          info.Key + " could not be built: " + e);
                }
                float took = (Time.realtimeSinceStartup - started) * 1000f;
                if (took > 50f)
                    NoVikingLeftBehindPlugin.Log.LogWarning("[SettingsMenu] row [" + info.Section + "] " +
                        info.Key + " took " + took.ToString("0") + " ms to build");
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
            _netReset = UiKit.Button(_rightContent, NetResetIdle, UiKit.BaseFontSize * 0.9f);
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

            // Since 0.8.1 these hotkeys are real ZInput bindings and also appear on vanilla's
            // Keyboard & Mouse page, where a rebind beats whatever is set here. Say so on the row,
            // or the two screens look like they disagree.
            string hint = info.Hint ?? "";
            if (KeyRecorder.IsKeySetting(info))
            {
                var id = NvlbKeys.IdForSetting(info.Section, info.Key);
                hint = (hint.Length > 0 ? hint + "  " : "") +
                       (string.IsNullOrEmpty(id)
                            ? "A rebind on the Keyboard & Mouse page wins over this."
                            : "Sets the binding itself - the same one the Keyboard & Mouse page shows" +
                              (NvlbKeys.HasSavedBinding(id)
                                   ? ", currently " + NvlbKeys.Label(id) + "."
                                   : "."));
            }

            row.Hint = UiKit.Label(rt, hint, UiKit.BaseFontSize * 0.78f,
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
            row.Reset = UiKit.Button(rt, _resetGlyph, UiKit.BaseFontSize * 0.9f);
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

            // Another mod's rows get plain prose and nothing else. Default/Type/Range is useful
            // to someone editing our own config file; on a borrowed setting it is just clutter
            // under an already long explanation.
            if (!info.Foreign)
            {
                sb.Append('\n').Append("Default: ").Append(info.DefaultString);
                sb.Append("   Type: ").Append(info.TypeName);
                if (info.HasRange) sb.Append("   Range: ").Append(info.Min).Append(" to ").Append(info.Max);
            }
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
            LogSliderRects(row);
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

        /// <summary>
        /// A list setting's control: a summary of what is picked, a button that opens the tick
        /// list, and a "Raw" toggle that brings back the plain text field. The summary button and
        /// the field are both here at once and one of them is hidden, so switching between them
        /// costs nothing and the row never has to be rebuilt.
        /// </summary>
        private void BuildPicker(Row row, RectTransform control)
        {
            var info = row.Info;

            row.PickButton = UiKit.Button(control, "", UiKit.BaseFontSize * 0.85f);
            if (row.PickButton != null)
            {
                var brt = (RectTransform)row.PickButton.transform;
                UiKit.Stretch(brt);
                brt.offsetMin = new Vector2(0f, -13f);
                brt.offsetMax = new Vector2(-56f, 13f);
                var captured = info;
                row.PickButton.onClick.AddListener(delegate
                {
                    ListPicker.Open(_page, captured, Shown(captured),
                                    delegate (string picked) { Apply(captured, picked); });
                });
                UiKit.Tip(row.PickButton.gameObject,
                    "Pick from what is actually in this world, by name. Anything already listed " +
                    "that this world does not have is kept and flagged rather than dropped.");
            }

            row.RawButton = UiKit.Button(control, "Raw", UiKit.BaseFontSize * 0.8f);
            if (row.RawButton != null)
            {
                var rrt = (RectTransform)row.RawButton.transform;
                rrt.anchorMin = new Vector2(1f, 0.5f);
                rrt.anchorMax = new Vector2(1f, 0.5f);
                rrt.pivot = new Vector2(1f, 0.5f);
                rrt.anchoredPosition = Vector2.zero;
                rrt.sizeDelta = new Vector2(52f, 26f);
                var r = row;
                row.RawButton.onClick.AddListener(delegate { ToggleRaw(r); });
                UiKit.Tip(row.RawButton.gameObject, "Edit the list as text instead.");
            }

            // BuildTextField, NOT BuildText. BuildText is the dispatcher: it looks at the setting
            // and sends a picker-backed one straight back here, so calling it from here was
            // BuildPicker -> BuildText -> BuildPicker without end. That is a stack overflow, and
            // Mono cannot catch one - the process simply stops, which is why the log ended mid
            // rebuild with no exception and no crash dump the first time anyone opened a module
            // that owns a list setting.
            //
            // The field is built anyway and starts hidden: it is the same field every other free
            // text row uses, so the pending queue and the commit-on-focus-loss sweep work here
            // without a special case.
            BuildTextField(row, control);
            if (row.Input != null)
            {
                var irt = (RectTransform)row.Input.transform;
                irt.offsetMax = new Vector2(-56f, irt.offsetMax.y);
                row.Input.gameObject.SetActive(false);
            }
        }

        private void ToggleRaw(Row row)
        {
            if (row == null) return;
            row.Raw = !row.Raw;
            if (row.Input != null) row.Input.gameObject.SetActive(row.Raw);
            if (row.PickButton != null) row.PickButton.gameObject.SetActive(!row.Raw);
            SetCaption(row.RawButton, row.Raw ? "List" : "Raw");
            RefreshRow(row);
        }

        private void BuildText(Row row, RectTransform control)
        {
            var info = row.Info;

            // A key is not free text. Typing one is how a tester ended up with a lowercase "p"
            // in the config, which Unity's key parser refuses - so a key setting gets a recorder
            // instead: press the key you want and it stores the canonical name.
            if (KeyRecorder.IsKeySetting(info))
            {
                var captured = info;
                row.Recorder = KeyRecorder.Build(control, Shown(info),
                                                 delegate (string picked) { Apply(captured, picked); });
                return;
            }

            // A list of things that exist in the world is ticked off a list, not typed from
            // memory - nobody knows every prefab name in the game. The text field is still there
            // behind the "Raw" button for anyone who would rather type.
            if (info.Picker != null)
            {
                BuildPicker(row, control);
                return;
            }

            BuildTextField(row, control);
        }

        /// <summary>
        /// Just the text field, with no decision about what kind of setting this is. Kept separate
        /// from <see cref="BuildText"/> - which dispatches to the recorder or the picker - so that
        /// the picker can ask for a plain field without being handed straight back to itself.
        /// </summary>
        private void BuildTextField(Row row, RectTransform control)
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

            // With an explicit size, not the donor's auto-sizing: that path leaves the caption in
            // the donor's own caption rect, which can be shorter than the text it is given, and
            // TMP then draws nothing at all. It is what left these two arrows as blank buttons
            // either side of the value - the same fault the footer buttons had in 0.7.4.
            row.Left = UiKit.Button(control, "<", UiKit.BaseFontSize);
            row.Right = UiKit.Button(control, ">", UiKit.BaseFontSize);

            // What this row can actually cycle through, once, at build. A value the arrows never
            // reach is either missing from here or spelt differently from what is stored.
            NoVikingLeftBehindPlugin.Log.LogInfo("[SettingsMenu] cycler [" + info.Section + "] " +
                info.Key + " choices: " + string.Join(" | ", choices) + "  (now '" + Shown(info) + "')");
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
            // Shown(), not CurrentString. Since 0.8.2 an edit only queues, so the LIVE value stops
            // moving the moment you click - and stepping from the live value meant every click
            // went to the same neighbour, one step from where the row started. On a three-value
            // preset that looked exactly like the middle one being skipped: Default went to
            // FastLink and stayed there, and going the other way went to Custom and stayed there,
            // so FastLink could never be reached from Custom. Every enum row had this, not just
            // the network preset.
            if (choices == null || choices.Length == 0) return;

            string current = Shown(info);
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

        /// <summary>
        /// Since 0.8.2 a control does NOT send anything. It queues. Every edit lands here, is
        /// validated the same way it always was, and is then held until the player presses Save -
        /// so a mis-click, a slider knocked on the way past, or a value typed and thought better
        /// of costs nothing. An edit that puts a setting back to the value it already has is
        /// taken off the queue rather than queued as a change to nothing.
        /// </summary>
        private void Apply(SettingInfo info, string value)
        {
            if (info == null) return;
            var why = WhyNot(info, value);
            if (why != null) { SetStatus(false, why); RefreshRow(FindRow(info)); return; }

            if (string.Equals(value ?? "", info.CurrentString ?? "", StringComparison.Ordinal))
            {
                if (_pending.Remove(info)) _pendingOrder.Remove(info);
            }
            else
            {
                if (!_pending.ContainsKey(info)) _pendingOrder.Add(info);
                _pending[info] = value;
            }

            RefreshRow(FindRow(info));
            RefreshModuleRow(info);
            RefreshFooter();
            SetStatus(true, _pendingOrder.Count == 0
                ? "Nothing waiting."
                : _pendingOrder.Count + (_pendingOrder.Count == 1 ? " change waiting" : " changes waiting") +
                  " - press Save changes.");
        }

        /// <summary>What a control should be showing: the queued value if there is one, else what is live.</summary>
        private string Shown(SettingInfo info)
        {
            string v;
            return (info != null && _pending.TryGetValue(info, out v)) ? v : (info == null ? "" : info.CurrentString);
        }

        private bool IsDirty(SettingInfo info) { return info != null && _pending.ContainsKey(info); }

        /// <summary>
        /// Send the queue through the door, in the order it was made, one tweak each - so the
        /// audit still names every setting that moved and Undo still works change by change.
        /// </summary>
        private void SaveChanges()
        {
            CommitOpenFields();
            if (_pendingOrder.Count == 0) { SetStatus(true, "Nothing to save."); return; }

            // Snapshot both the order and the values BEFORE emptying the queue: the door raises
            // events as it goes, and a refresh in the middle of the loop must not find half a queue.
            int n = _pendingOrder.Count;
            var order = new List<SettingInfo>(_pendingOrder);
            var values = new List<string>(order.Count);
            foreach (var info in order) values.Add(_pending[info]);

            _pending.Clear();
            _pendingOrder.Clear();

            int sent = 0;
            for (int i = 0; i < order.Count; i++)
            {
                try { PushBinding(order[i], values[i]); TweakDoor.Request(order[i], values[i]); sent++; }
                catch (Exception e)
                {
                    NoVikingLeftBehindPlugin.Log.LogError("[SettingsMenu] could not send [" + order[i].Section +
                                                          "] " + order[i].Key + ": " + e);
                }
            }

            RefreshAll();
            RefreshFooter();
            SetStatus(true, "Sent " + sent + " of " + n + (n == 1 ? " change." : " changes."));
        }

        /// <summary>
        /// A hotkey row sets the BINDING, not just the config value. Since 0.8.4 the binding is
        /// the only thing consulted when a key is pressed - the config value is the default it
        /// starts from - so writing only the config would leave the row looking like it had done
        /// something while the key carried on doing what it did before. Both are written: the
        /// binding so the key changes now, the config so the default follows it.
        /// </summary>
        private static void PushBinding(SettingInfo info, string value)
        {
            if (info == null) return;
            try
            {
                var id = NvlbKeys.IdForSetting(info.Section, info.Key);
                if (string.IsNullOrEmpty(id)) return;

                var cleaned = ConfigCatalog.Unquote(value ?? "").Trim().Replace(" ", "");
                if (cleaned.Length == 0) { NvlbKeys.ClearBinding(id); return; }

                KeyCode key;
                if (!Enum.TryParse(cleaned, true, out key))
                {
                    NoVikingLeftBehindPlugin.Log.LogWarning("[SettingsMenu] '" + value +
                        "' is not a key name - the binding for " + id + " is unchanged");
                    return;
                }
                if (key == KeyCode.None) NvlbKeys.ClearBinding(id);
                else NvlbKeys.SetBinding(id, key);
            }
            catch (Exception e)
            {
                NoVikingLeftBehindPlugin.Log.LogError("[SettingsMenu] could not set the binding for [" +
                                                      info.Section + "] " + info.Key + ": " + e);
            }
        }

        /// <summary>Drop the queue and put every control back to what is actually live.</summary>
        private void DiscardChanges()
        {
            int n = _pendingOrder.Count;
            _pending.Clear();
            _pendingOrder.Clear();
            RefreshAll();
            RefreshFooter();
            SetStatus(true, n == 0 ? "Nothing to discard."
                                   : "Discarded " + n + (n == 1 ? " change." : " changes."));
        }

        /// <summary>
        /// A text field that still has the caret has not raised its end-of-edit event yet, and on
        /// this page focus can move to another row without one ever arriving - which is how a
        /// typed value went missing entirely before 0.8.2. Read every field that has drifted from
        /// what it should be showing, and queue it, before anything is sent.
        /// </summary>
        private void CommitOpenFields()
        {
            foreach (var r in _rows)
            {
                if (r == null || r.Input == null || r.Info == null || !r.Editable) continue;
                var typed = r.Input.text ?? "";
                if (!string.Equals(typed, Shown(r.Info) ?? "", StringComparison.Ordinal))
                    Apply(r.Info, typed);
            }
        }

        private void RefreshFooter()
        {
            int n = _pendingOrder.Count;
            if (_saveButton != null)
            {
                SetCaption(_saveButton, n == 0 ? "Save changes" : "Save changes (" + n + ")");
                _saveButton.interactable = n > 0;
            }
            if (_discardButton != null) _discardButton.interactable = n > 0;
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

        /// <summary>
        /// One left-hand row: its checkbox, its name's tone, and whether it is the selected one.
        /// Selection is settled here rather than in a pass of its own so that a live change to a
        /// module's Enabled - which comes through this same method - cannot quietly repaint the
        /// selected row's name back to the unselected colour.
        /// </summary>
        private void RefreshModuleRowDirect(ModuleRow mr)
        {
            if (mr == null) return;

            bool selected = IsSelectedRow(mr);
            if (mr.Highlight != null && mr.Highlight.gameObject.activeSelf != selected)
                mr.Highlight.gameObject.SetActive(selected);

            // The Network panel and the plugin-level sections are rows without a module behind
            // them: there is no Enabled toggle to read, so "off" never applies to them.
            bool on = mr.Module == null || mr.Module.Enabled;

            // These checkboxes queue like every other control, so what the box shows is the
            // queued value when there is one - not what the server currently says.
            bool dirty = mr.EnabledInfo != null && IsDirty(mr.EnabledInfo);
            if (dirty) on = string.Equals(Shown(mr.EnabledInfo), "true", StringComparison.OrdinalIgnoreCase);

            if (mr.Module != null && mr.Enabled != null && mr.EnabledInfo != null)
            {
                _suppress = true;
                try
                {
                    mr.Enabled.isOn = on;
                    mr.Enabled.interactable = WhyNot(mr.EnabledInfo, on ? "false" : "true") == null;
                }
                finally { _suppress = false; }
            }

            if (mr.Label != null)
            {
                var tone = dirty ? DirtyColor : (on ? UiKit.TextColor : UiKit.DimColor);
                mr.Label.color = selected ? SelectedTint(tone) : tone;
                if (mr.Module != null)
                    mr.Label.text = (dirty ? "• " : "") + mr.Module.Name;
            }
        }

        private void RefreshRow(Row row)
        {
            if (row == null || row.Info == null) return;
            var info = row.Info;
            string why = WhyNot(info, null);
            bool editable = why == null;
            row.Editable = editable;

            bool dirty = IsDirty(info);

            _suppress = true;
            try
            {
                string current = Shown(info);

                if (row.Toggle != null)
                {
                    row.Toggle.isOn = dirty
                        ? string.Equals(current, "true", StringComparison.OrdinalIgnoreCase)
                        : (info.Entry.BoxedValue is bool && (bool)info.Entry.BoxedValue);
                }
                if (row.Input != null) row.Input.SetTextWithoutNotify(current);
                if (row.Recorder != null && !row.Recorder.Recording) row.Recorder.SetValue(current);
                if (row.PickButton != null) SetCaption(row.PickButton, ListPicker.Summary(info.Picker, current));
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

                // A queued row says so: tinted, and marked with a bullet so it reads at a glance
                // even for anyone who cannot tell the two colours apart.
                var tone = !editable ? UiKit.DimColor : (dirty ? DirtyColor : UiKit.TextColor);
                if (row.Label != null)
                {
                    row.Label.color = tone;
                    row.Label.text = (dirty ? "• " : "") + info.Label;
                }
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
