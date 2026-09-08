using System;
using UnityEngine;
using UnityEngine.UI;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// **PowerRing** - the Forsaken-power HUD decoration: round icons and a cooldown/charge ring.
    ///
    /// Why it exists
    /// -------------
    /// CombatRecharge already makes fighting shorten a power cooldown, but vanilla shows that as a
    /// square icon plus a shrinking number, so nobody sees the "ultimate charging" feeling. This
    /// draws a thin arc around each power icon that fills as the cooldown recovers, so a hit landed
    /// visibly jumps the ring forward.
    ///
    /// No shipped assets
    /// -----------------
    /// The mod ships no UI assets and must stay that way, so both sprites are generated at runtime
    /// into a 256x256 RGBA32 <see cref="Texture2D"/> (a solid antialiased disc for the mask, an
    /// annulus for the ring) and cached for the life of the process. The pixel maths lives in the
    /// pure static <see cref="Disc"/> / <see cref="Annulus"/> helpers so a headless dedicated
    /// server can prove them with no graphics device (see <see cref="LogSelfTest"/>).
    ///
    /// What it does to the vanilla hierarchy
    /// -------------------------------------
    /// Vanilla's widget is Hud.m_gpRoot holding Hud.m_gpIcon / m_gpName / m_gpCooldown, and
    /// Hud.UpdateGuardianPower(Player) (Hud.cs:1470) only ever writes m_gpIcon.sprite and
    /// m_gpIcon.color - it never looks at the icon's parent. So we can slip a container in:
    ///
    ///     (icon's original parent)
    ///       +- NVLB_GPDeco{slot}          RectTransform, copies the icon's own rect exactly
    ///            +- NVLB_GPMask           Image(disc) + Mask(showMaskGraphic:false)
    ///            |    +- m_gpIcon         the VANILLA Image, reparented, stretched to fill
    ///            +- NVLB_GPTrack          Image(annulus), the dark un-filled remainder
    ///            +- NVLB_GPFill           Image(annulus), type=Filled / Radial360, drawn on top
    ///
    /// The vanilla icon object, its sprite and its colour are never replaced - it is only moved,
    /// and <see cref="Undecorate"/> puts it back (parent, sibling index, anchors, pivot, size)
    /// exactly where it was. The same decoration is applied to DualPowers' cloned slot-2/3 widget
    /// (PowerHud.CloneIcon), so every slot looks identical.
    ///
    /// Ordering with PowerHud: PowerHud clones the whole m_gpRoot subtree and maps its leaves by
    /// component INDEX, so it must clone a PRISTINE tree. PowerHud.Ensure therefore calls
    /// <see cref="UndecorateAll"/> on its rebuild path; we re-decorate on the next Refresh, which
    /// is the same frame (DualPowersModule.HudPostfix calls PowerHud.Refresh then PowerRing.Refresh).
    /// A live HudOffsetX/HudOffsetY edit is a DIFFERENT path - PowerHud.SetOffset repositions the
    /// existing clone in place and never touches UndecorateAll, so it calls <see cref="Redecorate"/>
    /// directly instead of relying on the next frame's Refresh to notice.
    ///
    /// Everything is wrapped: one throw anywhere in here logs once, tears the decoration down and
    /// permanently disables it, because this code runs inside Hud.UpdateGuardianPower every frame.
    /// </summary>
    internal static class PowerRing
    {
        // ---- config, pushed in by DualPowersModule.Push() ---------------------------------

        public static bool RoundIcons = true;
        public static bool CooldownRing = true;
        public static float Thickness = 4f;
        public static Color RingColor = new Color(0.902f, 0.784f, 0.541f, 0.851f);   // E6C88AD9
        public static Color TrackColor = new Color(0f, 0f, 0f, 0.4f);                // 00000066

        /// <summary>Texture resolution of the generated sprites. One allocation per shape.</summary>
        private const int TexSize = 256;

        /// <summary>Length of the one-shot brighten when a power comes off cooldown.</summary>
        private const float FlashSeconds = 0.6f;

        // ---- state ------------------------------------------------------------------------

        private sealed class Deco
        {
            public Image Icon;                 // the vanilla (or cloned) icon we moved
            public GameObject Root;            // NVLB_GPDeco{slot}
            public RectTransform MaskRt;
            public Image Track;
            public Image Fill;

            // everything needed to put the icon back exactly as it was
            public Transform OrigParent;
            public int OrigIndex;
            public Vector2 OrigAnchorMin, OrigAnchorMax, OrigOffsetMin, OrigOffsetMax;
            public Vector2 OrigPivot, OrigAnchoredPos, OrigSizeDelta;
            public Vector3 OrigScale;

            public float IconSize;
            public StatusEffect LastSe;
            public float Total;                // denominator for the ring, seconds
            public bool WasReady;
            public float FlashUntil;
        }

        private static readonly Deco[] Decos = new Deco[PowerSlots.MaxSlots];

        private static Sprite _discSprite;
        private static Sprite _ringSprite;
        private static float _ringSpriteFraction = -1f;   // band width as a fraction of the diameter

        private static bool _failed;
        private static string _loggedShape;
        private static int _sizeWarnings;

        // ---- config plumbing ---------------------------------------------------------------

        /// <summary>
        /// Push new settings. Anything that changes the geometry or the colours tears the
        /// decoration down; the next Refresh rebuilds it, so config edits are live.
        /// </summary>
        public static void Configure(bool round, bool ring, float thickness, Color ringColor, Color trackColor)
        {
            thickness = Mathf.Clamp(thickness, 1f, 24f);
            bool geometry = round != RoundIcons || ring != CooldownRing ||
                            Mathf.Abs(thickness - Thickness) > 0.001f;

            RoundIcons = round;
            CooldownRing = ring;
            Thickness = thickness;
            RingColor = ringColor;
            TrackColor = trackColor;

            if (geometry)
            {
                UndecorateAll();
                _loggedShape = null;
            }
            else
            {
                // colours only: repaint in place, no rebuild.
                for (int i = 0; i < Decos.Length; i++)
                {
                    var d = Decos[i];
                    if (d == null) continue;
                    if (d.Track != null) d.Track.color = TrackColor;
                    if (d.Fill != null) d.Fill.color = RingColor;
                }
            }
        }

        /// <summary>Parse an RGBA hex string ("E6C88AD9", "#E6C88AD9", "E6C88A").</summary>
        public static Color ParseColor(string hex, Color fallback, string what)
        {
            if (string.IsNullOrEmpty(hex)) return fallback;
            string s = hex.Trim();
            if (s.Length > 0 && s[0] != '#') s = "#" + s;
            Color c;
            if (ColorUtility.TryParseHtmlString(s, out c)) return c;
            NoVikingLeftBehindPlugin.Log.LogWarning("[Powers] " + what + " = '" + hex +
                "' is not an RGB/RGBA hex colour, using the default.");
            return fallback;
        }

        /// <summary>Allow a retry after a transient failure (config toggle).</summary>
        public static void Reset()
        {
            UndecorateAll();
            _failed = false;
            _loggedShape = null;
            _sizeWarnings = 0;
        }

        // ---- per-frame entry point ----------------------------------------------------------

        /// <summary>
        /// Called from DualPowersModule's Hud.UpdateGuardianPower postfix, AFTER PowerHud.Refresh
        /// so the clone (if any) already exists. Never throws.
        /// </summary>
        public static void Refresh(Hud hud, Player player)
        {
            if (_failed || hud == null || player == null) return;
            try
            {
                if (!RoundIcons && !CooldownRing) { UndecorateAll(); return; }

                Apply(0, hud.m_gpIcon, player);

                var cloneIcon = PowerHud.CloneIcon;
                if (cloneIcon != null) Apply(1, cloneIcon, player);
                else Undecorate(1);

                LogShapeOnce();
            }
            catch (Exception e)
            {
                _failed = true;
                NoVikingLeftBehindPlugin.Log.LogWarning("[Powers] HUD ring disabled after an error: " + e.Message);
                try { UndecorateAll(); } catch { /* nothing left to do */ }
            }
        }

        private static void Apply(int slot, Image icon, Player player)
        {
            if (icon == null) { Undecorate(slot); return; }

            var d = Decos[slot];
            if (d == null || d.Root == null || d.Icon != icon)
            {
                Undecorate(slot);
                d = Build(slot, icon);
                if (d == null) return;              // layout not ready yet - try again next frame
                Decos[slot] = d;
                _loggedShape = null;
            }

            if (!CooldownRing)
            {
                if (d.Track != null && d.Track.enabled) d.Track.enabled = false;
                if (d.Fill != null && d.Fill.enabled) d.Fill.enabled = false;
                return;
            }
            if (d.Track != null && !d.Track.enabled) d.Track.enabled = true;
            if (d.Fill != null && !d.Fill.enabled) d.Fill.enabled = true;

            var se = PowerSlots.GetSe(player, slot);
            float cd = Mathf.Max(0f, PowerSlots.GetCooldown(player, slot));

            if (!ReferenceEquals(se, d.LastSe))
            {
                d.LastSe = se;
                d.Total = TotalFor(se);
                d.WasReady = cd <= 0f;
                d.FlashUntil = 0f;
            }
            // A shared-cooldown or CooldownMultiplier setup can hand us more than the status
            // effect's own m_cooldown; the ring must never overflow, so the denominator grows.
            if (cd > d.Total) d.Total = cd;

            float amount = (d.Total > 0f) ? 1f - (cd / d.Total) : 1f;
            amount = Mathf.Clamp01(amount);

            bool ready = cd <= 0f;
            if (ready && !d.WasReady) d.FlashUntil = Time.unscaledTime + FlashSeconds;
            d.WasReady = ready;

            if (d.Fill != null)
            {
                d.Fill.fillAmount = ready ? 1f : amount;
                d.Fill.color = FillColour(ready, d.FlashUntil);
            }
        }

        /// <summary>Ring colour: the configured colour, a touch brighter when ready, plus a one-shot flash.</summary>
        private static Color FillColour(bool ready, float flashUntil)
        {
            Color c = RingColor;
            if (ready) c = Color.Lerp(c, Color.white, 0.18f);

            float left = flashUntil - Time.unscaledTime;
            if (left > 0f && left <= FlashSeconds)
            {
                // 0 -> 1 -> 0 over FlashSeconds. One soft pulse, only at the ready moment.
                float k = Mathf.Sin((left / FlashSeconds) * Mathf.PI) * 0.55f;
                c = Color.Lerp(c, Color.white, k);
            }
            c.a = RingColor.a;
            return c;
        }

        /// <summary>Total cooldown the ring divides by: the power's own StatusEffect.m_cooldown.</summary>
        private static float TotalFor(StatusEffect se)
        {
            if (se == null) return 0f;
            float mult = PowerSlots.CooldownMultiplier;
            if (mult <= 0f || float.IsNaN(mult) || float.IsInfinity(mult)) mult = 1f;
            return Mathf.Max(0f, se.m_cooldown * mult);
        }

        /// <summary>
        /// Structural re-decorate for one slot, with no Player/cooldown data required - unlike
        /// <see cref="Apply"/>, this can run outside the per-frame Hud.UpdateGuardianPower postfix.
        /// Called from PowerHud.SetOffset, the one code path a live HudOffsetX/HudOffsetY edit
        /// takes: that path repositions the clone in place and never goes through
        /// PowerHud.Ensure()'s "UndecorateAll then rebuild" contract, so nothing else guarantees the
        /// mask/ring survive an offset change. Idempotent: a slot whose icon is already decorated
        /// (same Image reference, mask/ring objects still alive) is left untouched, so a widget can
        /// never end up with two rings. A slot whose layout is not ready yet (rect still 0x0, e.g.
        /// the clone was just built and immediately hidden) is silently skipped - the ordinary
        /// per-frame Refresh keeps retrying it.
        /// </summary>
        internal static void Redecorate(int slot, Image icon)
        {
            if (_failed || icon == null) return;
            try
            {
                var d = Decos[slot];
                if (d != null && d.Root != null && d.Icon == icon) return;   // already correct

                Undecorate(slot);
                var built = Build(slot, icon);
                if (built == null) return;               // layout not ready yet - Refresh() retries
                Decos[slot] = built;

                int n = 0;
                for (int i = 0; i < Decos.Length; i++) if (Decos[i] != null) n++;
                NoVikingLeftBehindPlugin.Log.LogInfo("[Powers] HUD rebuilt (offset " +
                    PowerHud.Offset.x.ToString("0") + "," + PowerHud.Offset.y.ToString("0") +
                    ") -> re-decorated slots=" + n + " icon=" + built.IconSize.ToString("0") + "px");
            }
            catch (Exception e)
            {
                _failed = true;
                NoVikingLeftBehindPlugin.Log.LogWarning("[Powers] HUD ring disabled after an error: " + e.Message);
                try { UndecorateAll(); } catch { /* nothing left to do */ }
            }
        }

        // ---- build / tear down ---------------------------------------------------------------

        private static Deco Build(int slot, Image icon)
        {
            var rt = icon.rectTransform;
            if (rt == null || rt.parent == null) return null;

            Rect r = rt.rect;
            float size = Mathf.Min(Mathf.Abs(r.width), Mathf.Abs(r.height));
            if (size < 4f)
            {
                // The canvas has not laid the widget out yet (or an odd non-square rect). Retry.
                if (_sizeWarnings++ == 0)
                    NoVikingLeftBehindPlugin.Log.LogInfo("[Powers] HUD ring: icon rect is " +
                        r.width.ToString("0.#") + "x" + r.height.ToString("0.#") + " - waiting for layout.");
                return null;
            }

            var d = new Deco
            {
                Icon = icon,
                IconSize = size,
                OrigParent = rt.parent,
                OrigIndex = rt.GetSiblingIndex(),
                OrigAnchorMin = rt.anchorMin,
                OrigAnchorMax = rt.anchorMax,
                OrigOffsetMin = rt.offsetMin,
                OrigOffsetMax = rt.offsetMax,
                OrigPivot = rt.pivot,
                OrigAnchoredPos = rt.anchoredPosition,
                OrigSizeDelta = rt.sizeDelta,
                OrigScale = rt.localScale
            };

            // --- container, occupying exactly the icon's own rect ------------------------------
            var rootGo = new GameObject("NVLB_GPDeco" + slot, typeof(RectTransform));
            var rootRt = rootGo.GetComponent<RectTransform>();
            rootRt.SetParent(d.OrigParent, false);
            rootRt.SetSiblingIndex(d.OrigIndex);
            rootRt.anchorMin = d.OrigAnchorMin;
            rootRt.anchorMax = d.OrigAnchorMax;
            rootRt.pivot = d.OrigPivot;
            rootRt.anchoredPosition = d.OrigAnchoredPos;
            rootRt.sizeDelta = d.OrigSizeDelta;
            rootRt.offsetMin = d.OrigOffsetMin;
            rootRt.offsetMax = d.OrigOffsetMax;
            rootRt.localScale = d.OrigScale;
            d.Root = rootGo;

            // --- circle mask, with the vanilla icon moved inside it ---------------------------
            var maskGo = new GameObject("NVLB_GPMask", typeof(RectTransform), typeof(Image));
            var maskRt = maskGo.GetComponent<RectTransform>();
            maskRt.SetParent(rootRt, false);
            Stretch(maskRt);
            var maskImg = maskGo.GetComponent<Image>();
            maskImg.raycastTarget = false;
            maskImg.sprite = DiscSprite();
            maskImg.color = Color.white;
            if (RoundIcons)
            {
                var mask = maskGo.AddComponent<Mask>();
                mask.showMaskGraphic = false;
            }
            else
            {
                maskImg.enabled = false;          // plain container, square icon as vanilla
            }
            d.MaskRt = maskRt;

            rt.localScale = Vector3.one;
            rt.SetParent(maskRt, false);
            Stretch(rt);

            // --- the ring itself, centred on the icon's edge ----------------------------------
            float ringD = size + Thickness;                       // band straddles the circle edge
            float fraction = Mathf.Clamp(Thickness / ringD, 0.01f, 0.49f);
            var ringSprite = RingSprite(fraction);

            d.Track = MakeRingImage(rootRt, "NVLB_GPTrack", ringSprite, ringD, TrackColor, false);
            d.Fill = MakeRingImage(rootRt, "NVLB_GPFill", ringSprite, ringD, RingColor, true);
            if (!CooldownRing)
            {
                if (d.Track != null) d.Track.enabled = false;
                if (d.Fill != null) d.Fill.enabled = false;
            }
            return d;
        }

        private static Image MakeRingImage(RectTransform parent, string name, Sprite sprite,
                                           float diameter, Color colour, bool filled)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(diameter, diameter);
            var img = go.GetComponent<Image>();
            img.raycastTarget = false;
            img.sprite = sprite;
            img.color = colour;
            if (filled)
            {
                img.type = Image.Type.Filled;
                img.fillMethod = Image.FillMethod.Radial360;
                img.fillOrigin = (int)Image.Origin360.Top;
                img.fillClockwise = true;
                img.fillAmount = 1f;
            }
            return img;
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        /// <summary>Put one slot's icon back exactly where vanilla had it and drop our objects.</summary>
        public static void Undecorate(int slot)
        {
            if (slot < 0 || slot >= Decos.Length) return;
            var d = Decos[slot];
            Decos[slot] = null;
            if (d == null) return;

            try
            {
                if (d.Icon != null && d.OrigParent != null)
                {
                    var rt = d.Icon.rectTransform;
                    rt.SetParent(d.OrigParent, false);
                    rt.SetSiblingIndex(Mathf.Clamp(d.OrigIndex, 0, d.OrigParent.childCount - 1));
                    rt.anchorMin = d.OrigAnchorMin;
                    rt.anchorMax = d.OrigAnchorMax;
                    rt.pivot = d.OrigPivot;
                    rt.anchoredPosition = d.OrigAnchoredPos;
                    rt.sizeDelta = d.OrigSizeDelta;
                    rt.offsetMin = d.OrigOffsetMin;
                    rt.offsetMax = d.OrigOffsetMax;
                    rt.localScale = d.OrigScale;
                }
                if (d.Root != null)
                {
                    // Detach BEFORE destroying. Object.Destroy is deferred to the end of the
                    // frame, so a decoration torn down here would still be sitting in the tree
                    // when PowerHud.Ensure clones m_gpRoot on the very next line - and Ensure maps
                    // the clone's leaves by component INDEX on the promise that the tree is
                    // pristine. Unparenting makes that promise true immediately; the destroy then
                    // happens whenever Unity gets round to it, off the tree, harming nothing.
                    d.Root.transform.SetParent(null, false);
                    UnityEngine.Object.Destroy(d.Root);
                }
            }
            catch (Exception e)
            {
                NoVikingLeftBehindPlugin.Log.LogWarning("[Powers] HUD ring teardown (slot " +
                                                        (slot + 1) + "): " + e.Message);
            }
        }

        public static void UndecorateAll()
        {
            for (int i = 0; i < Decos.Length; i++) Undecorate(i);
            _loggedShape = null;
        }

        // ---- the proof line -------------------------------------------------------------------

        /// <summary>
        /// One guarded line on the client path saying what was actually built - the only proof a
        /// dedicated server can never give us, because this whole module half is client-side.
        /// </summary>
        private static void LogShapeOnce()
        {
            int n = 0;
            float icon = 0f;
            for (int i = 0; i < Decos.Length; i++)
                if (Decos[i] != null) { n++; if (icon <= 0f) icon = Decos[i].IconSize; }
            if (n == 0) return;

            string shape = "slots=" + n + " icon=" + icon.ToString("0") + "px ring=" +
                           Thickness.ToString("0.#") + "px round=" + RoundIcons +
                           " ring=" + CooldownRing + " colour=#" + ColorUtility.ToHtmlStringRGBA(RingColor);
            if (shape == _loggedShape) return;
            _loggedShape = shape;
            NoVikingLeftBehindPlugin.Log.LogInfo("[Powers] HUD ring: decorated " + shape);
        }

        // ---- sprite generation ------------------------------------------------------------------

        /// <summary>Coverage of a filled disc at <paramref name="dist"/> from the centre.</summary>
        public static float Disc(float dist, float radius, float aa)
        {
            if (aa <= 0f) return dist <= radius ? 1f : 0f;
            return Mathf.Clamp01((radius - dist) / aa + 0.5f);
        }

        /// <summary>Coverage of an annulus band between <paramref name="inner"/> and <paramref name="outer"/>.</summary>
        public static float Annulus(float dist, float outer, float inner, float aa)
        {
            if (aa <= 0f) return (dist <= outer && dist >= inner) ? 1f : 0f;
            float a = Mathf.Clamp01((outer - dist) / aa + 0.5f);
            float b = Mathf.Clamp01((dist - inner) / aa + 0.5f);
            return Mathf.Min(a, b);
        }

        private static Sprite DiscSprite()
        {
            if (_discSprite != null) return _discSprite;
            _discSprite = Make(delegate (float dist, float half)
            {
                return Disc(dist, half - 1f, 1.2f);
            });
            return _discSprite;
        }

        private static Sprite RingSprite(float bandFraction)
        {
            if (_ringSprite != null && Mathf.Abs(bandFraction - _ringSpriteFraction) < 0.0005f)
                return _ringSprite;

            _ringSpriteFraction = bandFraction;
            float band = Mathf.Max(1.2f, bandFraction * TexSize);
            _ringSprite = Make(delegate (float dist, float half)
            {
                float outer = half - 0.6f;
                float inner = Mathf.Max(0f, outer - band);
                return Annulus(dist, outer, inner, 1.2f);
            });
            return _ringSprite;
        }

        private delegate float Coverage(float dist, float half);

        private static Sprite Make(Coverage f)
        {
            var tex = new Texture2D(TexSize, TexSize, TextureFormat.RGBA32, false);
            tex.hideFlags = HideFlags.HideAndDontSave;
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;

            var px = new Color32[TexSize * TexSize];
            float half = TexSize * 0.5f;
            for (int y = 0; y < TexSize; y++)
            {
                float dy = (y + 0.5f) - half;
                for (int x = 0; x < TexSize; x++)
                {
                    float dx = (x + 0.5f) - half;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    byte a = (byte)Mathf.RoundToInt(Mathf.Clamp01(f(dist, half)) * 255f);
                    px[y * TexSize + x] = new Color32(255, 255, 255, a);
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, false);

            var sprite = Sprite.Create(tex, new Rect(0f, 0f, TexSize, TexSize),
                                       new Vector2(0.5f, 0.5f), 100f, 0u, SpriteMeshType.FullRect);
            sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
        }

        // ---- self test (headless, 0 players) -------------------------------------------------------

        /// <summary>
        /// Proves the sprite maths with no graphics device: the pure coverage functions are checked
        /// at the points that matter, then the real Texture2D/Sprite build is attempted (guarded -
        /// a headless server may have no graphics device, which is not a failure of the maths).
        /// </summary>
        public static void LogSelfTest()
        {
            var log = NoVikingLeftBehindPlugin.Log;
            float half = TexSize * 0.5f;

            float cIn = Disc(0f, half - 1f, 1.2f);
            float cEdge = Disc(half - 1f, half - 1f, 1.2f);
            float cOut = Disc(half, half - 1f, 1.2f);
            log.LogInfo("[Powers] SelfTest: disc coverage centre=" + cIn.ToString("0.00") +
                        " edge=" + cEdge.ToString("0.00") + " outside=" + cOut.ToString("0.00") +
                        ((cIn > 0.99f && cEdge > 0.4f && cEdge < 0.6f && cOut < 0.2f) ? "  OK" : "  *** FAIL ***"));

            // A 64 px icon with a 4 px ring -> band fraction 4/68.
            float fraction = 4f / 68f;
            float band = Mathf.Max(1.2f, fraction * TexSize);
            float outer = half - 0.6f;
            float inner = outer - band;
            float aMid = Annulus((outer + inner) * 0.5f, outer, inner, 1.2f);
            float aHole = Annulus(inner - 3f, outer, inner, 1.2f);
            float aPast = Annulus(outer + 3f, outer, inner, 1.2f);
            log.LogInfo("[Powers] SelfTest: ring band=" + band.ToString("0.0") + "px of " + TexSize +
                        " (icon 64px + 4px ring) mid=" + aMid.ToString("0.00") +
                        " hole=" + aHole.ToString("0.00") + " outside=" + aPast.ToString("0.00") +
                        ((aMid > 0.99f && aHole < 0.05f && aPast < 0.05f) ? "  OK" : "  *** FAIL ***"));

            try
            {
                var disc = DiscSprite();
                var ring = RingSprite(fraction);
                bool ok = disc != null && ring != null &&
                          disc.texture != null && disc.texture.width == TexSize &&
                          ring.texture != null && ring.texture.width == TexSize;
                log.LogInfo("[Powers] SelfTest: generated sprites disc=" +
                            (disc != null ? disc.texture.width + "x" + disc.texture.height : "null") +
                            " ring=" + (ring != null ? ring.texture.width + "x" + ring.texture.height : "null") +
                            " format=" + TextureFormat.RGBA32 + (ok ? "  OK" : "  *** FAIL ***"));
            }
            catch (Exception e)
            {
                log.LogInfo("[Powers] SelfTest: sprite generation unavailable headless (" +
                            e.GetType().Name + ": " + e.Message + ") - the coverage maths above is the proof.");
            }

            log.LogInfo("[Powers] SelfTest: ring config round=" + RoundIcons + " ring=" + CooldownRing +
                        " thickness=" + Thickness + "px colour=#" + ColorUtility.ToHtmlStringRGBA(RingColor) +
                        " track=#" + ColorUtility.ToHtmlStringRGBA(TrackColor));
        }
    }
}
