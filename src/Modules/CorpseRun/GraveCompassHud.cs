using System;
using TMPro;
using UnityEngine;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// The grave compass widget: an arrow that points at your corpse plus the distance in metres.
    ///
    /// The mod ships no UI assets, so both labels are Instantiate()d clones of an existing vanilla
    /// TMP_Text (Hud.m_gpName by preference, else Hud.m_healthText, else the first TMP_Text under
    /// Hud.m_rootObject) - that inherits Valheim's font, material and outline for free.
    ///
    /// They are parented to Hud.m_rootObject, NOT to the donor's own parent: Hud.m_gpName lives
    /// inside Hud.m_gpRoot, which vanilla SetActive(false)s whenever you have no guardian power
    /// (Hud.UpdateGuardianPower), and a clone under there would be switched off with it. Because
    /// the parent changes, the anchors are set explicitly (centre/centre) instead of being
    /// inherited, so the configured offset means the same thing at every resolution.
    ///
    /// The arrow is a plain character (default "^") in its own label, rotated about Z by the
    /// bearing to the grave relative to the camera. A rotated RectTransform is the only way to get
    /// a smooth 360-degree arrow without shipping a sprite, and "^" is ASCII so it is guaranteed
    /// to exist in whatever font the donor uses. [CorpseRun] CompassArrow can be set to a nicer
    /// glyph if the font has one.
    ///
    /// Every vanilla lookup is guarded. Any failure sets _failed, logs once and permanently skips
    /// the compass - the other three CorpseRunPlus features keep working.
    /// </summary>
    internal static class GraveCompassHud
    {
        public static Vector2 Offset = new Vector2(0f, 200f);
        public static string ArrowChar = "^";
        public static float ArrowScale = 1.6f;

        private static Hud _hud;
        private static GameObject _arrowGo;
        private static GameObject _labelGo;
        private static RectTransform _arrowRt;
        private static RectTransform _labelRt;
        private static TMP_Text _arrow;
        private static TMP_Text _label;
        private static bool _built;
        private static bool _failed;
        private static bool _visible;

        public static bool Failed { get { return _failed; } }
        public static bool Visible { get { return _visible; } }

        /// <summary>Point the compass at <paramref name="target"/>. Called from the module tick.</summary>
        public static void Show(Player me, Vector3 target, float distance)
        {
            if (_failed || me == null) return;
            try
            {
                var hud = Hud.instance;
                if (hud == null) { HideInternal(); return; }
                if (!Ensure(hud)) return;

                Vector3 to = target - me.transform.position;
                to.y = 0f;
                if (to.sqrMagnitude < 0.0001f) to = me.transform.forward;

                Vector3 fwd = CameraForward(me);
                fwd.y = 0f;
                if (fwd.sqrMagnitude < 0.0001f) fwd = me.transform.forward;

                // +bearing = the grave is to the right; UI Z rotation is counter-clockwise, so
                // an up-pointing glyph is turned by -bearing to face it.
                float bearing = Vector3.SignedAngle(fwd, to, Vector3.up);

                if (_arrowRt != null) _arrowRt.localRotation = Quaternion.Euler(0f, 0f, -bearing);
                if (_arrow != null && _arrow.text != ArrowChar) _arrow.text = ArrowChar;
                if (_label != null) _label.text = Mathf.RoundToInt(distance) + " m";

                if (!_visible)
                {
                    if (_arrowGo != null) _arrowGo.SetActive(true);
                    if (_labelGo != null) _labelGo.SetActive(true);
                    _visible = true;
                }
            }
            catch (Exception e)
            {
                Fail("compass update failed: " + e.Message);
            }
        }

        public static void Hide()
        {
            if (_failed) return;
            try { HideInternal(); }
            catch (Exception e) { Fail("compass hide failed: " + e.Message); }
        }

        private static void HideInternal()
        {
            if (!_visible) return;
            if (_arrowGo != null) _arrowGo.SetActive(false);
            if (_labelGo != null) _labelGo.SetActive(false);
            _visible = false;
        }

        private static Vector3 CameraForward(Player me)
        {
            var gc = GameCamera.instance;
            if (gc != null && gc.transform != null) return gc.transform.forward;
            var cam = Camera.main;
            if (cam != null) return cam.transform.forward;
            return me.transform.forward;
        }

        private static bool Ensure(Hud hud)
        {
            if (_built && _arrowGo != null && _labelGo != null && ReferenceEquals(_hud, hud)) return true;

            // The Hud was rebuilt (scene change) - start over.
            if (!ReferenceEquals(_hud, hud)) Destroy();

            TMP_Text donor = PickDonor(hud);
            if (donor == null)
            {
                Fail("no TMP_Text donor found under Hud - no grave compass " +
                     "(the rest of CorpseRunPlus still works).");
                return false;
            }

            var parent = hud.m_rootObject != null ? hud.m_rootObject.transform as RectTransform : null;
            if (parent == null) parent = donor.transform.parent as RectTransform;
            if (parent == null)
            {
                Fail("Hud.m_rootObject has no RectTransform and the donor has no parent - no grave compass.");
                return false;
            }

            _arrowGo = Build(donor, parent, "NVLB_GraveCompassArrow", out _arrowRt, out _arrow);
            _labelGo = Build(donor, parent, "NVLB_GraveCompassLabel", out _labelRt, out _label);
            if (_arrow == null || _label == null)
            {
                Fail("cloned compass label carries no TMP_Text - no grave compass.");
                Destroy();
                return false;
            }

            _arrow.text = ArrowChar;
            _arrow.fontSize = donor.fontSize * ArrowScale;
            _label.text = "";

            Reposition();
            _arrowGo.SetActive(false);
            _labelGo.SetActive(false);
            _visible = false;
            _hud = hud;
            _built = true;

            NoVikingLeftBehindPlugin.Log.LogInfo("[CorpseRun] grave compass built from donor '" +
                donor.name + "' under '" + parent.name + "' at offset " + Offset);
            return true;
        }

        private static TMP_Text PickDonor(Hud hud)
        {
            if (hud.m_gpName != null) return hud.m_gpName;
            if (hud.m_healthText != null) return hud.m_healthText;
            if (hud.m_rootObject != null)
            {
                var all = hud.m_rootObject.GetComponentsInChildren<TMP_Text>(true);
                if (all != null && all.Length > 0) return all[0];
            }
            return null;
        }

        private static GameObject Build(TMP_Text donor, RectTransform parent, string name,
                                        out RectTransform rt, out TMP_Text text)
        {
            var go = UnityEngine.Object.Instantiate(donor.gameObject, parent);
            go.name = name;
            rt = go.GetComponent<RectTransform>();
            text = go.GetComponent<TMP_Text>();
            if (rt != null)
            {
                rt.anchorMin = new Vector2(0.5f, 0.5f);
                rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.localScale = Vector3.one;
                rt.localRotation = Quaternion.identity;
                rt.sizeDelta = new Vector2(220f, 40f);
            }
            if (text != null)
            {
                text.alignment = TextAlignmentOptions.Center;
                text.enableWordWrapping = false;
                text.raycastTarget = false;
                text.color = Color.white;
            }
            return go;
        }

        private static void Reposition()
        {
            if (_arrowRt != null) _arrowRt.anchoredPosition = Offset;
            if (_labelRt != null) _labelRt.anchoredPosition = Offset + new Vector2(0f, -34f);
        }

        public static void SetOffset(Vector2 offset, string arrowChar, float arrowScale)
        {
            Offset = offset;
            ArrowChar = string.IsNullOrEmpty(arrowChar) ? "^" : arrowChar;
            ArrowScale = arrowScale <= 0f ? 1.6f : arrowScale;
            try
            {
                Reposition();
                if (_arrow != null) { _arrow.text = ArrowChar; }
            }
            catch (Exception e)
            {
                NoVikingLeftBehindPlugin.Log.LogWarning("[CorpseRun] compass reposition: " + e.Message);
            }
        }

        private static void Fail(string message)
        {
            _failed = true;
            NoVikingLeftBehindPlugin.Log.LogWarning("[CorpseRun] " + message);
            Destroy();
        }

        public static void Destroy()
        {
            try
            {
                if (_arrowGo != null) UnityEngine.Object.Destroy(_arrowGo);
                if (_labelGo != null) UnityEngine.Object.Destroy(_labelGo);
            }
            catch (Exception e)
            {
                NoVikingLeftBehindPlugin.Log.LogWarning("[CorpseRun] compass teardown: " + e.Message);
            }
            _arrowGo = null; _labelGo = null; _arrowRt = null; _labelRt = null;
            _arrow = null; _label = null; _hud = null; _built = false; _visible = false;
        }

        /// <summary>Allow a retry after a transient failure (config toggle).</summary>
        public static void Reset()
        {
            Destroy();
            _failed = false;
        }
    }
}
