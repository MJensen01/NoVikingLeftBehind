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
    /// Since 0.4.5 the marker is an OFF-SCREEN WAYPOINT (CompassMode = Edge, the default): the
    /// grave is projected to screen space, and while it is on screen the marker sits on it, while
    /// off screen (or behind you) the marker slides out to the screen edge along the direction to
    /// it. Dead centre therefore means "you are walking straight at it", which is the whole point.
    /// CompassMode = Fixed restores the pre-0.4.5 static arrow at CompassOffsetX/Y - which is also
    /// the fallback whenever the projection cannot be done (no Camera.main, no parent rect).
    /// A third, smaller label under the compass carries the hold-to-dismiss prompt and progress.
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

        /// <summary>Edge = off-screen waypoint marker (0.4.5 default). Fixed = the old static spot.</summary>
        public static bool EdgeMode = true;

        /// <summary>Pixels of inset kept between the marker and the edge of the screen.</summary>
        public static float EdgeMargin = 60f;

        private static Hud _hud;
        private static GameObject _arrowGo;
        private static GameObject _labelGo;
        private static GameObject _hintGo;
        private static RectTransform _arrowRt;
        private static RectTransform _labelRt;
        private static RectTransform _hintRt;
        private static TMP_Text _arrow;
        private static TMP_Text _label;
        private static TMP_Text _hint;
        private static bool _built;
        private static bool _failed;
        private static bool _visible;
        private static string _hintText = "";

        public static bool Failed { get { return _failed; } }
        public static bool Visible { get { return _visible; } }

        /// <summary>Small line under the compass ("Hold Delete to dismiss grave"). "" hides it.</summary>
        public static void SetHint(string text)
        {
            _hintText = text ?? "";
        }

        /// <summary>
        /// Point the compass at <paramref name="target"/>. Called from the module tick, every frame
        /// in Edge mode so the marker tracks the camera smoothly.
        /// </summary>
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

                Vector2 pos;
                float rotZ;
                if (!EdgeMode || !ScreenMarker(target, bearing, out pos, out rotZ))
                {
                    pos = Offset;
                    rotZ = -bearing;
                }

                if (_arrowRt != null)
                {
                    _arrowRt.anchoredPosition = pos;
                    _arrowRt.localRotation = Quaternion.Euler(0f, 0f, rotZ);
                }
                if (_labelRt != null) _labelRt.anchoredPosition = pos + new Vector2(0f, -34f);
                if (_hintRt != null) _hintRt.anchoredPosition = pos + new Vector2(0f, -60f);

                if (_arrow != null && _arrow.text != ArrowChar) _arrow.text = ArrowChar;
                if (_label != null) _label.text = Mathf.RoundToInt(distance) + " m";
                if (_hint != null && _hint.text != _hintText) _hint.text = _hintText;
                if (_hintGo != null && _hintGo.activeSelf != (_hintText.Length > 0))
                    _hintGo.SetActive(_hintText.Length > 0);

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

        /// <summary>
        /// The off-screen-waypoint maths.
        ///
        /// Camera.main.WorldToScreenPoint gives the grave's pixel position; z &lt; 0 means it is
        /// BEHIND the camera, where the projection mirrors through the centre, so the point is
        /// flipped about the screen centre before it is used (the standard fix - without it a grave
        /// directly behind you points the wrong way). The pixel position is then expressed as a
        /// fraction of the screen and multiplied by the parent RectTransform's own size, so the
        /// result is correct at any resolution and under any CanvasScaler without reading one.
        ///
        /// On screen: the marker sits on the grave, clamped to the margin. Off screen (or behind):
        /// it is pushed out along the direction from the screen centre until it touches whichever
        /// inset edge it reaches first, so a grave to your left floats on the left edge; the arrow
        /// is rotated to point that way. Dead centre only happens when you are walking at it.
        /// </summary>
        private static bool ScreenMarker(Vector3 target, float bearing, out Vector2 pos, out float rotZ)
        {
            pos = Vector2.zero;
            rotZ = -bearing;

            var cam = Camera.main;
            if (cam == null || _arrowRt == null) return false;
            var parent = _arrowRt.parent as RectTransform;
            if (parent == null) return false;

            float sw = Screen.width, sh = Screen.height;
            if (sw < 1f || sh < 1f) return false;

            Vector3 sp = cam.WorldToScreenPoint(target);
            bool behind = sp.z <= 0f;
            if (behind) { sp.x = sw - sp.x; sp.y = sh - sp.y; }

            // Screen pixels -> this RectTransform's units, centre-relative.
            float halfW = parent.rect.width * 0.5f;
            float halfH = parent.rect.height * 0.5f;
            float ux = parent.rect.width / sw;
            float uy = parent.rect.height / sh;
            Vector2 p = new Vector2((sp.x - sw * 0.5f) * ux, (sp.y - sh * 0.5f) * uy);

            float mx = Mathf.Max(0f, halfW - EdgeMargin * ux);
            float my = Mathf.Max(0f, halfH - EdgeMargin * uy);

            bool onScreen = !behind && Mathf.Abs(p.x) <= mx && Mathf.Abs(p.y) <= my;
            if (onScreen)
            {
                pos = p;
                rotZ = -bearing;               // it is sitting on the grave; keep the world bearing
                return true;
            }

            // Push out along the direction from the centre until it hits the inset rectangle.
            Vector2 dir = p;
            if (dir.sqrMagnitude < 0.0001f) dir = new Vector2(0f, 1f);
            dir.Normalize();

            float tx = Mathf.Abs(dir.x) > 0.0001f ? mx / Mathf.Abs(dir.x) : float.MaxValue;
            float ty = Mathf.Abs(dir.y) > 0.0001f ? my / Mathf.Abs(dir.y) : float.MaxValue;
            pos = dir * Mathf.Min(tx, ty);

            // The glyph points up at 0 degrees, so subtract 90 from the direction's own angle.
            rotZ = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg - 90f;
            return true;
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
            if (_hintGo != null) _hintGo.SetActive(false);
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
            _hintGo = Build(donor, parent, "NVLB_GraveCompassHint", out _hintRt, out _hint);
            if (_hint != null) _hint.fontSize = donor.fontSize * 0.75f;
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
            if (_hintGo != null) _hintGo.SetActive(false);
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
            if (_hintRt != null) _hintRt.anchoredPosition = Offset + new Vector2(0f, -60f);
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
                if (_hintGo != null) UnityEngine.Object.Destroy(_hintGo);
            }
            catch (Exception e)
            {
                NoVikingLeftBehindPlugin.Log.LogWarning("[CorpseRun] compass teardown: " + e.Message);
            }
            _arrowGo = null; _labelGo = null; _hintGo = null;
            _arrowRt = null; _labelRt = null; _hintRt = null;
            _arrow = null; _label = null; _hint = null;
            _hud = null; _built = false; _visible = false;
        }

        /// <summary>Allow a retry after a transient failure (config toggle).</summary>
        public static void Reset()
        {
            Destroy();
            _failed = false;
        }
    }
}
