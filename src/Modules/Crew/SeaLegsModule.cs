using System;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// With a crew aboard, your longship points closer into the wind - you can tack where a lone
    /// sailor must row.
    ///
    /// CONE NARROWING ONLY. Top speed, downwind force and the off-wind falloff are byte-for-byte
    /// vanilla; the only thing that changes is HOW CLOSE TO THE WIND'S EYE the sail still pulls.
    /// A crewed boat goes nowhere faster than a solo boat - it can just go places a solo boat
    /// cannot without oars.
    ///
    /// Vanilla, verbatim (0.221.13, Ship.cs:497):
    ///
    ///     public float GetWindAngleFactor()
    ///     {
    ///         float num  = Vector3.Dot(EnvMan.instance.GetWindDir(), -base.transform.forward);
    ///         float num2 = Mathf.Lerp(0.7f, 1f, 1f - Utils.Abs(num));
    ///         float num3 = 1f - Utils.LerpStep(0.75f, 0.8f, num);
    ///         return num2 * num3;
    ///     }
    ///
    /// with (assembly_utils/Utils.cs:187) <c>LerpStep(l, h, v) = Clamp01((v - l) / (h - l))</c>.
    ///
    ///   * <c>num</c> is cos(angle between the ship's heading and dead into the wind): 1 = bow
    ///     straight into the wind's eye, -1 = dead downwind.
    ///   * <c>num2</c> is the OFF-WIND FLOOR: 1.0 beam-on, falling to 0.70 dead upwind AND dead
    ///     downwind (it is symmetric in |num|). **Untouched** - which is why downwind speed,
    ///     including Speed.Full, is bit-identical to vanilla at every crew size.
    ///   * <c>num3</c> is the dead zone: it reaches 0 at num >= 0.80, i.e. within
    ///     acos(0.80) = 36.87 degrees of the wind's eye, and ramps in from num = 0.75
    ///     (41.41 degrees) - a 4.5-degree-wide ramp.
    ///
    /// This module replaces ONLY the pair (0.75, 0.80) with a crew-dependent pair. `m_sailForceFactor`,
    /// `Speed.Full`, the 0.70 floor and the Lerp/LerpStep shapes are not touched anywhere. Crew 1
    /// (or 0) is left exactly as vanilla - the postfix returns without writing __result at all.
    ///
    /// The pair comes from the configured cone in DEGREES: <c>hi = cos(deg)</c>, <c>lo = hi - 0.05</c>
    /// (the vanilla ramp width, which is 0.05 wide). Degrees are clamped to
    /// [MinConeDeg = 20, vanilla 36.87] - the dead zone can never close completely (straight
    /// upwind is still oars) and can never be made WIDER than vanilla either.
    ///
    /// A postfix that RECOMPUTES the whole expression is used rather than a transpiler over the
    /// two literals: it needs no IL match-count assertion to stay honest across a game patch, it
    /// survives a future vanilla cache of the result, and the arithmetic above is four lines.
    ///
    /// Crew count: NEVER the local <c>m_players.Count</c> - see CrewShip.cs. The ship owner writes
    /// it into the ship ZDO from a postfix on <c>Ship.UpdateOwner</c>, which vanilla already runs
    /// on an <c>InvokeRepeating("UpdateOwner", 2f, 2f)</c> from <c>Ship.Awake</c> - so the write
    /// costs one ZDO int comparison every 2 seconds per ship, and only actually writes when the
    /// number changed. The helmsman counts as crew.
    ///
    /// Because the force is derived from state every machine agrees on, the owner's physics
    /// (<c>Ship.CustomFixedUpdate</c>, which early-returns on non-owners) and the sail animation
    /// every passenger renders stay in step.
    ///
    /// Visual: the minimum. Vanilla already lerps <c>Hud.m_shipWindIcon</c>'s colour by
    /// <c>GetWindAngleFactor()</c>, so the helmsman simply sees the wind arrow brighten when the
    /// crew makes the sail catch. The one addition is that PASSENGERS get that same read-only
    /// gauge (see ShipHudPost) - wind arrow only, never rudder or sail controls. No sounds, no
    /// messages, no vignette.
    /// </summary>
    internal sealed class SeaLegsModule : FeatureModule
    {
        public override string Name => "SeaLegs";

        /// <summary>Both: the owner may be a client or (with nobody aboard) the server.</summary>
        public override ModuleSide Side => ModuleSide.Both;

        /// <summary>Vanilla's pair, reproduced from the decompile.</summary>
        private const float VanillaLo = 0.75f;
        private const float VanillaHi = 0.80f;

        /// <summary>Vanilla ramp width, kept for every crew size: lo = hi - RampWidth.</summary>
        private const float RampWidth = 0.05f;

        /// <summary>The dead zone never closes past this - straight upwind is still oars.</summary>
        private const float MinConeDeg = 20f;

        /// <summary>Highest crew index the cone table has an entry for.</summary>
        private const int MaxTableCrew = 4;

        private ConfigEntry<float> _crew2Cone;
        private ConfigEntry<float> _crew3Cone;
        private ConfigEntry<float> _crew4Cone;
        private ConfigEntry<int> _maxCrewCounted;
        private ConfigEntry<bool> _passengerGauge;
        private ConfigEntry<bool> _selfTest;

        private static SeaLegsModule _self;

        // Cone table, index = crew clamped to [0, MaxTableCrew]. 0 and 1 hold vanilla.
        private static readonly float[] _lo = new float[MaxTableCrew + 1];
        private static readonly float[] _hi = new float[MaxTableCrew + 1];
        private static int _maxCounted = MaxTableCrew;

        private static bool _broken;      // wind-angle postfix
        private static bool _hudBroken;   // passenger gauge

        // Passenger gauge: which vanilla objects we hid, so they are always put back.
        // -1 = not resolved yet, 0 = hide the whole m_shipControlsRoot, 1 = hide the individual
        // control objects (used only if the wind indicator turns out to live UNDER that root).
        private static int _hideMode = -1;
        private static bool _hidRoot;
        private static bool _hidIndividual;
        private static bool _loggedHideMode;

        private static bool Live()
        {
            return !_broken && _self != null && _self.Active;
        }

        // ---- config ---------------------------------------------------------------------------

        protected override void Bind()
        {
            _self = this;

            _crew2Cone = BindSynced("Crew2Cone", 32f,
                "With 2 aboard: how close to the wind's eye, in DEGREES, the sail still pulls. " +
                "Vanilla is 36.87. Clamped to " + MinConeDeg.ToString("0") + "-36.87 - smaller " +
                "is a narrower dead zone (you point higher), and it can never close completely.");

            _crew3Cone = BindSynced("Crew3Cone", 27f,
                "With 3 aboard: dead-zone half-angle in degrees. Same clamp as Crew2Cone.");

            _crew4Cone = BindSynced("Crew4Cone", 23f,
                "With 4 or more aboard: dead-zone half-angle in degrees. Same clamp as Crew2Cone.");

            _maxCrewCounted = BindSynced("MaxCrewCounted", MaxTableCrew,
                "Crew above this number stops helping (1-" + MaxTableCrew + "). 1 turns the " +
                "narrowing off entirely without disabling the module.");

            _passengerGauge = BindLocal("PassengerWindGauge", true,
                "Local: show the read-only ship wind arrow on your HUD while you are aboard a " +
                "ship someone ELSE is steering. Rudder and sail controls stay hidden - it is a " +
                "gauge, not a set of controls.");

            _selfTest = BindLocal("SelfTest", false,
                "Diagnostic, local only, never synced. Logs the whole cone table (the LerpStep " +
                "pair and the resulting degrees for crew 1..4) at load, so the arithmetic can be " +
                "checked on a headless server. Changes no game state.");

            RebuildTable();
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            RebuildTable();
            if (Active) Log.LogInfo("[" + Name + "] " + Numbers());
            // If the gauge was just turned off while standing on someone else's boat, put the
            // vanilla HUD objects back on the next frame's postfix rather than leaving them
            // hidden - ShipHudPost restores unconditionally when it is not showing the gauge.
        }

        /// <summary>
        /// Cone degrees -> the LerpStep pair, with the vanilla ramp width. Monotonic: more crew
        /// can never point worse than fewer crew, whatever the config says.
        /// </summary>
        private void RebuildTable()
        {
            _lo[0] = _lo[1] = VanillaLo;
            _hi[0] = _hi[1] = VanillaHi;

            _hi[2] = HiFor(_crew2Cone, 32f);
            _hi[3] = HiFor(_crew3Cone, 27f);
            _hi[4] = HiFor(_crew4Cone, 23f);

            for (int i = 2; i <= MaxTableCrew; i++)
            {
                if (_hi[i] < _hi[i - 1]) _hi[i] = _hi[i - 1];
                _lo[i] = _hi[i] - RampWidth;
            }

            int m = _maxCrewCounted != null ? _maxCrewCounted.Value : MaxTableCrew;
            _maxCounted = Mathf.Clamp(m, 1, MaxTableCrew);
        }

        private static float HiFor(ConfigEntry<float> entry, float fallback)
        {
            float deg = entry != null ? entry.Value : fallback;
            if (float.IsNaN(deg)) deg = fallback;
            // Never below MinConeDeg (the dead zone never closes) and never above vanilla's own
            // cone (the module can only ever help).
            deg = Mathf.Clamp(deg, MinConeDeg, VanillaConeDeg);
            return Mathf.Cos(deg * Mathf.Deg2Rad);
        }

        /// <summary>Vanilla's dead-zone half-angle in degrees: acos(0.80) = 36.8699.</summary>
        private static float VanillaConeDeg
        {
            get { return Mathf.Acos(VanillaHi) * Mathf.Rad2Deg; }
        }

        private static float ConeDeg(float hi)
        {
            return Mathf.Acos(Mathf.Clamp(hi, -1f, 1f)) * Mathf.Rad2Deg;
        }

        // ---- patches --------------------------------------------------------------------------

        protected override void ApplyPatches()
        {
            var windAngle = AccessTools.Method(typeof(Ship), "GetWindAngleFactor", Type.EmptyTypes);
            if (windAngle == null) throw new Exception("Ship.GetWindAngleFactor() not found");
            Harmony.Patch(windAngle,
                postfix: new HarmonyMethod(typeof(SeaLegsModule), nameof(WindAngleFactorPost)));

            var updateOwner = AccessTools.Method(typeof(Ship), "UpdateOwner", Type.EmptyTypes);
            if (updateOwner == null) throw new Exception("Ship.UpdateOwner() not found");
            Harmony.Patch(updateOwner,
                postfix: new HarmonyMethod(typeof(SeaLegsModule), nameof(UpdateOwnerPost)));

            // The list the owner counts, and the wind source the factor is recomputed from, must
            // both still exist - fail LOUDLY at load rather than silently sailing like vanilla.
            if (AccessTools.Field(typeof(Ship), "m_players") == null)
                throw new Exception("Ship.m_players not found");
            if (AccessTools.Method(typeof(EnvMan), "GetWindDir", Type.EmptyTypes) == null)
                throw new Exception("EnvMan.GetWindDir() not found");

            var shipHud = AccessTools.Method(typeof(Hud), "UpdateShipHud",
                new[] { typeof(Player), typeof(float) });
            if (shipHud == null) throw new Exception("Hud.UpdateShipHud(Player, float) not found");
            Harmony.Patch(shipHud,
                postfix: new HarmonyMethod(typeof(SeaLegsModule), nameof(ShipHudPost)));

            Log.LogInfo("[" + Name + "] " + Numbers());
            if (_selfTest != null && _selfTest.Value) RunSelfTest();
        }

        // ---- 1. the owner publishes the crew count -----------------------------------------------

        /// <summary>
        /// Vanilla runs UpdateOwner on an InvokeRepeating(2s, 2s) started in Ship.Awake, on every
        /// machine that has the ship instantiated; its body early-returns unless it is the owner,
        /// so this postfix does the ownership check itself. Only the owner writes, and only when
        /// the number actually changed, so a moored ship costs one GetInt comparison per 2 s.
        /// </summary>
        private static void UpdateOwnerPost(Ship __instance)
        {
            if (!Live() || __instance == null) return;
            try
            {
                var nview = __instance.m_nview;
                if (nview == null || !nview.IsValid() || !nview.IsOwner()) return;
                var zdo = nview.GetZDO();
                if (zdo == null) return;

                int crew = __instance.m_players != null ? __instance.m_players.Count : 0;
                crew = Mathf.Clamp(crew, 0, CrewShip.MaxCrew);

                if (zdo.GetInt(CrewShip.CrewKey, 0) != crew) zdo.Set(CrewShip.CrewKey, crew);
            }
            catch (Exception e)
            {
                _broken = true;
                Log.LogError("[SeaLegs] disabled for this session after an error in " +
                             "Ship.UpdateOwner postfix: " + e);
            }
        }

        // ---- 2. the cone ------------------------------------------------------------------------

        private static void WindAngleFactorPost(Ship __instance, ref float __result)
        {
            if (!Live() || __instance == null) return;
            try
            {
                int idx = Mathf.Clamp(CrewShip.CrewOf(__instance), 0, _maxCounted);
                if (idx <= 1) return;                       // solo (or empty) sails vanilla
                if (EnvMan.instance == null) return;

                // Vanilla arithmetic, reproduced exactly; the ONLY change is the LerpStep pair.
                float num = Vector3.Dot(EnvMan.instance.GetWindDir(), -__instance.transform.forward);
                float num2 = Mathf.Lerp(0.7f, 1f, 1f - Utils.Abs(num));   // off-wind floor: UNTOUCHED
                float num3 = 1f - Utils.LerpStep(_lo[idx], _hi[idx], num); // dead zone: narrowed
                __result = num2 * num3;
            }
            catch (Exception e)
            {
                _broken = true;
                Log.LogError("[SeaLegs] disabled for this session after an error in " +
                             "Ship.GetWindAngleFactor postfix: " + e);
            }
        }

        // ---- 3. the passenger's read-only wind gauge ----------------------------------------------

        /// <summary>
        /// Vanilla's UpdateShipHud shows the ship HUD only for the player at the tiller and hides
        /// the whole root for everyone else. This postfix brings the root back for a passenger and
        /// drives ONLY the three wind objects from the ship they are standing on, with the rudder
        /// and sail controls hidden. Nothing here touches the ship or any shared state.
        ///
        /// Restoring is unconditional and happens on every frame the gauge is not being shown -
        /// including the frame the player takes the tiller, the module is turned off, or they step
        /// ashore - so the vanilla HUD can never be left in a state this module put it in. The two
        /// objects vanilla never re-activates itself (m_shipControlsRoot and m_shipRudderIcon) are
        /// the only ones tracked; everything else in that cluster is re-asserted by vanilla on
        /// every frame it draws the helmsman's HUD.
        /// </summary>
        private static void ShipHudPost(Hud __instance, Player player, float dt)
        {
            if (_hudBroken || __instance == null) return;
            if (!ClientActive()) return;

            try
            {
                bool want = _self != null && _self.Active &&
                            _self._passengerGauge != null && _self._passengerGauge.Value &&
                            player != null &&
                            player == Player.m_localPlayer &&
                            player.GetControlledShip() == null &&
                            __instance.IsVisible();

                Ship ship = want ? CrewShip.Aboard(player) : null;
                if (ship == null)
                {
                    RestoreControls(__instance);
                    return;
                }

                if (__instance.m_shipWindIndicatorRoot == null ||
                    __instance.m_shipWindIconRoot == null ||
                    __instance.m_shipWindIcon == null ||
                    __instance.m_shipHudRoot == null)
                {
                    RestoreControls(__instance);
                    return;
                }

                __instance.m_shipHudRoot.SetActive(true);
                HideControls(__instance, ship);

                __instance.m_shipWindIndicatorRoot.localRotation =
                    Quaternion.Euler(0f, 0f, ship.GetShipYawAngle());
                __instance.m_shipWindIconRoot.localRotation =
                    Quaternion.Euler(0f, 0f, ship.GetWindAngle());
                __instance.m_shipWindIcon.color =
                    Color.Lerp(Hud.s_shipWindIconColor, Color.white, ship.GetWindAngleFactor());
            }
            catch (Exception e)
            {
                _hudBroken = true;
                try { RestoreControls(__instance); } catch { }
                Log.LogError("[SeaLegs] passenger wind gauge disabled for this session after an " +
                             "error in Hud.UpdateShipHud postfix: " + e);
            }
        }

        private static void ResolveHideMode(Hud hud)
        {
            if (_hideMode >= 0) return;
            var wind = hud.m_shipWindIndicatorRoot;
            var root = hud.m_shipControlsRoot;
            _hideMode = (wind != null && root != null && wind.IsChildOf(root.transform)) ? 1 : 0;
            if (!_loggedHideMode)
            {
                _loggedHideMode = true;
                Log.LogInfo("[SeaLegs] passenger wind gauge: hiding " +
                            (_hideMode == 0 ? "m_shipControlsRoot (wind indicator is a sibling)"
                                            : "the individual rudder/sail objects (wind indicator " +
                                              "is a CHILD of m_shipControlsRoot)"));
            }
        }

        private static void HideControls(Hud hud, Ship ship)
        {
            ResolveHideMode(hud);

            if (_hideMode == 0)
            {
                if (hud.m_shipControlsRoot != null && hud.m_shipControlsRoot.activeSelf)
                {
                    hud.m_shipControlsRoot.SetActive(false);
                    _hidRoot = true;
                }
                return;
            }

            // Fallback: the wind ring lives under the controls root, so hide the control objects
            // one by one instead and keep the root where vanilla would have put it.
            Off(hud.m_rudderLeft); Off(hud.m_rudderRight);
            Off(hud.m_rudderSlow); Off(hud.m_rudderForward);
            Off(hud.m_rudderFastForward); Off(hud.m_rudderBackward);
            Off(hud.m_halfSail); Off(hud.m_fullSail); Off(hud.m_rudder);
            if (hud.m_shipRudderIndicator != null) Off(hud.m_shipRudderIndicator.gameObject);
            if (hud.m_shipRudderIcon != null) Off(hud.m_shipRudderIcon.gameObject);
            _hidIndividual = true;

            var cam = Utils.GetMainCamera();
            if (cam != null && hud.m_shipControlsRoot != null && ship.m_controlGuiPos != null)
            {
                hud.m_shipControlsRoot.transform.position =
                    cam.WorldToScreenPointScaled(ship.m_controlGuiPos.position);
            }
        }

        private static void Off(GameObject go)
        {
            if (go != null && go.activeSelf) go.SetActive(false);
        }

        private static void RestoreControls(Hud hud)
        {
            if (hud == null) return;

            if (_hidRoot)
            {
                _hidRoot = false;
                if (hud.m_shipControlsRoot != null) hud.m_shipControlsRoot.SetActive(true);
            }

            if (_hidIndividual)
            {
                _hidIndividual = false;
                // Only the one vanilla never SetActive()s itself. Every other object in the
                // cluster is re-asserted by UpdateShipHud on each frame it draws the helm HUD,
                // so putting them back here would just flicker them for a frame.
                if (hud.m_shipRudderIcon != null && !hud.m_shipRudderIcon.gameObject.activeSelf)
                    hud.m_shipRudderIcon.gameObject.SetActive(true);
            }
        }

        public override void Disable()
        {
            try { if (Hud.instance != null) RestoreControls(Hud.instance); } catch { }
            base.Disable();
        }

        // ---- reporting ---------------------------------------------------------------------------

        private string Numbers()
        {
            return "Crew2Cone=" + ConeDeg(_hi[2]).ToString("0.0") + "deg" +
                   " Crew3Cone=" + ConeDeg(_hi[3]).ToString("0.0") + "deg" +
                   " Crew4Cone=" + ConeDeg(_hi[4]).ToString("0.0") + "deg" +
                   " MaxCrewCounted=" + _maxCounted +
                   " PassengerWindGauge=" + (_passengerGauge != null && _passengerGauge.Value) +
                   " (cone only; off-wind floor 0.70, m_sailForceFactor and Speed.Full unchanged)";
        }

        public override string StatusDetail() { return Numbers(); }

        // ---- headless proof -----------------------------------------------------------------------

        /// <summary>
        /// Pure arithmetic on the config - no world, no player, no ObjectDB - so a dedicated
        /// server can print and check the whole cone table at load. Changes nothing.
        /// </summary>
        private void RunSelfTest()
        {
            var sb = new StringBuilder();
            sb.Append("[SeaLegs][SelfTest] --- begin --- ").Append(Numbers());
            sb.Append("\n  vanilla Ship.GetWindAngleFactor():");
            sb.Append("\n    num  = Vector3.Dot(windDir, -transform.forward)      (1 = bow into the wind's eye)");
            sb.Append("\n    num2 = Mathf.Lerp(0.7, 1, 1 - Utils.Abs(num))        <- off-wind floor, UNTOUCHED");
            sb.Append("\n    num3 = 1 - Utils.LerpStep(0.75, 0.80, num)           <- dead zone, the only thing changed");
            sb.Append("\n    return num2 * num3;   Utils.LerpStep(l,h,v) = Clamp01((v - l) / (h - l))");
            sb.Append("\n  cone = acos(hi) in degrees; lo = hi - ").Append(RampWidth.ToString("0.00"))
              .Append(" (vanilla ramp width); clamp [").Append(MinConeDeg.ToString("0"))
              .Append(", ").Append(VanillaConeDeg.ToString("0.00")).Append("] degrees");

            for (int crew = 1; crew <= MaxTableCrew; crew++)
            {
                int idx = Mathf.Clamp(crew, 0, _maxCounted);
                string label = "crew " + crew + (crew >= MaxTableCrew ? "+" : " ");
                string note = idx <= 1 ? "  (vanilla, postfix does not write)" :
                              (idx != crew ? "  (capped by MaxCrewCounted=" + _maxCounted + ")" : "");
                sb.Append("\n  ").Append(label)
                  .Append(": LerpStep(").Append(_lo[idx].ToString("0.0000"))
                  .Append(", ").Append(_hi[idx].ToString("0.0000")).Append(")")
                  .Append("  dead cone ").Append(ConeDeg(_hi[idx]).ToString("0.00")).Append(" deg")
                  .Append("  ramp opens at ").Append(ConeDeg(_lo[idx]).ToString("0.00")).Append(" deg")
                  .Append(note);
            }

            // Prove the two invariants Matt asked for, with numbers rather than a claim.
            sb.Append("\n  invariant: beam-on (num=0) factor = ")
              .Append((Mathf.Lerp(0.7f, 1f, 1f) * (1f - Utils.LerpStep(_lo[_maxCounted], _hi[_maxCounted], 0f))).ToString("0.0000"))
              .Append(" for crew ").Append(_maxCounted)
              .Append(", vanilla ")
              .Append((Mathf.Lerp(0.7f, 1f, 1f) * (1f - Utils.LerpStep(VanillaLo, VanillaHi, 0f))).ToString("0.0000"));
            sb.Append("\n  invariant: dead downwind (num=-1) factor = ")
              .Append((Mathf.Lerp(0.7f, 1f, 0f) * (1f - Utils.LerpStep(_lo[_maxCounted], _hi[_maxCounted], -1f))).ToString("0.0000"))
              .Append(" for crew ").Append(_maxCounted)
              .Append(", vanilla ")
              .Append((Mathf.Lerp(0.7f, 1f, 0f) * (1f - Utils.LerpStep(VanillaLo, VanillaHi, -1f))).ToString("0.0000"))
              .Append("  <- identical; no speed change");
            sb.Append("\n  crew count source: ZDO int \"").Append(CrewShip.CrewKey)
              .Append("\", written by the ship OWNER in a Ship.UpdateOwner postfix (vanilla ")
              .Append("InvokeRepeating every 2 s), read by everyone; never m_players.Count on a reader");
            sb.Append("\n[SeaLegs][SelfTest] --- end ---");
            Log.LogInfo(sb.ToString());
        }
    }
}
