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
    /// Recipes and build pieces whose tier is behind the frontier cost less, and recipes crafted
    /// at a named crafting station cost less whatever their tier.
    ///
    /// The scaling itself happens in one place - a postfix on Piece.Requirement.GetAmount(int) -
    /// but a Requirement has no idea which recipe or piece it belongs to, and the spec's rule is
    /// "the recipe's tier is the MAX tier over its requirements" (so the wood in a bronze recipe
    /// is discounted too). So every caller that DOES know the whole recipe/requirement array
    /// publishes a <see cref="Ctx"/> (tier + station factor) into a thread-static for the duration
    /// of its own call, and the postfix only fires while a context is set. No context = vanilla
    /// numbers, which is the safe default.
    ///
    /// Context-setting call sites (0.221.13), chosen so the number SHOWN, the number CHECKED and
    /// the number CONSUMED can never disagree:
    ///
    ///   crafting / upgrading
    ///     Player.HaveRequirements(Recipe, bool, int, int)   - the "can I craft this?" check,
    ///                                                         also covers private HaveRequirementItems
    ///     Player.GetFirstRequiredItem(Inventory, Recipe, ...) - the m_requireOnlyOneIngredient path,
    ///                                                         reached from Recipe.GetAmount
    ///     InventoryGui.SetupRequirementList(int,Player,bool,int) - the crafting panel's cost row
    ///                                                         (it calls the static SetupRequirement,
    ///                                                         which has no recipe of its own)
    ///     InventoryGui.DoCrafting(Player)                   - publishes m_craftRecipe's STATION for
    ///                                                         the ConsumeResources call it makes:
    ///                                                         a Requirement[] alone cannot say which
    ///                                                         station it came from, so ConsumeResources'
    ///                                                         own prefix inherits the station from
    ///                                                         whatever context wraps it
    ///     Player.ConsumeResources(Requirement[], int, int, int) - what actually leaves the inventory
    ///
    ///   building
    ///     Hud.SetupPieceInfo(Piece)                         - the build hammer's cost row
    ///     Player.ConsumeResources(...)                      - shared with crafting (called with
    ///                                                         piece.m_resources, qualityLevel 0)
    ///
    /// Player.HaveRequirements(Piece, RequirementMode) is the one hole: its CanBuild branch reads
    /// requirement.m_amount DIRECTLY instead of calling GetAmount, so a GetAmount postfix cannot
    /// reach it. Rather than duplicate vanilla's station/DLC/free-build checks in a postfix, the
    /// prefix scales the m_amount fields in place for the duration of that one call and a
    /// finalizer puts them back (finalizers run even if vanilla throws). Pieces are always
    /// quality 1, so GetAmount(0) == m_amount and the two paths agree exactly.
    ///
    /// StationMultipliers exists because a mod's own config is not always authoritative:
    /// CookingAdditions rewrites its "Crafting Costs" back to its defaults on every server boot,
    /// so its recipes cannot be made cheaper there. Scaling them here - at the four call sites
    /// above, never by editing Recipe.m_resources in ObjectDB - survives that, because the other
    /// mod re-applies its own numbers to the shared recipe data and we only ever change the
    /// number on its way out of GetAmount.
    /// </summary>
    internal sealed class TrailingTierDiscountModule : FeatureModule
    {
        public override string Name => "TrailingTierDiscount";
        public override ModuleSide Side => ModuleSide.Client;
        public override string Section => "Discount";

        private static ConfigEntry<float> _costMultiplier;
        private static ConfigEntry<float> _extraPerTierBehind;
        private static ConfigEntry<int> _minAmount;
        private static ConfigEntry<string> _stationMultipliers;
        private static TrailingTierDiscountModule _self;

        /// <summary>StationPrefabName -> cost multiplier, parsed from [Discount] StationMultipliers.</summary>
        private static Dictionary<string, float> _stations =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Recipes in ObjectDB whose station is listed. -1 = not counted yet.</summary>
        private static int _matchedRecipes = -1;
        private static bool _stationsReported;

        /// <summary>What the requirement currently being priced belongs to. Tier 0 = none,
        /// Station 0 = not set (treated as 1 = no station discount). Game logic is
        /// single-threaded, but ThreadStatic costs nothing and makes the invariant explicit.</summary>
        internal struct Ctx
        {
            public int Tier;
            public float Station;
        }

        [ThreadStatic] private static Ctx _ctx;

        // InventoryGui.m_selectedRecipe is a private field of the private struct RecipeDataPair,
        // whose Recipe is a get-only property. Resolved once at patch time, not per frame.
        private static FieldInfo _fiSelectedRecipe;
        private static MethodInfo _piRecipeGet;
        private static FieldInfo _fiCraftRecipe;

        private static bool Live()
        {
            return _self != null && _self.Active && ClientActive();
        }

        protected override void Bind()
        {
            _self = this;

            _costMultiplier = BindSynced("CostMultiplier", 0.5f,
                "Cost of a recipe or build piece whose tier is behind the frontier, as a fraction " +
                "of vanilla. 0.5 = half price. 1 = no discount. Values above 1 are clamped to 1: " +
                "this module never makes anything more expensive.");

            _extraPerTierBehind = BindSynced("ExtraPerTierBehind", 0.0f,
                "Extra discount per FURTHER tier behind the frontier. 0 = the same discount " +
                "whether the recipe is 1 or 3 tiers behind. 0.1 with CostMultiplier 0.5 means " +
                "0.5 at the threshold, 0.4 one tier further back, 0.3 two tiers further back. " +
                "The multiplier is clamped to a minimum of 0.01.");

            _minAmount = BindSynced("MinAmount", 1,
                "Floor for a discounted requirement. 1 = a cost never drops to zero. A " +
                "requirement that already costs less than this is left alone.");

            _stationMultipliers = BindSynced("StationMultipliers", DefaultStationMultipliers,
                "Comma-separated StationPrefabName:multiplier pairs. Every recipe crafted at a " +
                "listed station costs that fraction of its normal ingredients, at every quality " +
                "level, whatever its tier - use it to re-price another mod's recipes when that " +
                "mod's own cost settings do not stick (CookingAdditions rewrites its \"Crafting " +
                "Costs\" back to defaults on every server boot, which is why BCA_CookingPot is " +
                "the default entry). The name is the STATION PREFAB name, not its displayed " +
                "name. Recipes with no crafting station, and build pieces, are never matched. " +
                "Multipliers are clamped to 0.01..1: this module never makes anything more " +
                "expensive. Composes with the tier discount above by multiplication, and the " +
                "result still respects MinAmount. Empty = off.");

            ParseStations();
        }

        /// <summary>CookingAdditions' custom cooking pot at 3/4 price - see StationMultipliers.</summary>
        internal const string DefaultStationMultipliers = "BCA_CookingPot:0.75";

        protected override void ApplyPatches()
        {
            var getAmount = AccessTools.Method(typeof(Piece.Requirement), "GetAmount", new[] { typeof(int) });
            if (getAmount == null) throw new Exception("Piece.Requirement.GetAmount(int) not found");
            Harmony.Patch(getAmount, postfix: Post(nameof(GetAmountPost)));

            PatchCtx(AccessTools.Method(typeof(Player), "HaveRequirements",
                         new[] { typeof(Recipe), typeof(bool), typeof(int), typeof(int) }),
                     "Player.HaveRequirements(Recipe,bool,int,int)",
                     nameof(RecipeCtxPre), nameof(CtxFin));

            PatchCtx(AccessTools.Method(typeof(Player), "GetFirstRequiredItem"),
                     "Player.GetFirstRequiredItem(Inventory,Recipe,...)",
                     nameof(RecipeCtxPre), nameof(CtxFin));

            PatchCtx(AccessTools.Method(typeof(Player), "ConsumeResources",
                         new[] { typeof(Piece.Requirement[]), typeof(int), typeof(int), typeof(int) }),
                     "Player.ConsumeResources(Requirement[],int,int,int)",
                     nameof(ConsumeCtxPre), nameof(CtxFin));

            PatchCtx(AccessTools.Method(typeof(Hud), "SetupPieceInfo", new[] { typeof(Piece) }),
                     "Hud.SetupPieceInfo(Piece)",
                     nameof(PieceCtxPre), nameof(CtxFin));

            // Crafting panel cost rows. Needs the private selected-recipe field; resolve it up
            // front so a rename in a future Valheim build fails loudly here instead of silently
            // leaving the UI showing vanilla numbers while the consume path halves them.
            _fiSelectedRecipe = AccessTools.Field(typeof(InventoryGui), "m_selectedRecipe");
            if (_fiSelectedRecipe == null)
                throw new Exception("InventoryGui.m_selectedRecipe not found");
            _piRecipeGet = AccessTools.PropertyGetter(_fiSelectedRecipe.FieldType, "Recipe");
            if (_piRecipeGet == null)
                throw new Exception("InventoryGui." + _fiSelectedRecipe.FieldType.Name + ".Recipe getter not found");

            PatchCtx(AccessTools.Method(typeof(InventoryGui), "SetupRequirementList",
                         new[] { typeof(int), typeof(Player), typeof(bool), typeof(int) }),
                     "InventoryGui.SetupRequirementList(int,Player,bool,int)",
                     nameof(GuiCtxPre), nameof(CtxFin));

            // The consume path's station. DoCrafting is the ONLY vanilla caller that hands a
            // recipe's m_resources to ConsumeResources, and the array by itself cannot say which
            // station the recipe belongs to - so the station factor is published here, around the
            // whole craft, and ConsumeCtxPre keeps it. Without this the panel would show a
            // station-discounted cost while the inventory paid the vanilla one.
            _fiCraftRecipe = AccessTools.Field(typeof(InventoryGui), "m_craftRecipe");
            if (_fiCraftRecipe == null)
                throw new Exception("InventoryGui.m_craftRecipe not found");

            PatchCtx(AccessTools.Method(typeof(InventoryGui), "DoCrafting", new[] { typeof(Player) }),
                     "InventoryGui.DoCrafting(Player)",
                     nameof(CraftCtxPre), nameof(CtxFin));

            // The build check reads m_amount directly - scale the fields for that call only.
            var havePiece = AccessTools.Method(typeof(Player), "HaveRequirements",
                                               new[] { typeof(Piece), typeof(Player.RequirementMode) });
            if (havePiece == null) throw new Exception("Player.HaveRequirements(Piece,RequirementMode) not found");
            Harmony.Patch(havePiece,
                          prefix: Post(nameof(HavePiecePre)),
                          finalizer: Post(nameof(HavePieceFin)));

            Log.LogInfo("[" + Name + "] " + Numbers());
        }

        private void PatchCtx(MethodBase target, string label, string pre, string fin)
        {
            if (target == null) throw new Exception(label + " not found");
            Harmony.Patch(target, prefix: Post(pre), finalizer: Post(fin));
        }

        private static HarmonyMethod Post(string name)
        {
            return new HarmonyMethod(typeof(TrailingTierDiscountModule), name);
        }

        // ---- the one place a number changes -------------------------------------------------

        private static void GetAmountPost(ref int __result)
        {
            if (__result <= 0) return;
            var ctx = _ctx;

            float mult = 1f;
            if (ctx.Tier > 0 && Tiers.IsBehind(ctx.Tier)) mult = MultiplierFor(ctx.Tier);
            if (ctx.Station > 0f && ctx.Station < 1f) mult *= ctx.Station;
            if (mult >= 1f) return;

            if (!Live()) return;
            __result = ScaledAmount(__result, mult);
        }

        // ---- context setters ------------------------------------------------------------------

        private static Ctx CtxOf(Recipe recipe)
        {
            return new Ctx { Tier = Tiers.OfRecipe(recipe), Station = StationMultiplierOf(recipe) };
        }

        private static void RecipeCtxPre(Recipe recipe, out Ctx __state)
        {
            __state = _ctx;
            _ctx = Live() ? CtxOf(recipe) : default(Ctx);
        }

        private static void ConsumeCtxPre(Piece.Requirement[] requirements, out Ctx __state)
        {
            __state = _ctx;
            // The tier is derivable from the requirements alone; the station is not, so it is
            // inherited from whatever context wraps this call - DoCrafting for a recipe, nothing
            // (= no station discount) for Player.PlacePiece.
            _ctx = Live()
                       ? new Ctx { Tier = Tiers.OfRequirements(requirements), Station = __state.Station }
                       : default(Ctx);
        }

        private static void PieceCtxPre(Piece piece, out Ctx __state)
        {
            __state = _ctx;
            // Station multipliers are a crafting-recipe feature; a build piece's m_craftingStation
            // is a proximity requirement, not where its cost comes from, so it never matches.
            _ctx = Live() ? new Ctx { Tier = Tiers.OfPiece(piece), Station = 1f } : default(Ctx);
        }

        private static void GuiCtxPre(InventoryGui __instance, out Ctx __state)
        {
            __state = _ctx;
            _ctx = default(Ctx);
            if (!Live()) return;
            try
            {
                object pair = _fiSelectedRecipe.GetValue(__instance);
                var recipe = pair == null ? null : _piRecipeGet.Invoke(pair, null) as Recipe;
                _ctx = CtxOf(recipe);
            }
            catch (Exception e)
            {
                Log.LogWarning("[TrailingTierDiscount] could not read the selected recipe: " + e.Message);
            }
        }

        private static void CraftCtxPre(InventoryGui __instance, out Ctx __state)
        {
            __state = _ctx;
            _ctx = default(Ctx);
            if (!Live()) return;
            try
            {
                _ctx = CtxOf(_fiCraftRecipe.GetValue(__instance) as Recipe);
            }
            catch (Exception e)
            {
                Log.LogWarning("[TrailingTierDiscount] could not read the crafted recipe: " + e.Message);
            }
        }

        /// <summary>Restores the caller's context. A finalizer, so an exception in vanilla code
        /// can never leave a stale tier or station behind.</summary>
        private static void CtxFin(Ctx __state)
        {
            _ctx = __state;
        }

        // ---- the build check, which never calls GetAmount --------------------------------------

        private static void HavePiecePre(Piece piece, Player.RequirementMode mode, out int[] __state)
        {
            __state = null;
            if (mode != Player.RequirementMode.CanBuild) return;
            if (!Live() || piece == null || piece.m_resources == null) return;

            int tier = Tiers.OfPiece(piece);
            if (tier <= 0 || !Tiers.IsBehind(tier)) return;

            var reqs = piece.m_resources;
            var saved = new int[reqs.Length];
            for (int i = 0; i < reqs.Length; i++)
            {
                var r = reqs[i];
                saved[i] = r == null ? 0 : r.m_amount;
                if (r != null && r.m_amount > 0) r.m_amount = DiscountedAmount(r.m_amount, tier);
            }
            __state = saved;
        }

        private static void HavePieceFin(Piece piece, int[] __state)
        {
            if (__state == null || piece == null || piece.m_resources == null) return;
            var reqs = piece.m_resources;
            int n = Math.Min(reqs.Length, __state.Length);
            for (int i = 0; i < n; i++)
                if (reqs[i] != null) reqs[i].m_amount = __state[i];
        }

        // ---- maths -----------------------------------------------------------------------------

        /// <summary>Cost multiplier for a recipe/piece of this tier. 1 = no discount.</summary>
        internal static float MultiplierFor(int tier)
        {
            if (_costMultiplier == null) return 1f;
            int behindBy = Frontier.WorldTier -
                           (Frontier.TiersBehind != null ? Frontier.TiersBehind.Value : 1) - tier;
            if (behindBy < 0) return 1f;
            float extra = _extraPerTierBehind != null ? _extraPerTierBehind.Value : 0f;
            return Mathf.Clamp(_costMultiplier.Value - extra * behindBy, 0.01f, 1f);
        }

        /// <summary>Vanilla amount -> tier-discounted amount.</summary>
        internal static int DiscountedAmount(int amount, int tier)
        {
            return ScaledAmount(amount, MultiplierFor(tier));
        }

        /// <summary>Vanilla amount x multiplier. Rounded to nearest, floored at MinAmount, and
        /// never above the vanilla amount.</summary>
        internal static int ScaledAmount(int amount, float mult)
        {
            if (amount <= 0 || mult >= 1f) return amount;

            int v = Mathf.RoundToInt(amount * mult);
            int floor = Mathf.Min(amount, Mathf.Max(0, _minAmount != null ? _minAmount.Value : 1));
            if (v < floor) v = floor;
            if (v > amount) v = amount;
            return v;
        }

        // ---- station multipliers -----------------------------------------------------------------

        private static void ParseStations()
        {
            var map = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            var raw = _stationMultipliers != null ? _stationMultipliers.Value : DefaultStationMultipliers;

            foreach (var chunk in (raw ?? "").Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var piece = chunk.Trim();
                if (piece.Length == 0) continue;

                var colon = piece.LastIndexOf(':');
                if (colon <= 0)
                {
                    Log.LogWarning("[Discount] ignoring malformed StationMultipliers entry '" + piece +
                                   "' (want StationPrefabName:multiplier)");
                    continue;
                }

                var name = piece.Substring(0, colon).Trim();
                float mult;
                if (!float.TryParse(piece.Substring(colon + 1).Trim(), NumberStyles.Float,
                                    CultureInfo.InvariantCulture, out mult) || mult <= 0f)
                {
                    Log.LogWarning("[Discount] ignoring '" + piece + "': multiplier must be a number above 0");
                    continue;
                }

                map[name] = Mathf.Clamp(mult, 0.01f, 1f);
            }

            _stations = map;
        }

        /// <summary>Cost multiplier for this recipe's crafting station. 1 = not listed / no station.</summary>
        internal static float StationMultiplierOf(Recipe recipe)
        {
            string name;
            float mult;
            return TryStation(recipe, out name, out mult) ? mult : 1f;
        }

        /// <summary>True if this recipe's crafting station is one of the configured ones. Matches on
        /// the station's PREFAB name (recipe.m_craftingStation.gameObject.name), never the
        /// localised m_name.</summary>
        private static bool TryStation(Recipe recipe, out string name, out float mult)
        {
            name = null;
            mult = 1f;
            if (recipe == null || _stations.Count == 0) return false;

            var station = recipe.m_craftingStation;
            if (station == null || station.gameObject == null) return false;

            var prefab = Tiers.CleanName(station.gameObject.name);
            if (string.IsNullOrEmpty(prefab) || !_stations.TryGetValue(prefab, out mult))
            {
                mult = 1f;
                return false;
            }

            name = prefab;
            return true;
        }

        /// <summary>Recipes in ObjectDB whose station is listed, or -1 while ObjectDB has none.</summary>
        private static int CountMatchedRecipes()
        {
            if (_stations.Count == 0) return 0;

            var odb = ObjectDB.instance;
            if (odb == null || odb.m_recipes == null || odb.m_recipes.Count == 0) return -1;

            int n = 0;
            string name;
            float mult;
            foreach (var r in odb.m_recipes)
                if (TryStation(r, out name, out mult)) n++;
            return n;
        }

        private static string StationsText()
        {
            if (_stations.Count == 0) return "none";
            var sb = new StringBuilder();
            foreach (var kv in _stations)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(kv.Key).Append(" x").Append(kv.Value.ToString("0.##", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        /// <summary>The proof line: what is configured and how many real recipes it hits.</summary>
        internal static void ReportStations()
        {
            _matchedRecipes = CountMatchedRecipes();
            Log.LogInfo("[Discount] station multipliers: " + StationsText() +
                        (_matchedRecipes < 0
                             ? " (ObjectDB has no recipes yet)"
                             : " (" + _matchedRecipes + " recipes matched)"));
        }

        // (ReportStationsOnce below is the load-time half of the same line.)

        /// <summary>
        /// Called from FrontierModule's ZoneSystem.Start postfix - the first point where
        /// ObjectDB.m_recipes is populated on BOTH halves (ObjectDB.UpdateRegisters is too early on
        /// a dedicated server; RepairAll's self-test learned that the hard way). This module is
        /// client-side, so on a dedicated server it never patches anything itself and could not log
        /// this line from its own hook - but its config is bound and synced there, so the server's
        /// log still proves what every client will be told.
        /// </summary>
        internal static void ReportStationsOnce()
        {
            if (_stationsReported || _stationMultipliers == null) return;
            if (CountMatchedRecipes() < 0) return;   // ObjectDB not ready yet - the next key change retries
            _stationsReported = true;
            ReportStations();
        }

        // ---- reporting --------------------------------------------------------------------------

        private string Numbers()
        {
            return "cost x" + _costMultiplier.Value.ToString("0.##") +
                   (_extraPerTierBehind.Value != 0f
                        ? " (-" + _extraPerTierBehind.Value.ToString("0.##") + " per further tier behind)"
                        : "") +
                   " min " + _minAmount.Value +
                   ", behind-the-frontier tiers: " + Frontier.BehindRangeText() +
                   ", stations: " + StationsText() +
                   (_matchedRecipes >= 0 ? " (" + _matchedRecipes + " recipes matched)" : "");
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            if (entry == _stationMultipliers)
            {
                // Re-parse and re-log on both halves: this is what makes a live edit of the
                // station list provable in a dedicated server's log, where the module itself is
                // disabled(side) and Active is false.
                ParseStations();
                ReportStations();
            }
            if (Active) Log.LogInfo("[" + Name + "] " + Numbers());
        }

        public override string StatusDetail()
        {
            return Numbers();
        }

        /// <summary>Headless proof: price a real recipe out of ObjectDB with the current frontier.</summary>
        internal static string SelfTest()
        {
            var sb = new StringBuilder();
            sb.Append("[SelfTest][TrailingTierDiscount] ").Append(Frontier.Describe())
              .Append(" TiersBehind=").Append(Frontier.TiersBehind.Value)
              .Append(" behind=").Append(Frontier.BehindRangeText())
              .Append(" CostMultiplier=").Append(_costMultiplier != null ? _costMultiplier.Value : -1f)
              .Append(" ExtraPerTierBehind=").Append(_extraPerTierBehind != null ? _extraPerTierBehind.Value : -1f)
              .Append(" MinAmount=").Append(_minAmount != null ? _minAmount.Value : -1)
              .Append(" StationMultipliers=").Append(StationsText());

            var odb = ObjectDB.instance;
            if (odb == null || odb.m_recipes == null)
            {
                sb.Append("\n  ObjectDB has no recipes - cannot price anything");
                return sb.ToString();
            }

            string[] wanted = { "AxeBronze", "Bronze", "ArmorBronzeChest", "AxeIron" };
            foreach (var want in wanted)
            {
                Recipe found = null;
                foreach (var r in odb.m_recipes)
                {
                    if (r == null || r.m_item == null) continue;
                    if (string.Equals(Tiers.CleanName(r.m_item.name), want, StringComparison.OrdinalIgnoreCase))
                    {
                        found = r;
                        break;
                    }
                }
                if (found == null)
                {
                    sb.Append("\n  ").Append(want).Append(": no recipe in ObjectDB");
                    continue;
                }

                int tier = Tiers.OfRecipe(found);
                bool behind = Tiers.IsBehind(tier);
                sb.Append("\n  ").Append(want).Append(": recipeTier=").Append(tier)
                  .Append(behind ? " BEHIND x" + MultiplierFor(tier).ToString("0.##") : " at/ahead of the frontier -> vanilla")
                  .Append(" ->");
                foreach (var req in found.m_resources)
                {
                    if (req == null || req.m_resItem == null) continue;
                    int vanilla = req.GetAmount(1);
                    int now = behind ? DiscountedAmount(vanilla, tier) : vanilla;
                    sb.Append(' ').Append(Tiers.CleanName(req.m_resItem.name))
                      .Append("(t").Append(Tiers.OfItem(req.m_resItem.name)).Append(") ")
                      .Append(vanilla).Append("->").Append(now).Append(';');
                }
            }

            AppendStationSamples(sb, odb, 5);
            return sb.ToString();
        }

        /// <summary>Price the first few recipes that a station multiplier actually hits, ingredient
        /// by ingredient, so "N recipes matched" can be checked against real numbers.</summary>
        private static void AppendStationSamples(StringBuilder sb, ObjectDB odb, int max)
        {
            int matched = CountMatchedRecipes();
            sb.Append("\n  stations: ").Append(StationsText())
              .Append(matched < 0 ? " (ObjectDB has no recipes yet)" : " (" + matched + " recipes matched)");
            if (matched <= 0) return;

            int shown = 0;
            foreach (var r in odb.m_recipes)
            {
                string station;
                float stationMult;
                if (!TryStation(r, out station, out stationMult)) continue;
                if (shown++ >= max) break;

                int tier = Tiers.OfRecipe(r);
                bool behind = Tiers.IsBehind(tier);
                float mult = (behind ? MultiplierFor(tier) : 1f) * stationMult;

                sb.Append("\n    ").Append(station).Append('/')
                  .Append(r.m_item != null ? Tiers.CleanName(r.m_item.name) : "?")
                  .Append(": station x").Append(stationMult.ToString("0.##", CultureInfo.InvariantCulture))
                  .Append(behind ? " * tier(" + tier + ") x" + MultiplierFor(tier).ToString("0.##") : "")
                  .Append(" = x").Append(mult.ToString("0.###", CultureInfo.InvariantCulture))
                  .Append(" ->");
                if (r.m_resources == null) continue;
                foreach (var req in r.m_resources)
                {
                    if (req == null || req.m_resItem == null) continue;
                    int vanilla = req.GetAmount(1);
                    sb.Append(' ').Append(Tiers.CleanName(req.m_resItem.name)).Append(' ')
                      .Append(vanilla).Append("->").Append(ScaledAmount(vanilla, mult)).Append(';');
                }
            }
        }
    }
}
