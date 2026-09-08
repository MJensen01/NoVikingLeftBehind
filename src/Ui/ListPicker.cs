using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// The tick-list editor for a setting that is really a list of things in the world.
    ///
    /// `[Mining] OreNodes` reads `rock4_copper:1,MineRock_Tin:1,silvervein:3` and there is no way
    /// to edit that without already knowing every prefab name in the game - which is a fair
    /// description of nobody. This panel asks <see cref="PickerCandidates"/> what actually exists
    /// in the world being played, shows it by its in-game name with the prefab underneath, and
    /// writes the setting back in exactly the format its own module already parses. No module's
    /// parsing changes; the picker is only a better way to type the same string.
    ///
    /// Three things it is careful about:
    ///   * An entry already in the value that is not in this world - a prefab from a mod that is
    ///     not loaded - is never dropped. It is listed first, ticked, and flagged, so saving from
    ///     a machine that lacks a mod cannot quietly delete another server's settings.
    ///   * The item list can run to well over a thousand entries, so only a capped page of rows
    ///     is ever built and the search box does the rest. Building a thousand rows would stall
    ///     the menu for seconds.
    ///   * OK does not save. It writes into the tab's pending queue like every other control, so
    ///     Save and Discard still mean what they say.
    /// </summary>
    internal static class ListPicker
    {
        private const int MaxRows = 150;
        private const float RowH = 34f;

        private static RectTransform _root;
        private static RectTransform _listContent;
        private static ScrollRect _scroll;
        private static RectTransform _knob;
        private static TMP_InputField _search;
        private static TMP_Text _title, _count;

        private static PickerSpec _spec;
        private static Action<string> _onOk;
        private static string _filter = "";

        /// <summary>Every candidate, plus any unknown entry from the current value, in display order.</summary>
        private static readonly List<Candidate> _all = new List<Candidate>();

        /// <summary>Prefab -> the entry as it will be written. Absent means unticked.</summary>
        private static readonly Dictionary<string, Entry> _chosen =
            new Dictionary<string, Entry>();

        private static readonly HashSet<string> _unknown = new HashSet<string>();

        /// <summary>
        /// The prefabs the value already held, in the order it held them. Order carries meaning in
        /// at least one of these settings - `[CorpseRun] RespawnFoods` is "best first", and only
        /// the first few are ever used - so an entry that was already there keeps its place and
        /// only newly ticked ones go on the end. Writing them back in candidate order would have
        /// quietly reshuffled a list whose order was the whole point.
        /// </summary>
        private static readonly List<string> _storedOrder = new List<string>();
        private static readonly List<GameObject> _rows = new List<GameObject>();

        internal static bool IsOpen { get { return _root != null && _root.gameObject.activeSelf; } }

        // ---- opening ---------------------------------------------------------------------------

        /// <summary>
        /// Show the picker for one setting. <paramref name="onOk"/> is handed the serialised value
        /// and is not called at all if the panel is cancelled.
        /// </summary>
        internal static void Open(RectTransform page, SettingInfo info, string current, Action<string> onOk)
        {
            if (page == null || info == null || info.Picker == null) return;

            try
            {
                _spec = info.Picker;
                _onOk = onOk;
                _filter = "";

                LoadState(current);
                Build(page);

                if (_title != null)
                    _title.text = info.Label + "   -   " + _all.Count +
                                  (PickerCandidates.Available ? " to choose from" : " (join a world to see the full list)");

                if (_search != null) _search.SetTextWithoutNotify("");
                _root.gameObject.SetActive(true);
                _root.SetAsLastSibling();
                Refill();
            }
            catch (Exception e)
            {
                NoVikingLeftBehindPlugin.Log.LogError("[SettingsMenu] could not open the picker for [" +
                                                      info.Section + "] " + info.Key + ": " + e);
                Close();
            }
        }

        /// <summary>
        /// Work out what is ticked, and what the world can offer. Anything in the value that the
        /// world does not know about is added to the top of the list under its own flag rather
        /// than being quietly lost.
        /// </summary>
        private static void LoadState(string current)
        {
            _chosen.Clear();
            _unknown.Clear();
            _all.Clear();
            _storedOrder.Clear();

            var entries = PickerCandidates.Parse(_spec, current ?? "");
            foreach (var e in entries)
            {
                if (e == null || string.IsNullOrEmpty(e.Prefab)) continue;
                if (!_chosen.ContainsKey(e.Prefab)) _storedOrder.Add(e.Prefab);
                _chosen[e.Prefab] = e;
            }

            var candidates = PickerCandidates.For(_spec);
            var known = new HashSet<string>();
            foreach (var c in candidates) if (c != null && c.Prefab != null) known.Add(c.Prefab);

            foreach (var kv in _chosen)
            {
                if (known.Contains(kv.Key)) continue;
                _unknown.Add(kv.Key);
                _all.Add(new Candidate
                {
                    Prefab = kv.Key,
                    Display = kv.Key,
                    Group = "Not found in this world"
                });
            }
            // Group first, then name. The provider sorts by display name alone, which interleaves
            // the groups - and the list draws a heading whenever the group changes, so an
            // interleaved list would repeat "Rock nodes" and "Destructibles" all the way down.
            // The unknown entries stay pinned above all of it, where they were added.
            candidates.Sort(delegate (Candidate a, Candidate b)
            {
                int g = string.Compare(a.Group ?? "", b.Group ?? "", StringComparison.OrdinalIgnoreCase);
                if (g != 0) return g;
                return string.Compare(a.Display ?? a.Prefab ?? "", b.Display ?? b.Prefab ?? "",
                                      StringComparison.OrdinalIgnoreCase);
            });
            _all.AddRange(candidates);
        }

        // ---- the panel -------------------------------------------------------------------------

        private static void Build(RectTransform page)
        {
            if (_root != null && _root.parent == page) return;
            if (_root != null) UnityEngine.Object.Destroy(_root.gameObject);
            _rows.Clear();

            var dim = UiKit.Fill(page, new Color(0f, 0f, 0f, 0.75f));
            dim.raycastTarget = true;                  // nothing behind the panel is clickable
            dim.gameObject.name = "NVLB_Picker";
            _root = (RectTransform)dim.transform;
            UiKit.Stretch(_root);

            var panel = UiKit.Fill(_root, new Color(0.09f, 0.08f, 0.06f, 0.99f));
            var prt = (RectTransform)panel.transform;
            prt.anchorMin = new Vector2(0.5f, 0.5f);
            prt.anchorMax = new Vector2(0.5f, 0.5f);
            prt.pivot = new Vector2(0.5f, 0.5f);
            prt.anchoredPosition = Vector2.zero;
            prt.sizeDelta = new Vector2(760f, 520f);

            _title = UiKit.Label(prt, "", UiKit.BaseFontSize, TextAlignmentOptions.MidlineLeft);
            UiKit.Place((RectTransform)_title.transform, 16f, 10f, 500f, 26f);

            var searchLabel = UiKit.Label(prt, "Search", UiKit.BaseFontSize * 0.85f,
                                          TextAlignmentOptions.MidlineLeft, UiKit.HintColor);
            UiKit.Place((RectTransform)searchLabel.transform, 16f, 44f, 60f, 24f);

            _search = UiKit.Input(prt, 300f, 24f);
            if (_search != null)
            {
                UiKit.Place((RectTransform)_search.transform, 78f, 44f, 300f, 24f);
                if (_search.placeholder != null) ((TMP_Text)_search.placeholder).text = "name or prefab";
                _search.onValueChanged.AddListener(delegate (string s)
                {
                    _filter = (s ?? "").Trim();
                    Refill();
                });
            }

            var all = UiKit.Button(prt, "Tick all shown", UiKit.BaseFontSize * 0.85f);
            if (all != null)
            {
                UiKit.Place((RectTransform)all.transform, 390f, 42f, 150f, 28f);
                all.onClick.AddListener(delegate { TickAllShown(true); });
            }

            var none = UiKit.Button(prt, "Clear all", UiKit.BaseFontSize * 0.85f);
            if (none != null)
            {
                UiKit.Place((RectTransform)none.transform, 548f, 42f, 110f, 28f);
                none.onClick.AddListener(delegate { _chosen.Clear(); Refill(); });
            }

            _count = UiKit.Label(prt, "", UiKit.BaseFontSize * 0.8f,
                                 TextAlignmentOptions.MidlineRight, UiKit.HintColor);
            UiKit.Place((RectTransform)_count.transform, 16f, 76f, 728f, 22f);

            _scroll = UiKit.Scroll(prt, "PickerList", out _listContent);
            var srt = (RectTransform)_scroll.transform;
            srt.anchorMin = new Vector2(0f, 0f);
            srt.anchorMax = new Vector2(1f, 1f);
            srt.pivot = new Vector2(0.5f, 0.5f);
            srt.offsetMin = new Vector2(12f, 52f);
            srt.offsetMax = new Vector2(-12f, -102f);
            _knob = UiKit.ScrollBar(_scroll);

            var ok = UiKit.Button(prt, "OK", UiKit.BaseFontSize * 0.9f);
            if (ok != null)
            {
                UiKit.Place((RectTransform)ok.transform, 540f, 480f, 100f, 32f);
                ok.onClick.AddListener(delegate { Accept(); });
            }

            var cancel = UiKit.Button(prt, "Cancel", UiKit.BaseFontSize * 0.9f);
            if (cancel != null)
            {
                UiKit.Place((RectTransform)cancel.transform, 648f, 480f, 100f, 32f);
                cancel.onClick.AddListener(delegate { Close(); });
            }

            _root.gameObject.SetActive(false);
        }

        // ---- the rows --------------------------------------------------------------------------

        /// <summary>
        /// Rebuild the visible rows for the current search. Only <see cref="MaxRows"/> are ever
        /// built: the item list alone runs past a thousand entries, and a thousand rows of cloned
        /// vanilla controls stalls the menu outright. The count line says when there is more.
        /// </summary>
        private static void Refill()
        {
            if (_listContent == null) return;

            foreach (var go in _rows) if (go != null) UnityEngine.Object.Destroy(go);
            _rows.Clear();

            var shown = new List<Candidate>();
            foreach (var c in _all)
            {
                if (c == null) continue;
                if (!Matches(c)) continue;
                shown.Add(c);
                if (shown.Count >= MaxRows) break;
            }

            float y = 2f;
            string group = null;
            foreach (var c in shown)
            {
                if (c.Group != group)
                {
                    group = c.Group;
                    if (!string.IsNullOrEmpty(group))
                    {
                        var head = UiKit.Label(_listContent, group.ToUpperInvariant(),
                                               UiKit.BaseFontSize * 0.72f,
                                               TextAlignmentOptions.BottomLeft, UiKit.HintColor);
                        UiKit.Place((RectTransform)head.transform, 6f, y, 600f, 22f);
                        _rows.Add(head.gameObject);
                        y += 24f;
                    }
                }
                BuildRow(c, y);
                y += RowH;
            }

            UiKit.FitContent(_listContent, y + 6f);

            int matched = 0;
            foreach (var c in _all) if (c != null && Matches(c)) matched++;
            if (_count != null)
                _count.text = _chosen.Count + " ticked" +
                              (matched > shown.Count
                                   ? "   -   showing " + shown.Count + " of " + matched +
                                     ", narrow the search to see the rest"
                                   : "   -   " + matched + " shown");
        }

        private static bool Matches(Candidate c)
        {
            if (_filter.Length == 0) return true;
            return (c.Display != null && c.Display.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0)
                || (c.Prefab != null && c.Prefab.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static void BuildRow(Candidate c, float y)
        {
            var go = new GameObject("Pick_" + c.Prefab, typeof(RectTransform));
            go.transform.SetParent(_listContent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.offsetMin = new Vector2(0f, -(y + RowH));
            rt.offsetMax = new Vector2(0f, -y);
            _rows.Add(go);

            bool ticked = _chosen.ContainsKey(c.Prefab);

            var toggle = UiKit.Toggle(rt);
            if (toggle != null)
            {
                var trt = (RectTransform)toggle.transform;
                trt.anchorMin = new Vector2(0f, 0.5f);
                trt.anchorMax = new Vector2(0f, 0.5f);
                trt.pivot = new Vector2(0f, 0.5f);
                trt.anchoredPosition = new Vector2(8f, 0f);
                toggle.isOn = ticked;
                var prefab = c.Prefab;
                toggle.onValueChanged.AddListener(delegate (bool on) { Tick(prefab, on); });
            }

            bool missing = _unknown.Contains(c.Prefab);
            var name = UiKit.Label(rt, c.Display ?? c.Prefab, UiKit.BaseFontSize * 0.9f,
                                   TextAlignmentOptions.MidlineLeft,
                                   missing ? new Color(0.95f, 0.65f, 0.45f, 1f) : UiKit.TextColor);
            UiKit.Place((RectTransform)name.transform, 46f, 2f, 280f, 18f);

            var sub = UiKit.Label(rt, missing ? c.Prefab + "  (not found in this world)" : c.Prefab,
                                  UiKit.BaseFontSize * 0.7f, TextAlignmentOptions.MidlineLeft, UiKit.HintColor);
            UiKit.Place((RectTransform)sub.transform, 46f, 18f, 280f, 14f);

            // The numbers this entry carries - a tier, a multiplier, a stack and a price. Only
            // live while the entry is ticked, because a value on something unticked means nothing.
            if (_spec.Fields != null && _spec.Fields.Length > 0)
            {
                float x = 340f;
                for (int i = 0; i < _spec.Fields.Length; i++)
                {
                    var field = _spec.Fields[i];
                    int index = i;
                    var prefab = c.Prefab;

                    var lbl = UiKit.Label(rt, field.Label, UiKit.BaseFontSize * 0.7f,
                                          TextAlignmentOptions.MidlineRight, UiKit.HintColor);
                    UiKit.Place((RectTransform)lbl.transform, x, 8f, 70f, 18f);

                    var box = UiKit.Input(rt, 60f, 22f);
                    if (box != null)
                    {
                        UiKit.Place((RectTransform)box.transform, x + 74f, 6f, 60f, 22f);
                        box.contentType = field.WholeNumbers
                            ? TMP_InputField.ContentType.IntegerNumber
                            : TMP_InputField.ContentType.DecimalNumber;
                        box.SetTextWithoutNotify(FieldText(prefab, index, field));
                        box.interactable = _chosen.ContainsKey(prefab);
                        box.onEndEdit.AddListener(delegate (string s) { SetField(prefab, index, field, s); });
                    }
                    x += 140f;
                }
            }
        }

        private static string FieldText(string prefab, int index, PickerField field)
        {
            Entry e;
            if (_chosen.TryGetValue(prefab, out e) && e.Fields != null && index < e.Fields.Length)
                return Num(e.Fields[index], field);
            return Num(field.Default, field);
        }

        private static string Num(double v, PickerField field)
        {
            return field.WholeNumbers
                ? ((long)Math.Round(v)).ToString(System.Globalization.CultureInfo.InvariantCulture)
                : v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static void Tick(string prefab, bool on)
        {
            if (!on) { _chosen.Remove(prefab); Refill(); return; }
            if (_chosen.ContainsKey(prefab)) return;

            var e = new Entry { Prefab = prefab };
            if (_spec.Fields != null && _spec.Fields.Length > 0)
            {
                e.Fields = new double[_spec.Fields.Length];
                for (int i = 0; i < _spec.Fields.Length; i++) e.Fields[i] = _spec.Fields[i].Default;
            }
            _chosen[prefab] = e;
            Refill();
        }

        private static void SetField(string prefab, int index, PickerField field, string text)
        {
            Entry e;
            if (!_chosen.TryGetValue(prefab, out e)) return;

            double v;
            if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                                 System.Globalization.CultureInfo.InvariantCulture, out v))
                v = field.Default;
            v = Math.Max(field.Min, Math.Min(field.Max, v));
            if (field.WholeNumbers) v = Math.Round(v);

            if (e.Fields == null || e.Fields.Length < _spec.Fields.Length)
            {
                var grown = new double[_spec.Fields.Length];
                for (int i = 0; i < grown.Length; i++)
                    grown[i] = (e.Fields != null && i < e.Fields.Length) ? e.Fields[i] : _spec.Fields[i].Default;
                e.Fields = grown;
            }
            e.Fields[index] = v;
            e.Raw = null;                      // it is ours now, not the text we found
        }

        private static void TickAllShown(bool on)
        {
            foreach (var c in _all)
            {
                if (c == null || !Matches(c)) continue;
                if (on) Tick(c.Prefab, true); else _chosen.Remove(c.Prefab);
            }
            Refill();
        }

        // ---- leaving ---------------------------------------------------------------------------

        private static void Accept()
        {
            try
            {
                // Everything that was already in the value keeps the place it had - order is
                // load-bearing in at least one of these settings, and an unchanged value must
                // come back out byte for byte. Newly ticked entries go on the end, in the order
                // the list showed them.
                var list = new List<Entry>();
                var placed = new HashSet<string>();

                foreach (var prefab in _storedOrder)
                {
                    Entry e;
                    if (!_chosen.TryGetValue(prefab, out e)) continue;   // unticked since
                    list.Add(e);
                    placed.Add(prefab);
                }

                foreach (var c in _all)
                {
                    if (c == null || placed.Contains(c.Prefab)) continue;
                    Entry e;
                    if (_chosen.TryGetValue(c.Prefab, out e)) { list.Add(e); placed.Add(c.Prefab); }
                }

                var value = PickerCandidates.Serialise(_spec, list);
                var ok = _onOk;
                Close();
                if (ok != null) ok(value);
            }
            catch (Exception e)
            {
                NoVikingLeftBehindPlugin.Log.LogError("[SettingsMenu] the picker could not write its value: " + e);
                Close();
            }
        }

        internal static void Close()
        {
            _onOk = null;
            if (_root != null) _root.gameObject.SetActive(false);
        }

        /// <summary>Driven from the tab's Update, like everything else that has to tick.</summary>
        internal static void Tick()
        {
            if (!IsOpen) return;
            UiKit.TickScroll(_scroll, _knob, RowH);
        }

        /// <summary>The page is going away - forget the panel so the next open rebuilds it.</summary>
        internal static void Forget()
        {
            _root = null; _listContent = null; _scroll = null; _knob = null;
            _search = null; _title = null; _count = null; _onOk = null;
            _rows.Clear(); _chosen.Clear(); _all.Clear(); _unknown.Clear();
        }

        /// <summary>A one-line summary for the row's button: what is picked, without opening it.</summary>
        internal static string Summary(PickerSpec spec, string value)
        {
            try
            {
                var entries = PickerCandidates.Parse(spec, value ?? "");
                if (entries.Count == 0) return "none";

                var sb = new System.Text.StringBuilder();
                sb.Append(entries.Count).Append(entries.Count == 1 ? " picked: " : " picked: ");
                for (int i = 0; i < entries.Count && i < 3; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(PickerCandidates.DisplayFor(spec, entries[i].Prefab));
                }
                if (entries.Count > 3) sb.Append(", ...");
                return sb.ToString();
            }
            catch { return value ?? ""; }
        }
    }
}
