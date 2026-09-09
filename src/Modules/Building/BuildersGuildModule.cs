using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// BuildersGuild - cheaper building at the base. Stage 1 of the plan in
    /// docs/BUILD-COSTS-PLAN.md: the YARD (station-gated, per-material cost multipliers) and the
    /// FRAMING rule (support beams and poles cost 1 of each material free-standing and nothing at
    /// all when snapped onto another beam).
    ///
    ///     piece cost = vanilla x tier x settlement x YARD(material, station in range) x [skill] x [rhythm]
    ///                  rounded once, floored at MinAmount (or this module's per-material floor)
    ///
    /// WHERE THE CODE LIVES. Exactly like <see cref="SettlementDiscountModule"/>, this module does
    /// not price anything itself: it contributes a FACTOR (and, for the framing rule, an absolute
    /// override) into the single requirement-scaling context that
    /// <see cref="TrailingTierDiscountModule"/> owns. That context is the only place a build
    /// piece's cost is ever changed, and it reaches all four call sites that must agree:
    ///
    ///   Hud.SetupPieceInfo(Piece)                        the build hammer's cost row (display)
    ///   Player.HaveRequirements(Piece, RequirementMode)  "can I build this?" (reads m_amount directly)
    ///   Player.ConsumeResources(Requirement[], ...)      what actually leaves the inventory
    ///   Piece.DropResources(HitData)                     the deconstruct REFUND
    ///
    /// So the number SHOWN, the number CHECKED, the number CONSUMED and the number REFUNDED cannot
    /// disagree. Because the shared machinery is installed by <c>[Discount]</c>, <c>[Discount]
    /// Enabled</c> must have been ON at startup for this module to reach the game at all.
    ///
    /// THE YARD. A material is discounted only while its HOME STATION is within
    /// <c>YardRadius</c> (80 m by default) of the player - wood/core wood/fine wood at a workbench,
    /// stone at a stonecutter, iron/bronze/copper at a forge, black metal at a black forge. The
    /// scan is throttled (<c>CheckIntervalSeconds</c>) and has hysteresis on the way out
    /// (<c>HysteresisSeconds</c>), the same shape as BuildersLoad's, so a cost cannot flicker while
    /// you stand on the boundary. Outside every yard the multiplier is a flat 1.0 - the wild costs
    /// vanilla, minus whatever tier and settlement already give.
    ///
    /// THE FRAMING RULE. Pieces in the STRUCTURAL SET (every beam and pole, auto-discovered from
    /// the hammer's own piece table by prefab-name shape and adjustable through
    /// <c>StructuralPieces</c>) bypass the multipliers entirely and use flat numbers:
    /// <c>StructuralFirstCost</c> (1) of each material free-standing, <c>StructuralAttachedCost</c>
    /// (0) when the placement ghost is snapped onto ANOTHER STRUCTURAL PIECE. Snapped onto a wall,
    /// a floor or a roof does not count - frames are what gets rewarded. "Snapped onto" is decided
    /// exactly the way vanilla decides it: a snap point of the ghost within 0.5 m (vanilla's own
    /// <c>maxSnapDistance</c> in <c>Player.UpdatePlacementGhost</c>) of a snap point belonging to a
    /// structural piece, over the same <c>Piece.GetSnapPoints(point, 10f, ...)</c> overlap vanilla
    /// uses.
    ///
    /// REFUNDS ARE TRACKED, NOT GUESSED. A free beam that refunded "what it would cost now" would
    /// be a duplication bug the moment the rule or the yard changed. So on a successful placement
    /// the placing client - which instantiated the piece and therefore OWNS its brand-new ZDO -
    /// writes what it actually paid onto the piece: <c>nvlb.paid</c> (how many materials were
    /// recorded, the presence marker, because a recorded amount of 0 is legitimate) plus one
    /// <c>nvlb.paid.&lt;Material&gt;</c> int per requirement. <c>Piece.DropResources</c> then refunds
    /// exactly that: 0 for a free attached beam, 1 for the paid root of the chain. A structural
    /// piece with no record - anything built before this version - refunds through the normal
    /// multiplier pipeline instead, which can only ever give back today's price.
    ///
    /// KNOWN, DELIBERATE: the yard factor is transient, so a piece built next to a stonecutter and
    /// deconstructed after that stonecutter is gone refunds the un-discounted price. That is the
    /// same trade <c>[Settlement]</c> already documents ("a refund is what the piece costs right
    /// now"); Stage 3 (Rhythm) tightens it to "the cheapest factor the builder could have had".
    ///
    /// WHAT IT NEVER TOUCHES: crafting recipes (iron still costs iron in gear - that is the whole
    /// point), crafting-station build costs, repair, and ward permissions.
    ///
    /// Side = Client, like the rest of the build-cost family: a dedicated server never places a
    /// piece. <c>[Builders] SelfTest</c> flips Side to Both so a headless server can prove the
    /// arithmetic against real prefabs; every patch body still gates on ClientActive() and stays
    /// inert there.
    /// </summary>
    internal sealed class BuildersGuildModule : FeatureModule
    {
        public override string Name => "BuildersGuild";
        public override string Section => "Builders";
        public override string Theme => "Building & gathering";
        public override string Hint => "Cheap building at your base: the yard and the framing rule";

        /// <summary>Client, except while the local SelfTest flag is on - see the class doc.</summary>
        public override ModuleSide Side =>
            _selfTest != null && _selfTest.Value ? ModuleSide.Both : ModuleSide.Client;

        protected override string EnabledDescription =>
            "Building costs less at your base: each material is discounted while its home station " +
            "is inside YardRadius, and beams/poles use the flat framing rule (1 of each material " +
            "free-standing, 0 when snapped onto another beam). Deconstruct refunds exactly what " +
            "was paid. Build pieces only - crafting recipes are never touched. Requires " +
            "[Discount] Enabled at startup: the shared build-cost machinery this module feeds is " +
            "installed there.";

        // ---- defaults (Matt's picks, docs/BUILD-COSTS-PLAN.md) ------------------------------

        internal const string DefaultMaterials =
            "Wood:0.5,RoundLog:0.5,FineWood:0.5,Stone:0.5,Iron:0.10,Bronze:0.5,Copper:0.5,BlackMetal:0.5";

        internal const string DefaultStations =
            "piece_workbench:Wood|RoundLog|FineWood,piece_stonecutter:Stone," +
            "forge:Iron|Bronze|Copper,blackforge:BlackMetal";

        /// <summary>Vanilla's own snap tolerance - Player.UpdatePlacementGhost calls
        /// FindClosestSnapPoints(ghost, 0.5f, ...). Hard-coded on purpose: a different number here
        /// would mean the mod thinks a piece is snapped when the game does not.</summary>
        private const float SnapTolerance = 0.5f;

        /// <summary>The radius vanilla's own Piece.GetSnapPoints(point, radius, ...) uses.</summary>
        private const float SnapSearchRadius = 10f;

        // ---- config ---------------------------------------------------------------------------

        private static ConfigEntry<float> _yardRadius;
        private static ConfigEntry<string> _materialsCfg;
        private static ConfigEntry<string> _stationsCfg;
        private static ConfigEntry<bool> _stationLevelScaling;
        private static ConfigEntry<string> _minAmountsCfg;
        private static ConfigEntry<string> _structuralCfg;
        private static ConfigEntry<int> _structuralFirstCost;
        private static ConfigEntry<int> _structuralAttachedCost;
        private static ConfigEntry<bool> _structuralInYardOnly;
        private static ConfigEntry<float> _hysteresis;
        private static ConfigEntry<float> _checkInterval;
        private static ConfigEntry<bool> _showTooltip;

        // stage 2 - the Builder skill
        private static ConfigEntry<bool> _skillEnabled;
        private static ConfigEntry<float> _skillXpPerMaterial;
        private static ConfigEntry<float> _skillMaxDiscount;

        // stage 3 - Rhythm
        private static ConfigEntry<bool> _rhythmEnabled;
        private static ConfigEntry<float> _rhythmWindowSec;
        private static ConfigEntry<float> _rhythmPerRepeat;
        private static ConfigEntry<float> _rhythmMax;

        private static ConfigEntry<bool> _selfTest;

        private static BuildersGuildModule _self;
        private static bool _reported;

        /// <summary>material prefab name -> cost multiplier, from [Builders] Materials.</summary>
        private static Dictionary<string, float> _matMult =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        /// <summary>material prefab name -> per-material floor, from [Builders] MinAmounts.</summary>
        private static Dictionary<string, int> _matFloor =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>material prefab name -> the station prefabs that discount it.</summary>
        private static Dictionary<string, List<string>> _homeStations =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Every station prefab named in [Builders] Stations.</summary>
        private static List<string> _stationNames = new List<string>();

        /// <summary>Manual add/remove entries from [Builders] StructuralPieces.</summary>
        private static readonly List<string> _structuralAdd = new List<string>();
        private static readonly HashSet<string> _structuralRemove =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The structural set actually in force: auto-discovered + adds - removes.</summary>
        private static HashSet<string> _structural =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static List<string> _autoStructural = new List<string>();
        /// <summary>Beam/pole-shaped names the build-category filter threw out, for the log.</summary>
        private static readonly List<string> _rejected = new List<string>();
        private static bool _structuralResolved;
        private static float _lastStructuralTry = -999f;

        /// <summary>Station prefab -> the level at which its discount is "full", derived from how
        /// many distinct StationExtension prefabs target it. 1 = cannot be extended (the
        /// stonecutter), so it is always full. Only consulted when StationLevelScaling is on.</summary>
        private static Dictionary<string, int> _stationFullLevel =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static bool _fullLevelsResolved;

        private static FieldInfo _fiPlacementGhost;

        // ---- runtime state ----------------------------------------------------------------------

        private sealed class StationState
        {
            public bool In;
            public float LastCheck = -999f;
            public float LastIn = -999f;
            public int Level = 1;
        }

        private static readonly Dictionary<string, StationState> _stationState =
            new Dictionary<string, StationState>(StringComparer.OrdinalIgnoreCase);

        // "is the ghost snapped onto a structural piece?", cached for one frame
        private static int _ghostFrame = -1;
        private static bool _ghostAttached;

        // the decision frozen for one placement (see AttachedFor)
        private static int _lockedFrame = -1;
        private static bool _lockedAttached;
        private static Piece _lockedPiece;
        // ...and the two transient factors frozen with it, so the XP this very placement earns
        // (and the streak it starts) cannot change the price the player was just quoted.
        private static float _lockedSkill = 1f;
        private static float _lockedRhythm = 1f;

        // rhythm streak (local player only, never stored)
        private static string _rhythmPrefab;
        private static float _rhythmLast = -999f;
        private static int _rhythmCount;
        private static bool _commandRegistered;

        // what the placing client is about to record on the new piece's ZDO
        private static int _pendingFrame = -1;
        private static string _pendingPrefab;
        private static readonly List<KeyValuePair<string, int>> _pendingPaid =
            new List<KeyValuePair<string, int>>();

        // scratch buffers - the snap test runs at most once per frame, but never allocates
        private static readonly List<Transform> _ghostPoints = new List<Transform>();
        private static readonly List<Transform> _nearPoints = new List<Transform>();
        private static readonly List<Piece> _nearPieces = new List<Piece>();
        private static readonly List<Transform> _otherPoints = new List<Transform>();

        // ---- self-test overrides (null / -1 = off; only ever set inside SelfTest) ---------------

        private static HashSet<string> _testStationsInRange;
        private static int _testAttached = -1;
        private static Dictionary<string, int> _testRecord;
        private static bool _testLive;
        private static float _testSkillLevel = -1f;
        private static float _testNow = -1f;

        /// <summary>The clock the rhythm streak runs on. Injectable so the self test can prove the
        /// timeout without sleeping for 20 real seconds.</summary>
        private static float Now()
        {
            return _testNow >= 0f ? _testNow : Time.time;
        }

        /// <summary>ZDO keys. The marker holds the NUMBER of recorded materials, because a
        /// recorded amount of 0 is a legitimate value and GetInt's default is also 0.</summary>
        private const string PaidMarkerKey = "nvlb.paid";
        private const string PaidPrefix = "nvlb.paid.";
        private static readonly int PaidMarkerHash = PaidMarkerKey.GetStableHashCode();

        /// <summary>Enabled, patched, and running somewhere this module is allowed to act.</summary>
        internal static bool Live()
        {
            return _self != null && _self.Active && (ClientActive() || _testLive);
        }

        // ---- Stage 2 (Builder skill) and Stage 3 (Rhythm) ------------------------------------------

        /// <summary>
        /// The two WHO-and-HOW factors, multiplied into the same single rounding as the yard:
        /// the builder's own skill and their placement rhythm.
        ///
        /// <paramref name="cheapest"/> is the refund rule (see <see cref="MaterialFactor"/>): a
        /// transient factor must answer with the LOWEST value the builder could have had, so a
        /// refund can never pay out more than the piece was bought for. Rhythm is transient, so a
        /// refund is priced at FULL streak. Skill is not transient in the dangerous direction - it
        /// only ever goes up, so today's skill factor is already no larger than the one the piece
        /// was bought at, and using it live is both correct and simpler than recording it.
        ///
        /// During a placement both are FROZEN (see PlacePiecePre): the same swing of the hammer
        /// awards XP and extends the streak, and neither may move the price between the number the
        /// player was shown and the number ConsumeResources takes out of their pack.
        /// </summary>
        internal static float ExtraFactorFor(Piece piece, Piece.Requirement req, bool cheapest)
        {
            if (cheapest) return SkillFactor() * (1f - RhythmMaxClamped());

            if (_lockedFrame == Time.frameCount && ReferenceEquals(_lockedPiece, piece))
                return _lockedSkill * _lockedRhythm;

            return SkillFactor() * RhythmFactor(piece);
        }

        /// <summary>Extra segments for the one-line build tooltip, appended after the yard /
        /// framing part: "Builder Lv 31 -9%", "Rhythm x4 -20%".</summary>
        internal static void ExtraTooltipSegments(Piece piece, List<string> segs)
        {
            // Only once it is worth at least a whole percent - "Builder Lv 1 -0%" on every tooltip
            // from the first wall onwards is clutter, not information. nvlb.status and
            // nvlb.builder always show the level.
            if (SkillOn())
            {
                float f = SkillFactor();
                int pct = Mathf.RoundToInt((1f - f) * 100f);
                if (pct >= 1) segs.Add("Builder Lv " + Mathf.FloorToInt(LocalSkill().Level) + " -" + pct + "%");
            }

            int streak = RhythmStreak(piece);
            if (streak > 0)
            {
                float f = RhythmFactor(piece);
                segs.Add("Rhythm x" + streak + " -" + Mathf.RoundToInt((1f - f) * 100f) + "%");
            }
        }

        // ---- the Builder skill ---------------------------------------------------------------------

        private static bool SkillOn()
        {
            return Live() && _skillEnabled != null && _skillEnabled.Value &&
                   _skillMaxDiscount != null && _skillMaxDiscount.Value > 0f;
        }

        /// <summary>The local player's record, or the level the self test injected.</summary>
        internal static BuilderSkill.Record LocalSkill()
        {
            if (_testSkillLevel >= 0f) return new BuilderSkill.Record(_testSkillLevel, 0f);
            return BuilderSkill.Get(Player.m_localPlayer);
        }

        /// <summary>The skill's cost multiplier right now. 1 = no discount / no player.</summary>
        internal static float SkillFactor()
        {
            if (!SkillOn()) return 1f;
            return BuilderSkill.FactorFor(LocalSkill().Level, _skillMaxDiscount.Value);
        }

        /// <summary>The XP one placement of this piece is worth: its VANILLA material count, so a
        /// stone wall earns more than a torch and a free attached beam still earns what a beam is
        /// worth - it is still building.</summary>
        internal static float XpFor(Piece piece)
        {
            if (piece == null || piece.m_resources == null) return 0f;
            int materials = 0;
            for (int i = 0; i < piece.m_resources.Length; i++)
            {
                var r = piece.m_resources[i];
                if (r == null || r.m_resItem == null || r.m_amount <= 0) continue;
                materials += r.m_amount;
            }
            float per = _skillXpPerMaterial != null ? _skillXpPerMaterial.Value : 1f;
            if (float.IsNaN(per) || per <= 0f) return 0f;
            return materials * per;
        }

        /// <summary>Award the XP for a piece that was actually placed. Deconstructing calls this
        /// nowhere - tearing your own wall down is not practice.</summary>
        private static void AwardXp(Piece piece)
        {
            if (!SkillOn() || _testSkillLevel >= 0f) return;
            var player = Player.m_localPlayer;
            if (player == null) return;

            float xp = XpFor(piece);
            if (xp <= 0f) return;

            int gained;
            var rec = BuilderSkill.Raise(BuilderSkill.Get(player), xp, out gained);
            BuilderSkill.Set(player, rec);

            if (gained > 0)
            {
                try
                {
                    player.Message(MessageHud.MessageType.TopLeft,
                                   "$msg_skillup Builder: " + Mathf.FloorToInt(rec.Level));
                }
                catch { /* a message is never worth an exception in the placement path */ }
                Log.LogInfo("[Builders] Builder skill " + BuilderSkill.Describe(rec) +
                            " (+" + gained + ")");
            }
        }

        // ---- Rhythm ----------------------------------------------------------------------------------

        private static bool RhythmOn()
        {
            return Live() && _rhythmEnabled != null && _rhythmEnabled.Value &&
                   _rhythmPerRepeat != null && _rhythmPerRepeat.Value > 0f;
        }

        /// <summary>The configured ceiling, whatever the module's state - for the log line.</summary>
        private static float RhythmMaxRaw()
        {
            if (_rhythmMax == null) return 0f;
            float m = _rhythmMax.Value;
            return float.IsNaN(m) ? 0f : Mathf.Clamp(m, 0f, 0.9f);
        }

        /// <summary>The ceiling actually in force. 0 when rhythm is off, which is also what makes
        /// a "full rhythm" refund collapse to no rhythm discount at all.</summary>
        private static float RhythmMaxClamped()
        {
            return RhythmOn() ? RhythmMaxRaw() : 0f;
        }

        /// <summary>How many repeats of THIS prefab are banked: 0 for a different piece, for a
        /// streak that has timed out, or before the first placement.</summary>
        internal static int RhythmStreak(Piece piece)
        {
            if (!RhythmOn() || _rhythmCount <= 0 || piece == null || piece.gameObject == null) return 0;

            float window = _rhythmWindowSec != null ? Mathf.Max(0f, _rhythmWindowSec.Value) : 0f;
            if (Now() - _rhythmLast > window) return 0;
            if (!string.Equals(_rhythmPrefab, Utils.GetPrefabName(piece.gameObject),
                               StringComparison.OrdinalIgnoreCase)) return 0;
            return _rhythmCount;
        }

        /// <summary>The rhythm cost multiplier for this piece right now. 1 = no streak.</summary>
        internal static float RhythmFactor(Piece piece)
        {
            int streak = RhythmStreak(piece);
            if (streak <= 0) return 1f;
            float per = _rhythmPerRepeat != null ? Mathf.Max(0f, _rhythmPerRepeat.Value) : 0f;
            return 1f - Mathf.Min(streak * per, RhythmMaxClamped());
        }

        /// <summary>One piece was placed: extend the streak, or start a new one. Called from the
        /// PlacePiece POSTFIX, so it can never move the price of the piece being placed.</summary>
        internal static void RhythmPlaced(Piece piece)
        {
            if (!RhythmOn() || piece == null || piece.gameObject == null) return;
            var name = Utils.GetPrefabName(piece.gameObject);
            float window = _rhythmWindowSec != null ? Mathf.Max(0f, _rhythmWindowSec.Value) : 0f;

            if (_rhythmCount > 0 && Now() - _rhythmLast <= window &&
                string.Equals(_rhythmPrefab, name, StringComparison.OrdinalIgnoreCase))
                _rhythmCount++;
            else
            {
                _rhythmPrefab = name;
                _rhythmCount = 1;
            }
            _rhythmLast = Now();
        }

        private static void RhythmReset()
        {
            _rhythmPrefab = null;
            _rhythmCount = 0;
            _rhythmLast = -999f;
        }

        // ---- config -------------------------------------------------------------------------

        protected override void Bind()
        {
            _self = this;

            _yardRadius = BindSynced("YardRadius", 80f,
                "How far, in metres, a crafting station discounts its own materials. This is the " +
                "YARD - deliberately much larger than a workbench's build range, so one base with " +
                "the workbench, stonecutter and forge spread out still counts as one yard. " +
                "Measured horizontally from the station, the same way vanilla measures build " +
                "range. 0 = the yard never applies.",
                Opt.N("How far from a station its materials stay cheap", 0, 300, 5));

            _materialsCfg = BindSynced("Materials", DefaultMaterials,
                "Comma-separated MaterialPrefabName:multiplier pairs. Each material costs that " +
                "fraction of vanilla while its home station (see Stations) is inside YardRadius, " +
                "and full price everywhere else. 0.5 = half, 0.10 = a tenth. Clamped to 0.01..1: " +
                "this module never makes anything more expensive. Materials that are not listed " +
                "are never discounted. Names are ITEM PREFAB names and are checked against " +
                "ObjectDB once the world loads; unknown names are logged once and ignored.",
                Opt.T("How much cheaper each material is inside the yard")
                    .Pick(new PickerSpec(PickerSource.Materials,
                        new PickerField("Multiplier", 0.01, 1.0, 0.5, false))));

            _stationsCfg = BindSynced("Stations", DefaultStations,
                "Which station discounts which materials: comma-separated " +
                "StationPrefabName:Material|Material|... entries. A material is only ever cheap " +
                "while one of ITS OWN stations is in range, so hauling stone to a forge buys you " +
                "nothing. A material listed in Materials with no station here is never " +
                "discounted (and is called out in the log at world load). Free text rather than a " +
                "tick list because each entry carries a list of its own.",
                Opt.T("Which station makes which materials cheap"));

            _stationLevelScaling = BindSynced("StationLevelScaling", false,
                "Off (the default): the full discount applies the moment the station stands. On: " +
                "the discount scales with the station's own upgrade level, so a bare bench in the " +
                "wild gives a fraction of it and a fully kitted hall gives all of it. \"Full\" is " +
                "derived from the game itself - the number of distinct station extensions that " +
                "exist for that station - so a station with no extensions at all (the " +
                "stonecutter) always gives the full discount.",
                Opt.B("Scale the discount by the station's upgrade level"));

            _minAmountsCfg = BindSynced("MinAmounts", "",
                "Per-material floor override: comma-separated MaterialPrefabName:floor pairs. " +
                "Empty (the default) means every material keeps the pipeline's normal floor, so a " +
                "requirement never drops below 1. \"Iron:0\" lets iron round all the way to zero " +
                "and DROP OFF the piece entirely - at a forge a wood-iron beam would then cost 0 " +
                "iron and 1 wood. Refunds of a zero-cost material are zero, so nothing can be " +
                "farmed with it. Use \"Iron:1\" to dial it back without touching the multiplier.",
                Opt.T("Lowest each material can be discounted to")
                    .Pick(new PickerSpec(PickerSource.Materials,
                        new PickerField("Floor", 0, 20, 1, true))));

            _structuralCfg = BindSynced("StructuralPieces", "",
                "Adjustments to the auto-discovered structural set (the beams and poles the " +
                "framing rule applies to). The set is found by walking the hammer's own piece " +
                "table and keeping every piece whose prefab name has a \"beam\" or \"pole\" part, " +
                "or contains log_26 / log_45 - the discovered list is logged once per world load. " +
                "List a piece PREFAB name here to ADD it, or prefix it with a minus " +
                "(\"-wood_pole\") to REMOVE one the pattern picked up by mistake. Empty = the " +
                "auto-discovered set exactly.",
                Opt.T("Extra beams/poles to add to (or drop from) the framing rule")
                    .Pick(new PickerSpec(PickerSource.Pieces)));

            _structuralFirstCost = BindSynced("StructuralFirstCost", 1,
                "What a free-standing beam or pole costs: this many of EACH of its materials, " +
                "whatever the recipe says. 1 (the default) means the first pole of a support " +
                "tower costs 1 iron and 1 wood. Never more than the piece's vanilla cost.",
                Opt.N("Cost of a beam that stands on its own", 0, 20, 1));

            _structuralAttachedCost = BindSynced("StructuralAttachedCost", 0,
                "What a beam or pole costs when it is SNAPPED ONTO ANOTHER BEAM OR POLE: this " +
                "many of each of its materials. 0 (the default) makes framing free once the first " +
                "one is paid for, so a 5-high iron support tower costs 1 iron and 1 wood in " +
                "total. Snapping onto a wall, a floor or a roof does not count.",
                Opt.N("Cost of a beam snapped onto another beam", 0, 20, 1));

            _structuralInYardOnly = BindSynced("StructuralInYardOnly", false,
                "Off (the default): the framing rule applies everywhere - vanilla already gates " +
                "wooden beams to a workbench and iron beams to a forge, which is gate enough. On: " +
                "the framing rule only applies while at least one of the piece's materials has " +
                "its home station inside YardRadius; outside, beams cost the normal discounted " +
                "price.",
                Opt.B("Only apply the framing rule inside a yard"));

            _hysteresis = BindSynced("HysteresisSeconds", 3f,
                "How long a station keeps counting as \"in range\" after you walk out of its " +
                "yard, so a displayed cost cannot flicker while you stand on the boundary. " +
                "0 = snap back instantly.",
                Opt.N("How long the yard lingers after you leave it", 0, 20, 0.5));

            _checkInterval = BindSynced("CheckIntervalSeconds", 0.5f,
                "How often the \"is my station in range?\" scan actually runs. The answer is " +
                "cached in between, because the build HUD asks for a piece's cost every frame. " +
                "Clamped to 0.05..5.",
                Opt.N("How often the yard check runs", 0.05, 5, 0.05));

            _showTooltip = BindSynced("ShowTooltip", true,
                "Add one short line to the build hammer's piece description saying why the piece " +
                "costs what it costs (\"Yard -50% Wood, -90% Iron\" / \"Framing: attached -> " +
                "free\"). Display only. Turn off if you would rather read the numbers alone.",
                Opt.B("Explain the discount in the build menu"));

            _skillEnabled = BindSynced("SkillEnabled", true,
                "The Builder skill: a per-character skill, 0-100, that makes every build piece a " +
                "little cheaper the more you have built. Tracked by this mod in the character " +
                "file (not a vanilla skill), so it is never lost on death and never shows up in " +
                "the vanilla skills panel. Off = no skill discount and no XP is recorded.",
                Opt.B("Building gets cheaper the more you have built"));

            _skillXpPerMaterial = BindSynced("SkillXpPerMaterial", 1f,
                "Builder XP per unit of VANILLA material in a piece you place: a 4-stone wall " +
                "earns 4, a torch earns 1. The vanilla count is used, so a beam made free by the " +
                "framing rule still earns what a beam is worth - it is still building. The level " +
                "curve is vanilla's own (pow(level+1, 1.5) * 0.5 + 0.5 per level), so raise this " +
                "to level faster. Deconstructing earns nothing.",
                Opt.N("How fast the Builder skill goes up", 0, 10, 0.1));

            _skillMaxDiscount = BindSynced("SkillMaxDiscount", 0.30f,
                "How much cheaper build pieces are at Builder level 100, as a fraction. 0.30 = " +
                "30% off at Lv 100, linear on the way up, so Lv 50 is 15% off. 0 = the skill is " +
                "still tracked and shown but costs nothing. Clamped to 0..0.9.",
                Opt.N("Biggest discount the Builder skill can reach", 0, 0.9, 0.05));

            _rhythmEnabled = BindSynced("RhythmEnabled", true,
                "Rhythm: placing the SAME piece over and over gets cheaper as you find a groove, " +
                "and resets the moment you switch piece or stop. Local to each builder and never " +
                "stored anywhere.",
                Opt.B("Placing the same piece repeatedly gets cheaper"));

            _rhythmWindowSec = BindSynced("RhythmWindowSec", 20f,
                "How long, in seconds, the streak survives between placements. Pause longer than " +
                "this and the rhythm resets to nothing.",
                Opt.N("How long a building streak survives a pause", 1, 120, 1));

            _rhythmPerRepeat = BindSynced("RhythmPerRepeat", 0.05f,
                "How much cheaper each repeat of the same piece makes the next one, as a " +
                "fraction. 0.05 = 5% per repeat.",
                Opt.N("Discount per repeat of the same piece", 0, 0.25, 0.01));

            _rhythmMax = BindSynced("RhythmMax", 0.25f,
                "Ceiling on the rhythm discount, as a fraction. 0.25 with RhythmPerRepeat 0.05 " +
                "means five in a row is as good as it gets. Deconstruct refunds always assume " +
                "FULL rhythm, so a refund can never exceed what the piece was bought for.",
                Opt.N("Biggest discount a building streak can reach", 0, 0.9, 0.05));

            _selfTest = BindLocal("SelfTest", false,
                "Diagnostic, local only, never synced. Flips this module's Side to Both so a " +
                "dedicated server can prove the yard maths, the framing rule and the refund " +
                "record against real prefabs, once per world load. Changes no game state - it " +
                "prices pieces, it never places one. Leave false in normal use.",
                Opt.B("Prove the yard and framing maths in the log").Admin().Restart());

            ParseLists();
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            if (entry == _materialsCfg || entry == _stationsCfg || entry == _minAmountsCfg ||
                entry == _structuralCfg)
            {
                ParseLists();
                _structuralResolved = false;
                _lastStructuralTry = -999f;
                _fullLevelsResolved = false;
                EnsureStructural();
                EnsureStationFullLevels();
            }
            if (entry == _rhythmEnabled || entry == _rhythmWindowSec || entry == _rhythmPerRepeat ||
                entry == _rhythmMax)
                RhythmReset();
            _stationState.Clear();
            _ghostFrame = -1;
            if (Active) Log.LogInfo("[" + Name + "] " + Numbers());
        }

        public override void Disable()
        {
            base.Disable();
            if (_self == this) _self = null;
        }

        private static void ParseLists()
        {
            // Materials: Name:multiplier
            var mult = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            foreach (var chunk in Split(_materialsCfg, DefaultMaterials))
            {
                string name;
                float v;
                if (!SplitPair(chunk, out name, out v))
                {
                    Log.LogWarning("[Builders] ignoring malformed Materials entry '" + chunk +
                                   "' (want MaterialPrefabName:multiplier)");
                    continue;
                }
                mult[name] = Mathf.Clamp(v, 0.01f, 1f);
            }
            _matMult = mult;

            // MinAmounts: Name:floor
            var floor = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var chunk in Split(_minAmountsCfg, ""))
            {
                string name;
                float v;
                if (!SplitPair(chunk, out name, out v))
                {
                    Log.LogWarning("[Builders] ignoring malformed MinAmounts entry '" + chunk +
                                   "' (want MaterialPrefabName:floor)");
                    continue;
                }
                floor[name] = Mathf.Max(0, Mathf.RoundToInt(v));
            }
            _matFloor = floor;

            // Stations: StationPrefab:Mat|Mat|Mat
            var home = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var stations = new List<string>();
            foreach (var chunk in Split(_stationsCfg, DefaultStations))
            {
                int colon = chunk.IndexOf(':');
                if (colon <= 0 || colon == chunk.Length - 1)
                {
                    Log.LogWarning("[Builders] ignoring malformed Stations entry '" + chunk +
                                   "' (want StationPrefabName:Material|Material)");
                    continue;
                }
                var station = chunk.Substring(0, colon).Trim();
                if (station.Length == 0) continue;
                if (!stations.Contains(station)) stations.Add(station);

                foreach (var m in chunk.Substring(colon + 1)
                                       .Split(new[] { '|', '/' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var mat = m.Trim();
                    if (mat.Length == 0) continue;
                    List<string> list;
                    if (!home.TryGetValue(mat, out list)) home[mat] = list = new List<string>();
                    if (!list.Contains(station)) list.Add(station);
                }
            }
            _homeStations = home;
            _stationNames = stations;

            // StructuralPieces: name adds, -name removes
            _structuralAdd.Clear();
            _structuralRemove.Clear();
            foreach (var chunk in Split(_structuralCfg, ""))
            {
                if (chunk[0] == '-' || chunk[0] == '!')
                {
                    var n = chunk.Substring(1).Trim();
                    if (n.Length > 0) _structuralRemove.Add(n);
                }
                else if (!_structuralAdd.Contains(chunk))
                {
                    _structuralAdd.Add(chunk);
                }
            }
        }

        private static bool SplitPair(string chunk, out string name, out float value)
        {
            name = null;
            value = 0f;
            int colon = chunk.LastIndexOf(':');
            if (colon <= 0) return false;
            name = chunk.Substring(0, colon).Trim();
            if (name.Length == 0) return false;
            return float.TryParse(chunk.Substring(colon + 1).Trim(), NumberStyles.Float,
                                  CultureInfo.InvariantCulture, out value);
        }

        private static IEnumerable<string> Split(ConfigEntry<string> entry, string fallback)
        {
            var raw = entry != null ? entry.Value : fallback;
            foreach (var chunk in (raw ?? "").Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var s = chunk.Trim();
                if (s.Length > 0) yield return s;
            }
        }

        // ---- patches ------------------------------------------------------------------------

        protected override void ApplyPatches()
        {
            // Display only: one extra line under the piece's description in the build HUD.
            var pieceInfo = AccessTools.Method(typeof(Hud), "SetupPieceInfo", new[] { typeof(Piece) });
            if (pieceInfo == null) throw new Exception("Hud.SetupPieceInfo(Piece) not found");
            Harmony.Patch(pieceInfo, postfix: Post(nameof(PieceInfoPost)));

            // Freeze the framing decision for the whole placement, BEFORE the new piece exists -
            // the instant it does, its own snap points sit exactly on the ghost's and every later
            // "am I attached?" test would answer yes. See AttachedFor.
            var placePiece = AccessTools.Method(typeof(Player), "PlacePiece",
                new[] { typeof(Piece), typeof(Vector3), typeof(Quaternion), typeof(bool), typeof(bool) });
            if (placePiece == null)
                throw new Exception("Player.PlacePiece(Piece,Vector3,Quaternion,bool,bool) not found");
            // Prefix freezes the price; postfix banks the XP and the streak. Vanilla reaches
            // PlacePiece only from TryPlacePiece's success branch, so the postfix IS "a piece was
            // successfully placed" - and it runs before ConsumeResources, which is exactly why the
            // prefix froze the numbers first.
            Harmony.Patch(placePiece,
                          prefix: Post(nameof(PlacePiecePre)),
                          postfix: Post(nameof(PlacePiecePost)));

            var termInit = AccessTools.Method(typeof(Terminal), "InitTerminal");
            if (termInit == null) throw new Exception("Terminal.InitTerminal() not found");
            Harmony.Patch(termInit, postfix: Post(nameof(RegisterCommand)));

            // Piece implements IPlaced, and Player.PlacePiece calls OnPlaced() on every IPlaced of
            // the object it just instantiated - so this is the first moment the new piece's own
            // ZDO exists, on the client that owns it.
            var onPlaced = AccessTools.Method(typeof(Piece), "OnPlaced");
            if (onPlaced == null) throw new Exception("Piece.OnPlaced() not found");
            Harmony.Patch(onPlaced, postfix: Post(nameof(OnPlacedPost)));

            _fiPlacementGhost = AccessTools.Field(typeof(Player), "m_placementGhost");
            if (_fiPlacementGhost == null) throw new Exception("Player.m_placementGhost not found");

            var start = AccessTools.Method(typeof(ZoneSystem), "Start");
            if (start == null) throw new Exception("ZoneSystem.Start() not found");
            Harmony.Patch(start, postfix: Post(nameof(WorldReady)));

            Log.LogInfo("[" + Name + "] " + Numbers());
        }

        private static HarmonyMethod Post(string name)
        {
            return new HarmonyMethod(typeof(BuildersGuildModule), name);
        }

        // ---- the yard -------------------------------------------------------------------------

        /// <summary>Is this station prefab inside YardRadius right now? Throttled, with hysteresis
        /// on the way out, exactly like BuildersLoad's own scan.</summary>
        private static bool StationInRange(string prefab, out int level)
        {
            level = 1;

            if (_testStationsInRange != null)
                return _testStationsInRange.Contains(prefab);

            var player = Player.m_localPlayer;
            if (player == null) return false;

            float radius = _yardRadius != null ? _yardRadius.Value : 0f;
            if (radius <= 0f) return false;

            StationState st;
            if (!_stationState.TryGetValue(prefab, out st)) _stationState[prefab] = st = new StationState();

            float now = Time.time;
            float interval = Mathf.Clamp(_checkInterval != null ? _checkInterval.Value : 0.5f, 0.05f, 5f);
            if (now - st.LastCheck < interval)
            {
                level = st.Level;
                return st.In;
            }

            st.LastCheck = now;
            int found;
            bool near = Scan(prefab, player.transform.position, radius, out found);
            if (near)
            {
                st.In = true;
                st.LastIn = now;
                st.Level = found;
                level = found;
                return true;
            }

            float hold = _hysteresis != null ? Mathf.Max(0f, _hysteresis.Value) : 0f;
            if (st.In && now - st.LastIn <= hold)
            {
                level = st.Level;
                return true;
            }

            st.In = false;
            st.Level = 1;
            return false;
        }

        /// <summary>The closest loaded station with this prefab name inside the radius, and its
        /// level. Horizontal distance, the way vanilla's HaveBuildStationInRange measures.</summary>
        private static bool Scan(string prefab, Vector3 point, float radius, out int level)
        {
            level = 1;
            var all = WorkbenchReachModule.AllStations();
            if (all == null) return false;

            bool found = false;
            float best = float.MaxValue;
            for (int i = 0; i < all.Count; i++)
            {
                var st = all[i];
                if (st == null) continue;
                var go = st.gameObject;
                if (go == null) continue;
                if (!string.Equals(Utils.GetPrefabName(go), prefab, StringComparison.OrdinalIgnoreCase)) continue;

                var pos = st.transform.position;
                var p = point;
                p.y = pos.y;
                float d = Vector3.Distance(pos, p);
                if (d >= radius || d >= best) continue;

                best = d;
                found = true;
                try { level = st.GetLevel(false); }
                catch { level = 1; }
            }
            return found;
        }

        /// <summary>Is this material inside one of its own yards, and how "full" is that station?
        /// The fraction is 1 unless StationLevelScaling is on.</summary>
        private static bool MaterialInYard(string material, out float fraction)
        {
            fraction = 1f;
            List<string> stations;
            if (!_homeStations.TryGetValue(material, out stations) || stations.Count == 0) return false;

            bool any = false;
            float bestFraction = 0f;
            for (int i = 0; i < stations.Count; i++)
            {
                int level;
                if (!StationInRange(stations[i], out level)) continue;
                any = true;
                float f = LevelFraction(stations[i], level);
                if (f > bestFraction) bestFraction = f;
            }
            if (!any) return false;
            fraction = bestFraction;
            return true;
        }

        /// <summary>How much of the discount a station of this level gives. 1 unless
        /// StationLevelScaling is on; a station with no extensions at all is always full.</summary>
        private static float LevelFraction(string prefab, int level)
        {
            if (_stationLevelScaling == null || !_stationLevelScaling.Value) return 1f;
            int full;
            if (!_stationFullLevel.TryGetValue(prefab, out full) || full <= 1) return 1f;
            if (level < 1) level = 1;
            return Mathf.Clamp01((level - 1) / (float)(full - 1));
        }

        /// <summary>
        /// The yard multiplier for one requirement of one build piece. 1 = no discount. This is
        /// what TrailingTierDiscount composes into the single rounding.
        ///
        /// <paramref name="cheapest"/> is THE REFUND RULE, and it is what stops the yard from
        /// being a resource duplicator. The yard is a TRANSIENT factor: a stone wall bought at
        /// x0.5 beside a stonecutter and deconstructed after that stonecutter is gone would
        /// otherwise refund the full vanilla price and double the stone. So a refund is priced as
        /// though the builder were standing inside EVERY yard with every station fully upgraded -
        /// the cheapest this piece could ever have been - which makes "a refund never exceeds what
        /// was paid" true by construction rather than by luck of where the player is standing.
        /// The documented cost of that rule: deconstructing something you built out in the wild
        /// gives back the yard price, i.e. slightly less than you paid. Deliberate, and the same
        /// direction of error [Settlement] already errs in.
        /// </summary>
        internal static float MaterialFactor(Piece piece, Piece.Requirement req, bool cheapest = false)
        {
            if (!Live() || req == null || req.m_resItem == null) return 1f;

            float mult;
            if (!_matMult.TryGetValue(Tiers.CleanName(req.m_resItem.name), out mult)) return 1f;
            if (mult >= 1f) return 1f;

            if (!cheapest)
            {
                float fraction;
                if (!MaterialInYard(Tiers.CleanName(req.m_resItem.name), out fraction)) return 1f;

                // Lerp towards vanilla for a part-built station; a no-op while fraction is 1.
                if (fraction < 1f) mult = 1f - (1f - mult) * fraction;
            }
            // cheapest: no yard test and no level scaling - the full discount, unconditionally.

            // The skill and rhythm factors are DELIBERATELY not applied here: they belong to the
            // builder, not to the material, so they must reach every piece and not just the ones
            // with a yard entry. TrailingTierDiscountModule.PieceAmount multiplies them in.
            return mult;
        }

        /// <summary>This material's floor override, or -1 for "use the pipeline's normal floor".
        /// A floor of 0 lets the requirement round away and drop off the piece entirely.</summary>
        internal static int FloorFor(Piece.Requirement req)
        {
            if (!Live() || req == null || req.m_resItem == null || _matFloor.Count == 0) return -1;
            int floor;
            return _matFloor.TryGetValue(Tiers.CleanName(req.m_resItem.name), out floor) ? floor : -1;
        }

        // ---- the framing rule -------------------------------------------------------------------

        /// <summary>Is this piece one of the beams/poles the framing rule governs?</summary>
        internal static bool IsStructural(Piece piece)
        {
            if (piece == null || piece.gameObject == null) return false;
            EnsureStructural();
            return _structural.Contains(Utils.GetPrefabName(piece.gameObject));
        }

        /// <summary>
        /// The framing rule's absolute cost for one requirement, or false when it does not apply.
        /// Absolute, not a multiplier: a beam costs what the rule says whatever the yard, the tier
        /// and the settlement would have made of it. The caller clamps it to the vanilla amount.
        /// </summary>
        internal static bool StructuralAmount(Piece piece, Piece.Requirement req, out int amount)
        {
            amount = 0;
            if (!Live() || piece == null || req == null) return false;
            if (!IsStructural(piece)) return false;
            if (_structuralInYardOnly != null && _structuralInYardOnly.Value && !AnyMaterialInYard(piece))
                return false;

            bool attached = AttachedFor(piece);
            amount = attached
                         ? (_structuralAttachedCost != null ? _structuralAttachedCost.Value : 0)
                         : (_structuralFirstCost != null ? _structuralFirstCost.Value : 1);
            if (amount < 0) amount = 0;
            return true;
        }

        private static bool AnyMaterialInYard(Piece piece)
        {
            if (piece.m_resources == null) return false;
            for (int i = 0; i < piece.m_resources.Length; i++)
            {
                var r = piece.m_resources[i];
                if (r == null || r.m_resItem == null) continue;
                float f;
                if (MaterialInYard(Tiers.CleanName(r.m_resItem.name), out f)) return true;
            }
            return false;
        }

        /// <summary>
        /// Is the piece about to be placed snapped onto another structural piece?
        ///
        /// During a placement the answer is FROZEN (see PlacePiecePre): Player.PlacePiece
        /// instantiates the new piece before Player.ConsumeResources runs, and the new piece's snap
        /// points land exactly on the ghost's - so a live test after the instantiate would always
        /// say "attached" and every beam would be free. Outside a placement the answer is the live
        /// test, cached for one frame because the build HUD asks every frame.
        ///
        /// The piece is only ever the SELECTED one: the build menu also calls SetupPieceInfo for
        /// whatever the mouse is hovering, and that piece has no ghost of its own.
        /// </summary>
        private static bool AttachedFor(Piece piece)
        {
            if (_testAttached >= 0) return _testAttached == 1;

            if (_lockedFrame == Time.frameCount && ReferenceEquals(_lockedPiece, piece))
                return _lockedAttached;

            var player = Player.m_localPlayer;
            if (player == null || player.m_buildPieces == null) return false;
            if (!ReferenceEquals(player.m_buildPieces.GetSelectedPiece(), piece)) return false;

            if (_ghostFrame == Time.frameCount) return _ghostAttached;
            _ghostFrame = Time.frameCount;
            _ghostAttached = GhostAttached(player);
            return _ghostAttached;
        }

        /// <summary>
        /// The live snap test. A snap point of the placement ghost within vanilla's own 0.5 m
        /// snap tolerance of a snap point belonging to a STRUCTURAL piece. Uses vanilla's own
        /// overlap (Piece.GetSnapPoints(point, 10 m, ...)), whose collider mask excludes the ghost
        /// layer, so the ghost can never match itself.
        /// </summary>
        private static bool GhostAttached(Player player)
        {
            try
            {
                var ghost = _fiPlacementGhost != null ? _fiPlacementGhost.GetValue(player) as GameObject : null;
                if (ghost == null || !ghost.activeInHierarchy) return false;

                var ghostPiece = ghost.GetComponent<Piece>();
                if (ghostPiece == null) return false;

                _ghostPoints.Clear();
                ghostPiece.GetSnapPoints(_ghostPoints);
                if (_ghostPoints.Count == 0) return false;

                _nearPoints.Clear();
                _nearPieces.Clear();
                Piece.GetSnapPoints(ghost.transform.position, SnapSearchRadius, _nearPoints, _nearPieces);

                for (int i = 0; i < _nearPieces.Count; i++)
                {
                    var other = _nearPieces[i];
                    if (other == null || ReferenceEquals(other, ghostPiece)) continue;
                    if (!IsStructural(other)) continue;

                    _otherPoints.Clear();
                    other.GetSnapPoints(_otherPoints);
                    for (int a = 0; a < _otherPoints.Count; a++)
                    {
                        var pa = _otherPoints[a].position;
                        for (int b = 0; b < _ghostPoints.Count; b++)
                            if (Vector3.Distance(pa, _ghostPoints[b].position) <= SnapTolerance) return true;
                    }
                }
                return false;
            }
            catch (Exception e)
            {
                Log.LogError("[Builders] snap test failed: " + e.Message);
                return false;
            }
        }

        // ---- placement: freeze the decision, then record what was paid ---------------------------

        /// <summary>Runs before the new piece exists. Freezes the framing decision for this frame
        /// and works out what ConsumeResources is about to take, so the ZDO record and the
        /// inventory can never disagree.</summary>
        private static void PlacePiecePre(Piece piece)
        {
            if (!Live() || piece == null) return;
            try
            {
                _lockedPiece = piece;
                _lockedAttached = AttachedFor(piece);
                _lockedSkill = SkillFactor();
                _lockedRhythm = RhythmFactor(piece);
                _lockedFrame = Time.frameCount;

                _pendingPaid.Clear();
                _pendingPrefab = null;
                _pendingFrame = -1;

                // Only structural pieces carry a record: everything else is priced by the
                // multipliers, which the refund path recomputes the same way.
                if (!IsStructural(piece) || !TrailingTierDiscountModule.BuildCostsActive()) return;
                if (piece.m_resources == null) return;

                for (int i = 0; i < piece.m_resources.Length; i++)
                {
                    var r = piece.m_resources[i];
                    if (r == null || r.m_resItem == null || r.m_amount <= 0) continue;
                    _pendingPaid.Add(new KeyValuePair<string, int>(
                        Tiers.CleanName(r.m_resItem.name),
                        TrailingTierDiscountModule.PieceAmount(piece, r, r.m_amount, false)));
                }
                _pendingPrefab = Utils.GetPrefabName(piece.gameObject);
                _pendingFrame = Time.frameCount;
            }
            catch (Exception e)
            {
                Log.LogError("[Builders] placement prefix failed: " + e.Message);
                _pendingFrame = -1;
            }
        }

        /// <summary>
        /// The piece is placed. Bank the practice (Builder XP) and extend the streak (Rhythm).
        /// Both AFTER the price was frozen in the prefix, so this swing of the hammer can never
        /// change what this swing of the hammer costs. Deconstructing reaches neither.
        /// </summary>
        private static void PlacePiecePost(Piece piece)
        {
            if (!Live() || piece == null) return;
            try
            {
                AwardXp(piece);
                RhythmPlaced(piece);
            }
            catch (Exception e)
            {
                Log.LogError("[Builders] post-placement bookkeeping failed: " + e.Message);
            }
        }

        /// <summary>The new piece's ZDO exists and this client owns it (it instantiated it a few
        /// lines earlier in Player.PlacePiece). Write down what was paid.</summary>
        private static void OnPlacedPost(Piece __instance)
        {
            if (_pendingFrame != Time.frameCount || __instance == null) return;
            try
            {
                if (!string.Equals(Utils.GetPrefabName(__instance.gameObject), _pendingPrefab,
                                   StringComparison.OrdinalIgnoreCase)) return;

                var nview = __instance.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid() || !nview.IsOwner()) return;

                var zdo = nview.GetZDO();
                if (zdo == null) return;

                for (int i = 0; i < _pendingPaid.Count; i++)
                    zdo.Set((PaidPrefix + _pendingPaid[i].Key).GetStableHashCode(), _pendingPaid[i].Value);
                zdo.Set(PaidMarkerHash, _pendingPaid.Count);
            }
            catch (Exception e)
            {
                Log.LogError("[Builders] could not record what the piece was paid for: " + e.Message);
            }
            finally
            {
                _pendingFrame = -1;
            }
        }

        /// <summary>
        /// Does this piece carry a paid record? Only then is a tracked refund possible.
        ///
        /// Deliberately NOT gated on <see cref="Live()"/>: the record is a fact about the piece,
        /// not a feature toggle. Switching [Builders] off must not turn a shed full of free beams
        /// into a wood mine - as long as the shared build-cost pipeline is running at all, a piece
        /// that recorded what it was paid gives back exactly that.
        /// </summary>
        internal static bool HasPaidRecord(Piece piece)
        {
            if (_testRecord != null) return true;
            if (piece == null) return false;
            try
            {
                var nview = piece.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid()) return false;
                var zdo = nview.GetZDO();
                return zdo != null && zdo.GetInt(PaidMarkerHash, 0) > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>What this piece was actually paid for, per requirement. 0 when the material is
        /// not in the record - a material that cost nothing was never written.</summary>
        internal static int RecordedAmount(Piece piece, Piece.Requirement req)
        {
            if (req == null || req.m_resItem == null) return 0;
            var name = Tiers.CleanName(req.m_resItem.name);

            if (_testRecord != null)
            {
                int v;
                return _testRecord.TryGetValue(name, out v) ? v : 0;
            }

            try
            {
                var nview = piece.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid()) return 0;
                var zdo = nview.GetZDO();
                if (zdo == null) return 0;
                return Mathf.Max(0, zdo.GetInt((PaidPrefix + name).GetStableHashCode(), 0));
            }
            catch
            {
                return 0;
            }
        }

        // ---- the structural set -------------------------------------------------------------------

        /// <summary>
        /// Walk the hammer's own piece table and keep every piece whose prefab name has a "beam"
        /// or "pole" part, or contains log_26 / log_45 (core wood's 26 and 45 degree beams). Split
        /// on underscores so "piece_maypole" is not mistaken for a pole.
        ///
        /// The name alone is not enough - 1.0 ships piece_dvergr_lantern_pole, a light on a stick -
        /// so the candidate must ALSO sit in one of the build categories the game itself files
        /// structure under (see <see cref="IsBuildingCategory"/>). Furniture and decor never
        /// qualify however they are named. What the name shape found and the category then
        /// rejected is logged, so the filter is visible rather than silent. Finally the manual adds
        /// and removes from [Builders] StructuralPieces apply.
        /// </summary>
        private static void EnsureStructural()
        {
            if (_structuralResolved) return;
            // ObjectDB is not up yet on the main menu, and IsStructural is asked on every snap
            // candidate - so retry at most once a second rather than rebuilding the set per call.
            float now = Time.realtimeSinceStartup;
            if (now - _lastStructuralTry < 1f) return;
            _lastStructuralTry = now;

            var auto = new List<string>();
            var table = HammerPieceTable();
            if (table != null && table.m_pieces != null)
            {
                _rejected.Clear();
                foreach (var go in table.m_pieces)
                {
                    if (go == null) continue;
                    var name = Utils.GetPrefabName(go);
                    if (string.IsNullOrEmpty(name)) continue;
                    var piece = go.GetComponent<Piece>();
                    if (piece == null) continue;
                    if (!LooksStructural(name)) continue;

                    if (!IsBuildingCategory(piece.m_category))
                    {
                        if (!_rejected.Contains(name + " (" + piece.m_category + ")"))
                            _rejected.Add(name + " (" + piece.m_category + ")");
                        continue;
                    }
                    if (!auto.Contains(name)) auto.Add(name);
                }
                auto.Sort(StringComparer.OrdinalIgnoreCase);
                _rejected.Sort(StringComparer.OrdinalIgnoreCase);
                _structuralResolved = true;
            }
            _autoStructural = auto;

            var set = new HashSet<string>(auto, StringComparer.OrdinalIgnoreCase);
            foreach (var add in _structuralAdd) set.Add(add);
            foreach (var rm in _structuralRemove) set.Remove(rm);
            _structural = set;
        }

        /// <summary>
        /// The three build categories a real structural piece can be filed under on 1.0.7:
        /// BuildingWorkbench, BuildingStonecutter and DeepNorth - the last one is new in 1.0 and
        /// is where the Timber (stave_*) and Decorated Timber (stave_deco_*) beams and poles live,
        /// along with the 67-degree variants of the older sets. Everything else (Furniture,
        /// Lighting-style decor, Crafting, food and meads) is not structure however it is named -
        /// which is what keeps piece_dvergr_lantern_pole out of the set.
        /// </summary>
        private static bool IsBuildingCategory(Piece.PieceCategory c)
        {
            return c == Piece.PieceCategory.BuildingWorkbench ||
                   c == Piece.PieceCategory.BuildingStonecutter ||
                   c == Piece.PieceCategory.DeepNorth;
        }

        /// <summary>The name shape that says "this is a beam or a pole".</summary>
        private static bool LooksStructural(string prefabName)
        {
            var lower = prefabName.ToLowerInvariant();
            if (lower.Contains("log_26") || lower.Contains("log_45")) return true;
            foreach (var part in lower.Split('_'))
                if (part.StartsWith("beam", StringComparison.Ordinal) ||
                    part.StartsWith("pole", StringComparison.Ordinal)) return true;
            return false;
        }

        private static PieceTable HammerPieceTable()
        {
            var odb = ObjectDB.instance;
            if (odb == null) return null;
            GameObject hammer = null;
            try { hammer = odb.GetItemPrefab("Hammer"); }
            catch { }
            if (hammer == null) return null;
            var drop = hammer.GetComponent<ItemDrop>();
            if (drop == null || drop.m_itemData == null || drop.m_itemData.m_shared == null) return null;
            return drop.m_itemData.m_shared.m_buildPieces;
        }

        /// <summary>How far a station can be upgraded, derived from the game's own extensions.
        /// Only used by StationLevelScaling; a station nothing extends is always at full.</summary>
        private static void EnsureStationFullLevels()
        {
            if (_fullLevelsResolved) return;
            var scene = ZNetScene.instance;
            if (scene == null || scene.m_prefabs == null) return;
            _fullLevelsResolved = true;

            // station m_name (the localised token the game itself matches extensions on) -> count
            var byName = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var go in scene.m_prefabs)
            {
                if (go == null) continue;
                var ext = go.GetComponent<StationExtension>();
                if (ext == null || ext.m_craftingStation == null) continue;
                var key = ext.m_craftingStation.m_name ?? "";
                int n;
                byName[key] = byName.TryGetValue(key, out n) ? n + 1 : 1;
            }

            var levels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _stationNames.Count; i++)
            {
                var prefab = _stationNames[i];
                var go = scene.GetPrefab(prefab.GetStableHashCode());
                var cs = go != null ? go.GetComponentInChildren<CraftingStation>(true) : null;
                int n = 0;
                if (cs != null) byName.TryGetValue(cs.m_name ?? "", out n);
                levels[prefab] = 1 + n;
            }
            _stationFullLevel = levels;
        }

        // ---- the tooltip ---------------------------------------------------------------------------

        /// <summary>
        /// One short line under the piece's description saying why it costs what it costs. Display
        /// only, and deliberately additive: 1.0 reworked the build panel, so rather than trying to
        /// add a row to m_requirementItems (whose length is fixed by the prefab and whose last slot
        /// vanilla uses for the crafting-station row) this appends to the description TMP_Text that
        /// SetupPieceInfo has just rewritten. If that field is missing the line is simply not
        /// drawn - never a broken layout.
        /// </summary>
        private static void PieceInfoPost(Hud __instance, Piece piece)
        {
            if (!Live() || piece == null || __instance == null) return;
            if (_showTooltip == null || !_showTooltip.Value) return;
            if (!TrailingTierDiscountModule.BuildCostsActive()) return;
            try
            {
                var label = __instance.m_pieceDescription;
                if (label == null) return;

                var line = TooltipLine(piece);
                if (string.IsNullOrEmpty(line)) return;

                var current = label.text;
                label.text = string.IsNullOrEmpty(current) ? line : current + "\n" + line;
            }
            catch (Exception e)
            {
                Log.LogWarning("[Builders] tooltip line skipped: " + e.Message);
            }
        }

        /// <summary>
        /// The breakdown, or null when nothing about this piece is discounted. One line, built as
        /// segments joined with a middle dot, so a piece can read
        /// "Yard -50% Wood, -90% Iron · Builder Lv 31 -9% · Rhythm x4 -20%".
        /// </summary>
        internal static string TooltipLine(Piece piece)
        {
            var segs = new List<string>();

            int flat;
            if (StructuralAmount(piece, FirstRequirement(piece), out flat))
            {
                segs.Add("Framing: " + (AttachedFor(piece) ? "attached -> " : "free-standing -> ") +
                         (flat == 0 ? "free" : flat + " each"));
            }
            else if (piece.m_resources != null)
            {
                var parts = new List<string>();
                for (int i = 0; i < piece.m_resources.Length; i++)
                {
                    var r = piece.m_resources[i];
                    if (r == null || r.m_resItem == null || r.m_amount <= 0) continue;
                    float f = MaterialFactor(piece, r);
                    if (f >= 1f) continue;
                    parts.Add("-" + Mathf.RoundToInt((1f - f) * 100f) + "% " +
                              Tiers.CleanName(r.m_resItem.name));
                }
                if (parts.Count > 0) segs.Add("Yard " + string.Join(", ", parts.ToArray()));
            }

            ExtraTooltipSegments(piece, segs);
            if (segs.Count == 0) return null;
            return "<color=#87d37c>" + string.Join(" · ", segs.ToArray()) + "</color>";
        }

        private static Piece.Requirement FirstRequirement(Piece piece)
        {
            if (piece == null || piece.m_resources == null) return null;
            for (int i = 0; i < piece.m_resources.Length; i++)
                if (piece.m_resources[i] != null && piece.m_resources[i].m_resItem != null)
                    return piece.m_resources[i];
            return null;
        }

        // ---- reporting -------------------------------------------------------------------------------

        private string Numbers()
        {
            EnsureStructural();
            return "YardRadius=" + (_yardRadius != null ? _yardRadius.Value : 0f).ToString("0.#", CultureInfo.InvariantCulture) + "m" +
                   " materials=" + _matMult.Count +
                   " stations=[" + string.Join(",", _stationNames.ToArray()) + "]" +
                   " levelScaling=" + (_stationLevelScaling != null && _stationLevelScaling.Value) +
                   " floors=" + (_matFloor.Count == 0 ? "default" : FloorsText()) +
                   " framing=" + (_structuralFirstCost != null ? _structuralFirstCost.Value : 1) + "/" +
                   (_structuralAttachedCost != null ? _structuralAttachedCost.Value : 0) +
                   (_structuralInYardOnly != null && _structuralInYardOnly.Value ? " (yard only)" : "") +
                   " structural=" + _structural.Count + (_structuralResolved ? "" : " (not resolved yet)") +
                   " skill=" + (_skillEnabled != null && _skillEnabled.Value
                                    ? "max -" + Mathf.RoundToInt((_skillMaxDiscount != null ? _skillMaxDiscount.Value : 0f) * 100f) +
                                      "% @Lv100, " + (_skillXpPerMaterial != null ? _skillXpPerMaterial.Value : 0f)
                                          .ToString("0.##", CultureInfo.InvariantCulture) + " xp/material"
                                    : "off") +
                   " rhythm=" + (_rhythmEnabled != null && _rhythmEnabled.Value
                                     ? "-" + Mathf.RoundToInt((_rhythmPerRepeat != null ? _rhythmPerRepeat.Value : 0f) * 100f) +
                                       "%/repeat to -" + Mathf.RoundToInt(RhythmMaxRaw() * 100f) +
                                       "% in " + (_rhythmWindowSec != null ? _rhythmWindowSec.Value : 0f)
                                           .ToString("0.#", CultureInfo.InvariantCulture) + "s"
                                     : "off") +
                   (Live() ? "" : " [inactive on this half]");
        }

        /// <summary>nvlb.status gets the settings line plus this character's own progress.</summary>
        private static string LocalLine()
        {
            if (!ClientActive() || Player.m_localPlayer == null) return null;
            var rec = BuilderSkill.Get(Player.m_localPlayer);
            float f = SkillFactor();
            return "Builder " + BuilderSkill.Describe(rec) +
                   (f < 1f ? " -" + Mathf.RoundToInt((1f - f) * 100f) + "%" : " (no discount)") +
                   "; rhythm " + (_rhythmCount > 0 && Now() - _rhythmLast <=
                                  (_rhythmWindowSec != null ? _rhythmWindowSec.Value : 0f)
                                      ? "x" + _rhythmCount + " on " + _rhythmPrefab
                                      : "idle");
        }

        // ---- nvlb.builder --------------------------------------------------------------------------

        /// <summary>
        /// <c>nvlb.builder</c> prints this character's Builder level, the XP into the next level,
        /// what that is worth as a discount, and the rhythm streak. Read-only on purpose: there is
        /// no console setter, because a skill that can be typed in is not a skill.
        /// </summary>
        private static void RegisterCommand()
        {
            if (_commandRegistered) return;
            _commandRegistered = true;
            try
            {
                new Terminal.ConsoleCommand("nvlb.builder",
                    "Show this character's Builder level, XP into the next level, the build " +
                    "discount it is worth, and the current placement rhythm.",
                    new Terminal.ConsoleEvent(BuilderCommand));
                Log.LogInfo("[Builders] console command 'nvlb.builder' registered");
            }
            catch (Exception e)
            {
                _commandRegistered = false;
                Log.LogError("[Builders] could not register nvlb.builder: " + e);
            }
        }

        private static void BuilderCommand(Terminal.ConsoleEventArgs args)
        {
            string msg;
            if (!Live()) msg = "the Builders module is not active on this half";
            else if (Player.m_localPlayer == null) msg = "no local player";
            else
            {
                msg = LocalLine();
                if (_skillEnabled != null && !_skillEnabled.Value)
                    msg += "  [SkillEnabled=false: XP is not being recorded]";
                msg += "  xp for the selected piece: ";
                var sel = Player.m_localPlayer.m_buildPieces != null
                              ? Player.m_localPlayer.m_buildPieces.GetSelectedPiece() : null;
                msg += sel == null ? "(nothing selected)"
                                   : XpFor(sel).ToString("0.##", CultureInfo.InvariantCulture) +
                                     " (" + Utils.GetPrefabName(sel.gameObject) + ")";
            }
            if (args != null && args.Context != null) args.Context.AddString("[Builders] " + msg);
            Log.LogInfo("[Builders] " + msg);
        }

        private static string FloorsText()
        {
            var parts = new List<string>();
            foreach (var kv in _matFloor) parts.Add(kv.Key + ":" + kv.Value);
            parts.Sort(StringComparer.OrdinalIgnoreCase);
            return string.Join(",", parts.ToArray());
        }

        public override string StatusDetail()
        {
            var local = LocalLine();
            return Numbers() + (string.IsNullOrEmpty(local) ? "" : "  " + local);
        }

        private static void WorldReady()
        {
            if (_reported || _self == null || !_self.Active) return;
            _reported = true;
            try
            {
                EnsureStructural();
                EnsureStationFullLevels();
                foreach (var line in Report().Split('\n')) Log.LogInfo(line);
                if (_selfTest != null && _selfTest.Value)
                    foreach (var line in SelfTest().Split('\n')) Log.LogInfo(line);
            }
            catch (Exception e)
            {
                Log.LogError("[BuildersGuild] world-ready report failed: " + e);
            }
        }

        /// <summary>The proof line: the settings, the resolved names, and the discovered set.</summary>
        internal static string Report()
        {
            var sb = new StringBuilder();
            sb.Append("[BuildersGuild] ").Append(_self != null ? _self.Numbers() : "(not configured)");

            var odb = ObjectDB.instance;
            var scene = ZNetScene.instance;

            // materials
            var missingMat = new List<string>();
            var noStation = new List<string>();
            foreach (var kv in _matMult)
            {
                GameObject go = null;
                if (odb != null) { try { go = odb.GetItemPrefab(kv.Key); } catch { } }
                if (go == null) missingMat.Add(kv.Key);
                if (!_homeStations.ContainsKey(kv.Key)) noStation.Add(kv.Key);
            }
            sb.Append("\n  materials: ");
            var mats = new List<string>();
            foreach (var kv in _matMult)
            {
                List<string> st;
                _homeStations.TryGetValue(kv.Key, out st);
                mats.Add(kv.Key + " x" + kv.Value.ToString("0.##", CultureInfo.InvariantCulture) +
                         " @" + (st == null ? "(no station!)" : string.Join("+", st.ToArray())));
            }
            mats.Sort(StringComparer.OrdinalIgnoreCase);
            sb.Append(string.Join(", ", mats.ToArray()));
            if (missingMat.Count > 0)
                sb.Append("\n  materials NOT in ObjectDB (never matched): ")
                  .Append(string.Join(", ", missingMat.ToArray()));
            if (noStation.Count > 0)
                sb.Append("\n  materials with no home station (never discounted): ")
                  .Append(string.Join(", ", noStation.ToArray()));

            // stations
            sb.Append("\n  stations:");
            foreach (var prefab in _stationNames)
            {
                var go = scene != null ? scene.GetPrefab(prefab.GetStableHashCode()) : null;
                var cs = go != null ? go.GetComponentInChildren<CraftingStation>(true) : null;
                int full;
                _stationFullLevel.TryGetValue(prefab, out full);
                sb.Append("\n    ").Append(prefab).Append(": ")
                  .Append(cs != null ? "CraftingStation ok" : (go != null ? "NOT a CraftingStation" : "MISSING from ZNetScene"))
                  .Append(", full at level ").Append(full <= 0 ? 1 : full)
                  .Append(full > 1 ? " (" + (full - 1) + " extension prefabs)" : " (no extensions - always full)");
            }

            // structural set
            sb.Append("\n  structural set (").Append(_structural.Count).Append(" pieces, ")
              .Append(_autoStructural.Count).Append(" auto-discovered from the Hammer's piece table")
              .Append(_structuralAdd.Count > 0 ? ", +" + _structuralAdd.Count + " added" : "")
              .Append(_structuralRemove.Count > 0 ? ", -" + _structuralRemove.Count + " removed" : "")
              .Append("):");
            var sorted = new List<string>(_structural);
            sorted.Sort(StringComparer.OrdinalIgnoreCase);
            var lineBuf = new StringBuilder();
            foreach (var n in sorted)
            {
                if (lineBuf.Length > 90) { sb.Append("\n    ").Append(lineBuf); lineBuf.Length = 0; }
                if (lineBuf.Length > 0) lineBuf.Append(", ");
                lineBuf.Append(n);
            }
            if (lineBuf.Length > 0) sb.Append("\n    ").Append(lineBuf);
            if (_structural.Count == 0)
                sb.Append("\n    (empty - the Hammer's piece table was not readable; framing is inert)");
            if (_rejected.Count > 0)
                sb.Append("\n    beam/pole-shaped but not a building category, so NOT structural: ")
                  .Append(string.Join(", ", _rejected.ToArray()));

            return sb.ToString();
        }

        // ---- headless self-test -----------------------------------------------------------------------

        /// <summary>
        /// Proves the four decisions this module owns, without placing anything: the yard gate,
        /// the framing rule, the per-material floor and the tracked refund. Every check runs
        /// through the SHARED pricing function TrailingTierDiscountModule uses at runtime, so what
        /// passes here is the same arithmetic the game will do - not a copy of it.
        ///
        /// The station scan, the snap test and the ZDO are the three things a headless server has
        /// no way to exercise (no player, no ghost, no placed piece), so each is replaced by an
        /// explicit override for the duration of the run and cleared in a finally.
        /// </summary>
        internal static string SelfTest()
        {
            var sb = new StringBuilder();
            int pass = 0, fail = 0;

            Action<bool, string> check = (ok, what) =>
            {
                if (ok) { pass++; sb.Append("\n  PASS  ").Append(what); }
                else { fail++; sb.Append("\n  FAIL  ").Append(what); }
            };

            sb.Append("[Builders] SelfTest: --- begin ---");

            var savedFloors = _matFloor;
            try
            {
                _testLive = true;
                sb.Append("\n  ").Append(_self != null ? _self.Numbers() : "(not configured)");
                EnsureStructural();

                var scene = ZNetScene.instance;
                if (scene == null)
                {
                    sb.Append("\n  ZNetScene has no prefabs - cannot price anything");
                    sb.Append("\n[Builders] SelfTest: 0 passed, 1 FAILED");
                    return sb.ToString();
                }

                // ---- (1) a stone piece, stonecutter in range vs out of range ------------------
                var stone = FindPiece(scene, "stone_wall_2x1", "stone_wall_1x1", "stone_wall_4x2",
                                      "piece_stonecutter");
                if (stone == null)
                {
                    sb.Append("\n  no stone build piece in this build - yard check skipped");
                }
                else
                {
                    var req = FirstRequirement(stone);
                    int vanilla = req.m_amount;

                    _testStationsInRange = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    int far = TrailingTierDiscountModule.PieceAmount(stone, req, vanilla, false);

                    _testStationsInRange = new HashSet<string>(
                        new[] { "piece_stonecutter" }, StringComparer.OrdinalIgnoreCase);
                    int near = TrailingTierDiscountModule.PieceAmount(stone, req, vanilla, false);

                    float yard = MaterialFactor(stone, req);
                    float others = TrailingTierDiscountModule.PieceCostFactor(stone) *
                                   ExtraFactorFor(stone, req, false);
                    int expected = TrailingTierDiscountModule.ScaledAmount(vanilla, others * yard, FloorFor(req));

                    sb.Append("\n  ").Append(Utils.GetPrefabName(stone.gameObject)).Append(' ')
                      .Append(Tiers.CleanName(req.m_resItem.name)).Append(": vanilla ").Append(vanilla)
                      .Append(", outside the yard ").Append(far).Append(", inside ").Append(near)
                      .Append(" (yard x").Append(yard.ToString("0.###", CultureInfo.InvariantCulture))
                      .Append(" * tier/settlement x").Append(others.ToString("0.###", CultureInfo.InvariantCulture))
                      .Append(")");

                    check(near <= far, "a stone piece costs no more inside the yard than outside it");
                    check(near == expected,
                          "the yard, tier and settlement factors compose into ONE rounding (" +
                          vanilla + " -> " + near + ")");
                    check(far == TrailingTierDiscountModule.ScaledAmount(
                              vanilla, others, FloorFor(req)),
                          "outside the yard the material multiplier is a flat 1.0");

                    // The refund rule (stage 1b). Buy it in the yard, then tear it down with the
                    // stonecutter gone: without the "cheapest factor" rule this refunds `far` and
                    // doubles the stone every time.
                    _testStationsInRange = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    int refundOutside = TrailingTierDiscountModule.PieceAmount(stone, req, vanilla, true);
                    _testStationsInRange = new HashSet<string>(
                        new[] { "piece_stonecutter" }, StringComparer.OrdinalIgnoreCase);
                    int refundInside = TrailingTierDiscountModule.PieceAmount(stone, req, vanilla, true);

                    sb.Append("\n  refund of a yard-built ").Append(Utils.GetPrefabName(stone.gameObject))
                      .Append(" (paid ").Append(near).Append("): torn down inside the yard ")
                      .Append(refundInside).Append(", outside it ").Append(refundOutside)
                      .Append(" - transient factors refund at their cheapest");
                    check(refundOutside <= near,
                          "a yard-built piece torn down OUTSIDE the yard refunds no more than it cost (" +
                          refundOutside + " <= " + near + ")");
                    check(refundInside == refundOutside,
                          "where you stand cannot change a refund");
                    check(refundOutside < far,
                          "the old exploit is closed: the refund is not the un-discounted price (" +
                          refundOutside + " < " + far + ")");
                }

                // ---- (2) the framing rule: free-standing vs attached ---------------------------
                var beam = FindStructuralSample(scene, "Iron");
                string beamNote = beam == null ? "no iron beam/pole in the structural set" : null;
                if (beam == null) beam = FindStructuralSample(scene, null);

                if (beam == null)
                {
                    sb.Append("\n  the structural set is empty - framing checks skipped");
                    fail++;
                }
                else
                {
                    _testStationsInRange = new HashSet<string>(
                        new[] { "forge", "piece_workbench" }, StringComparer.OrdinalIgnoreCase);
                    var name = Utils.GetPrefabName(beam.gameObject);
                    if (beamNote != null) sb.Append("\n  ").Append(beamNote).Append(" - using ").Append(name);

                    _testAttached = 0;
                    var free = PriceAll(beam);
                    _testAttached = 1;
                    var attached = PriceAll(beam);
                    _testAttached = -1;

                    sb.Append("\n  ").Append(name).Append(": free-standing ").Append(Describe(beam, free))
                      .Append("  |  snapped onto another beam ").Append(Describe(beam, attached));

                    int first = _structuralFirstCost.Value, later = _structuralAttachedCost.Value;
                    check(AllEqualOrCapped(beam, free, first),
                          "a free-standing beam costs " + first + " of each material");
                    check(AllEqualOrCapped(beam, attached, later),
                          "a beam snapped onto another beam costs " + later + " of each material");

                    // ---- (3) tracked refunds ---------------------------------------------------
                    _testRecord = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < beam.m_resources.Length; i++)
                    {
                        var r = beam.m_resources[i];
                        if (r == null || r.m_resItem == null) continue;
                        _testRecord[Tiers.CleanName(r.m_resItem.name)] = later;
                    }
                    var refundAttached = PriceAll(beam, true);
                    for (int i = 0; i < beam.m_resources.Length; i++)
                    {
                        var r = beam.m_resources[i];
                        if (r == null || r.m_resItem == null) continue;
                        _testRecord[Tiers.CleanName(r.m_resItem.name)] = first;
                    }
                    var refundRoot = PriceAll(beam, true);
                    _testRecord = null;

                    sb.Append("\n  refunds from the ZDO record: attached beam ")
                      .Append(Describe(beam, refundAttached)).Append("  |  paid root ")
                      .Append(Describe(beam, refundRoot));
                    check(AllEqual(refundAttached, later),
                          "an attached beam refunds exactly what it was paid for (" + later + ")");
                    check(AllEqual(refundRoot, first),
                          "the paid root of a chain refunds exactly what it was paid for (" + first + ")");
                    check(NoneExceeds(refundAttached, attached) && NoneExceeds(refundRoot, free),
                          "a refund never exceeds what was paid");

                    // ---- (4) a NON-structural piece snapped to a beam still pays ---------------
                    var wall = FindPiece(scene, "wood_wall", "woodwall", "wood_floor", "wood_door");
                    if (wall == null)
                    {
                        sb.Append("\n  no plain wooden piece in this build - the \"walls still pay\" check was skipped");
                    }
                    else
                    {
                        _testAttached = 1;
                        var wallReq = FirstRequirement(wall);
                        int paid = TrailingTierDiscountModule.PieceAmount(wall, wallReq, wallReq.m_amount, false);
                        int want = TrailingTierDiscountModule.ScaledAmount(
                            wallReq.m_amount,
                            TrailingTierDiscountModule.PieceCostFactor(wall) * MaterialFactor(wall, wallReq) *
                                ExtraFactorFor(wall, wallReq, false),
                            FloorFor(wallReq));
                        _testAttached = -1;
                        sb.Append("\n  ").Append(Utils.GetPrefabName(wall.gameObject))
                          .Append(" snapped to a beam: ").Append(Tiers.CleanName(wallReq.m_resItem.name))
                          .Append(' ').Append(wallReq.m_amount).Append(" -> ").Append(paid);
                        check(!IsStructural(wall), Utils.GetPrefabName(wall.gameObject) +
                              " is not in the structural set");
                        check(paid == want && paid > 0,
                              "a non-structural piece snapped to a beam still pays the normal price");
                    }

                    // ---- (5) MinAmounts Iron:0 drops the requirement ---------------------------
                    var ironReq = RequirementFor(beam, "Iron") ?? FirstRequirement(beam);
                    var matName = Tiers.CleanName(ironReq.m_resItem.name);
                    var zeroFloor = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    zeroFloor[matName] = 0;
                    _matFloor = zeroFloor;
                    _testStationsInRange = new HashSet<string>(
                        new[] { "forge", "piece_workbench", "piece_stonecutter", "blackforge" },
                        StringComparer.OrdinalIgnoreCase);

                    // The framing rule would override the multipliers, so this check prices the
                    // material the way a NON-structural piece would: the floor is what is on test.
                    float mult = MaterialFactor(beam, ironReq) *
                                 TrailingTierDiscountModule.PieceCostFactor(beam) *
                                 ExtraFactorFor(beam, ironReq, false);
                    int floored = TrailingTierDiscountModule.ScaledAmount(ironReq.m_amount, mult, FloorFor(ironReq));
                    int normal = TrailingTierDiscountModule.ScaledAmount(ironReq.m_amount, mult, -1);
                    sb.Append("\n  MinAmounts ").Append(matName).Append(":0 -> ")
                      .Append(ironReq.m_amount).Append(" x")
                      .Append(mult.ToString("0.###", CultureInfo.InvariantCulture))
                      .Append(" = ").Append(floored)
                      .Append(" (with the normal floor of 1: ").Append(normal).Append(')');
                    check(FloorFor(ironReq) == 0, "MinAmounts gives " + matName + " a floor of 0");
                    check(floored <= normal,
                          "a floor of 0 lets " + matName + " round below the normal floor" +
                          (floored == 0 ? " and drop off the piece entirely" : ""));
                    _matFloor = savedFloors;
                }

                SkillSelfTest(sb, check);
                RhythmSelfTest(sb, check, scene);
            }
            catch (Exception e)
            {
                fail++;
                sb.Append("\n  EXCEPTION: ").Append(e);
            }
            finally
            {
                _matFloor = savedFloors;
                _testStationsInRange = null;
                _testAttached = -1;
                _testRecord = null;
                _testSkillLevel = -1f;
                _testNow = -1f;
                RhythmReset();
                _testLive = false;
            }

            sb.Append("\n[Builders] SelfTest: ").Append(pass).Append(" passed, ").Append(fail).Append(" FAILED");
            return sb.ToString();
        }

        /// <summary>Stage 2: the record, the curve and the discount, all of which are pure.</summary>
        private static void SkillSelfTest(StringBuilder sb, Action<bool, string> check)
        {
            float max = _skillMaxDiscount != null ? _skillMaxDiscount.Value : 0.30f;
            sb.Append("\n  --- Builder skill ---")
              .Append("\n  SkillEnabled=").Append(_skillEnabled != null && _skillEnabled.Value)
              .Append(" SkillXpPerMaterial=")
              .Append((_skillXpPerMaterial != null ? _skillXpPerMaterial.Value : 0f).ToString("0.##", CultureInfo.InvariantCulture))
              .Append(" SkillMaxDiscount=").Append(max.ToString("0.##", CultureInfo.InvariantCulture));

            // (a) XP accrues and levels the character up
            var rec = new BuilderSkill.Record(0f, 0f);
            int gained, totalGained = 0;
            for (int i = 0; i < 200; i++)
            {
                rec = BuilderSkill.Raise(rec, 4f, out gained);   // a 4-stone wall
                totalGained += gained;
            }
            sb.Append("\n  200 x 4-material pieces from scratch -> ").Append(BuilderSkill.Describe(rec));
            check(rec.Level > 0f && totalGained > 0, "Builder XP accrues and raises the level");

            var noXp = BuilderSkill.Raise(new BuilderSkill.Record(3f, 1f), 0f, out gained);
            check(noXp.Level == 3f && noXp.Accumulator == 1f && gained == 0,
                  "zero XP changes nothing (deconstructing earns nothing)");

            // (b) the curve is monotonic all the way to 100 and stops there
            bool monotonic = true;
            float prev = -1f;
            for (int lv = 0; lv < 100; lv++)
            {
                float need = BuilderSkill.NextLevelRequirement(lv);
                if (need <= prev) { monotonic = false; break; }
                prev = need;
            }
            sb.Append("\n  curve: Lv0->1 needs ")
              .Append(BuilderSkill.NextLevelRequirement(0f).ToString("0.##", CultureInfo.InvariantCulture))
              .Append(" xp, Lv49->50 ")
              .Append(BuilderSkill.NextLevelRequirement(49f).ToString("0.##", CultureInfo.InvariantCulture))
              .Append(", Lv99->100 ")
              .Append(BuilderSkill.NextLevelRequirement(99f).ToString("0.##", CultureInfo.InvariantCulture))
              .Append("  (vanilla pow(level+1,1.5)*0.5+0.5)");
            check(monotonic, "the level curve is strictly increasing from 0 to 100");

            var capped = BuilderSkill.Raise(new BuilderSkill.Record(100f, 0f), 9999f, out gained);
            check(capped.Level == 100f && gained == 0, "level 100 is the ceiling");

            // (c) the discount is linear from 1.0 to 1 - SkillMaxDiscount
            float f0 = BuilderSkill.FactorFor(0f, max);
            float f50 = BuilderSkill.FactorFor(50f, max);
            float f100 = BuilderSkill.FactorFor(100f, max);
            sb.Append("\n  discount: Lv0 x").Append(f0.ToString("0.###", CultureInfo.InvariantCulture))
              .Append("  Lv50 x").Append(f50.ToString("0.###", CultureInfo.InvariantCulture))
              .Append("  Lv100 x").Append(f100.ToString("0.###", CultureInfo.InvariantCulture));
            check(Mathf.Abs(f0 - 1f) < 0.0001f, "the Builder factor at Lv 0 is exactly 1.0");
            check(Mathf.Abs(f100 - (1f - max)) < 0.0001f,
                  "the Builder factor at Lv 100 is exactly " +
                  (1f - max).ToString("0.00", CultureInfo.InvariantCulture));
            check(Mathf.Abs(f50 - (1f - max * 0.5f)) < 0.0001f, "and linear in between (Lv 50 = half of it)");

            // (d) the record survives the trip through the custom-data string
            var orig = new BuilderSkill.Record(37f, 12.5f);
            var wire = BuilderSkill.Encode(orig);
            var back = BuilderSkill.Decode(wire);
            sb.Append("\n  record round-trip: \"").Append(wire).Append("\" -> ")
              .Append(BuilderSkill.Describe(back));
            check(back.Level == orig.Level && Mathf.Abs(back.Accumulator - orig.Accumulator) < 0.001f &&
                  BuilderSkill.Encode(back) == wire,
                  "the record round-trips through Player.m_customData[\"" + BuilderSkill.Key + "\"]");
            check(BuilderSkill.Decode("").Level == 0f &&
                  BuilderSkill.Decode("not a record").Level == 0f &&
                  BuilderSkill.Decode("9|1|2").Level == 0f,
                  "a missing, corrupt or future-version record decodes to a fresh Lv 0 instead of throwing");
        }

        /// <summary>Stage 3: the streak grows, caps, and resets on a prefab change and a timeout.</summary>
        private static void RhythmSelfTest(StringBuilder sb, Action<bool, string> check, ZNetScene scene)
        {
            sb.Append("\n  --- Rhythm ---")
              .Append("\n  RhythmEnabled=").Append(_rhythmEnabled != null && _rhythmEnabled.Value)
              .Append(" RhythmWindowSec=")
              .Append((_rhythmWindowSec != null ? _rhythmWindowSec.Value : 0f).ToString("0.#", CultureInfo.InvariantCulture))
              .Append(" RhythmPerRepeat=")
              .Append((_rhythmPerRepeat != null ? _rhythmPerRepeat.Value : 0f).ToString("0.##", CultureInfo.InvariantCulture))
              .Append(" RhythmMax=")
              .Append(RhythmMaxClamped().ToString("0.##", CultureInfo.InvariantCulture));

            var a = FindPiece(scene, "wood_wall", "woodwall", "wood_floor");
            var b = FindPiece(scene, "wood_floor", "wood_door", "wood_stair", "wood_pole");
            if (a == null || b == null || ReferenceEquals(a, b))
            {
                sb.Append("\n  need two different wooden pieces in this build - rhythm checks skipped");
                check(false, "two sample pieces for the rhythm checks");
                return;
            }

            float window = _rhythmWindowSec != null ? _rhythmWindowSec.Value : 20f;
            float per = _rhythmPerRepeat != null ? _rhythmPerRepeat.Value : 0.05f;

            _testNow = 1000f;
            RhythmReset();

            check(RhythmStreak(a) == 0 && Mathf.Abs(RhythmFactor(a) - 1f) < 0.0001f,
                  "no streak before the first placement (factor 1.0)");

            for (int i = 0; i < 3; i++) { RhythmPlaced(a); _testNow += 1f; }
            int s3 = RhythmStreak(a);
            float f3 = RhythmFactor(a);
            sb.Append("\n  3 x ").Append(Utils.GetPrefabName(a.gameObject)).Append(" in a row -> x")
              .Append(s3).Append(" factor ").Append(f3.ToString("0.###", CultureInfo.InvariantCulture));
            check(s3 == 3 && Mathf.Abs(f3 - (1f - 3f * per)) < 0.0001f,
                  "the streak grows one per repeat and is worth " +
                  Mathf.RoundToInt(per * 100f) + "% each");

            for (int i = 0; i < 12; i++) { RhythmPlaced(a); _testNow += 1f; }
            float fCap = RhythmFactor(a);
            sb.Append("\n  15 in a row -> x").Append(RhythmStreak(a)).Append(" factor ")
              .Append(fCap.ToString("0.###", CultureInfo.InvariantCulture))
              .Append(" (capped at -").Append(Mathf.RoundToInt(RhythmMaxClamped() * 100f)).Append("%)");
            check(Mathf.Abs(fCap - (1f - RhythmMaxClamped())) < 0.0001f,
                  "the streak discount caps at RhythmMax");

            check(RhythmStreak(b) == 0 && Mathf.Abs(RhythmFactor(b) - 1f) < 0.0001f,
                  "a different piece (" + Utils.GetPrefabName(b.gameObject) + ") has no streak of its own");
            RhythmPlaced(b);
            check(RhythmStreak(a) == 0 && RhythmStreak(b) == 1,
                  "switching piece RESETS the streak instead of carrying it over");

            _testNow += window + 1f;
            sb.Append("\n  after a ").Append((window + 1f).ToString("0.#", CultureInfo.InvariantCulture))
              .Append("s pause (window ").Append(window.ToString("0.#", CultureInfo.InvariantCulture))
              .Append("s) -> x").Append(RhythmStreak(b));
            check(RhythmStreak(b) == 0 && Mathf.Abs(RhythmFactor(b) - 1f) < 0.0001f,
                  "a pause longer than RhythmWindowSec resets the streak");

            // The refund rule with a live streak: buy at no streak, refund at full rhythm.
            RhythmReset();
            _testStationsInRange = new HashSet<string>(
                new[] { "piece_workbench" }, StringComparer.OrdinalIgnoreCase);
            var req = FirstRequirement(a);
            int paidNoStreak = TrailingTierDiscountModule.PieceAmount(a, req, req.m_amount, false);
            for (int i = 0; i < 10; i++) { RhythmPlaced(a); _testNow += 1f; }
            int paidFullStreak = TrailingTierDiscountModule.PieceAmount(a, req, req.m_amount, false);
            int refund = TrailingTierDiscountModule.PieceAmount(a, req, req.m_amount, true);
            sb.Append("\n  ").Append(Utils.GetPrefabName(a.gameObject)).Append(' ')
              .Append(Tiers.CleanName(req.m_resItem.name)).Append(": paid with no streak ")
              .Append(paidNoStreak).Append(", at full streak ").Append(paidFullStreak)
              .Append(", refund ").Append(refund).Append(" (refunds assume full rhythm)");
            check(refund <= paidNoStreak && refund <= paidFullStreak,
                  "a refund never exceeds what was paid, at any point in a streak");
            RhythmReset();
            _testNow = -1f;
        }

        private static int[] PriceAll(Piece piece, bool forRefund = false)
        {
            var outp = new int[piece.m_resources.Length];
            for (int i = 0; i < piece.m_resources.Length; i++)
            {
                var r = piece.m_resources[i];
                if (r == null || r.m_resItem == null) { outp[i] = 0; continue; }
                outp[i] = TrailingTierDiscountModule.PieceAmount(piece, r, r.m_amount, forRefund);
            }
            return outp;
        }

        private static string Describe(Piece piece, int[] amounts)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < amounts.Length; i++)
            {
                var r = piece.m_resources[i];
                if (r == null || r.m_resItem == null) continue;
                if (sb.Length > 0) sb.Append(" + ");
                sb.Append(amounts[i]).Append(' ').Append(Tiers.CleanName(r.m_resItem.name))
                  .Append(" (of ").Append(r.m_amount).Append(')');
            }
            return sb.ToString();
        }

        private static bool AllEqual(int[] amounts, int want)
        {
            for (int i = 0; i < amounts.Length; i++) if (amounts[i] != want) return false;
            return amounts.Length > 0;
        }

        /// <summary>The flat cost, except where vanilla already asks for less than the flat cost.</summary>
        private static bool AllEqualOrCapped(Piece piece, int[] amounts, int want)
        {
            for (int i = 0; i < amounts.Length; i++)
            {
                var r = piece.m_resources[i];
                if (r == null || r.m_resItem == null) continue;
                if (amounts[i] != Mathf.Min(want, r.m_amount)) return false;
            }
            return amounts.Length > 0;
        }

        private static bool NoneExceeds(int[] refund, int[] paid)
        {
            for (int i = 0; i < refund.Length && i < paid.Length; i++)
                if (refund[i] > paid[i]) return false;
            return true;
        }

        private static Piece FindPiece(ZNetScene scene, params string[] candidates)
        {
            foreach (var want in candidates)
            {
                var go = scene.GetPrefab(want.GetStableHashCode());
                var piece = go == null ? null : go.GetComponent<Piece>();
                if (piece != null && piece.m_resources != null && piece.m_resources.Length > 0 &&
                    FirstRequirement(piece) != null) return piece;
            }
            return null;
        }

        /// <summary>The first structural piece that costs the named material (any, if null).</summary>
        private static Piece FindStructuralSample(ZNetScene scene, string material)
        {
            var names = new List<string>(_structural);
            names.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var n in names)
            {
                var go = scene.GetPrefab(n.GetStableHashCode());
                var piece = go == null ? null : go.GetComponent<Piece>();
                if (piece == null || piece.m_resources == null || piece.m_resources.Length == 0) continue;
                if (FirstRequirement(piece) == null) continue;
                if (material == null || RequirementFor(piece, material) != null) return piece;
            }
            return null;
        }

        private static Piece.Requirement RequirementFor(Piece piece, string material)
        {
            if (piece == null || piece.m_resources == null) return null;
            for (int i = 0; i < piece.m_resources.Length; i++)
            {
                var r = piece.m_resources[i];
                if (r == null || r.m_resItem == null) continue;
                if (string.Equals(Tiers.CleanName(r.m_resItem.name), material, StringComparison.OrdinalIgnoreCase))
                    return r;
            }
            return null;
        }
    }
}
