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
    ///     Piece.DropResources(HitData)                      - the deconstruct REFUND (0.6.0; see
    ///                                                         DropResourcesPre - it reads
    ///                                                         m_amount directly, so without this
    ///                                                         a discounted piece refunded MORE
    ///                                                         than it cost)
    ///
    /// Since 0.6.0 this module also hosts [Settlement] SettlementDiscount's factor: that module
    /// owns its own config and Enabled but contributes a multiplier into the single composition
    /// below (tier x station x settlement, rounded once), so the two discounts cannot round twice
    /// or race each other's in-place m_amount save/restore. See SettlementDiscountModule.
    ///
    /// Since 0.9.3 [Builders] BuildersGuild plugs into the same composition twice over, and this is
    /// the only place in the mod where a build piece's cost is PER-REQUIREMENT rather than one
    /// factor for the whole piece:
    ///   - a per-material YARD multiplier (wood is half price near a workbench, iron a tenth near
    ///     a forge), composed into the same single rounding, with an optional per-material floor
    ///     that may be 0 - a requirement that rounds to 0 disappears from the piece, which vanilla
    ///     handles cleanly at all four call sites (SetupRequirement hides the row, HaveRequirements
    ///     skips m_amount &lt;= 0, ConsumeResources skips num &lt;= 0, DropResources skips dropCount
    ///     &lt;= 0);
    ///   - the FRAMING rule, an ABSOLUTE override for beams and poles (1 of each material
    ///     free-standing, 0 when snapped onto another beam) that bypasses every multiplier.
    /// Both arrive through <see cref="PieceAmount"/>, which is the single function every build-piece
    /// path below now goes through.
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

        public override string Theme => "Catching up";

        public override string Hint => "Trailing-tier recipes and build pieces cost less";

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
        /// single-threaded, but ThreadStatic costs nothing and makes the invariant explicit.
        ///
        /// IsPiece/PieceRef were added in 0.6.0 for [Settlement] SettlementDiscount, which applies
        /// to BUILD PIECES ONLY and needs the piece itself to honour its ExcludePieces /
        /// OnlyCategories lists. A crafting context leaves both at their defaults, which is what
        /// makes "never a crafting recipe" structural rather than a rule someone has to remember.</summary>
        internal struct Ctx
        {
            public int Tier;
            public float Station;
            public bool IsPiece;
            public Piece PieceRef;
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

        /// <summary>
        /// True when EITHER this module or [Settlement] SettlementDiscount wants build-piece costs
        /// scaled. The build-piece context setters and the two in-place m_amount patches
        /// (HaveRequirements(Piece,...) and DropResources) gate on this rather than on Live(), so
        /// switching [Discount] Enabled off at runtime leaves SettlementDiscount working. The
        /// patches themselves are still installed by THIS module, so [Discount] Enabled must have
        /// been on at startup for either to reach the game - the usual "on: restart / off: live"
        /// rule, documented in [Settlement] Enabled and docs/MODULES.md.
        /// </summary>
        private static bool PieceLive()
        {
            return Live() || SettlementDiscountModule.Live() || BuildersGuildModule.Live();
        }

        /// <summary>
        /// Are the shared build-cost patches installed and is SOMETHING contributing to them right
        /// now? [Builders] asks before it records what a piece was paid for: a record written while
        /// the pipeline is not actually pricing anything would be a lie the refund path would then
        /// believe.
        /// </summary>
        internal static bool BuildCostsActive()
        {
            return _self != null && _self.Applied && PieceLive();
        }

        /// <summary>
        /// The single combined WHOLE-PIECE cost factor: tier discount x settlement discount.
        /// Per-material factors (the [Builders] yard) are not in here - see
        /// <see cref="PieceAmount"/>, which is what every build-piece path actually calls.
        /// </summary>
        internal static float PieceCostFactor(Piece piece)
        {
            float mult = 1f;
            if (Live())
            {
                int tier = Tiers.OfPiece(piece);
                if (tier > 0 && Tiers.IsBehind(tier)) mult = MultiplierFor(tier);
            }
            mult *= SettlementDiscountModule.FactorFor(piece);
            return mult;
        }

        /// <summary>
        /// What ONE requirement of a build piece costs right now, rounded exactly once.
        ///
        /// Every build-piece path goes through here - the HUD row, the CanBuild check, the
        /// consume, and the deconstruct refund - which is what makes the four numbers provably the
        /// same. The order matters:
        ///
        ///   1. a TRACKED REFUND wins outright: the piece itself recorded what it was paid for, so
        ///      it gives back exactly that and never a recomputed price;
        ///   2. otherwise the FRAMING rule, an absolute cost that bypasses the multipliers (but
        ///      never charges more than vanilla would);
        ///   3. otherwise tier x settlement x yard, rounded once, floored at MinAmount or at the
        ///      material's own [Builders] MinAmounts override.
        ///
        /// <paramref name="forRefund"/> does two things. It separates 1 from 2 - the framing rule
        /// is a PRICE, not a refund - and it puts every TRANSIENT factor into its "cheapest"
        /// setting: the yard counts as though the builder were standing in it (and Stage 3's
        /// rhythm as though it were at full streak), so a refund can never pay out more than the
        /// piece could have been bought for, wherever the player happens to be standing when they
        /// swing the hammer. See BuildersGuildModule.MaterialFactor.
        /// </summary>
        internal static int PieceAmount(Piece piece, Piece.Requirement req, int vanillaAmount, bool forRefund)
        {
            if (vanillaAmount <= 0) return vanillaAmount;

            if (forRefund && BuildersGuildModule.HasPaidRecord(piece))
                return Mathf.Clamp(BuildersGuildModule.RecordedAmount(piece, req), 0, vanillaAmount);

            int flat;
            if (!forRefund && BuildersGuildModule.StructuralAmount(piece, req, out flat))
                return Mathf.Clamp(flat, 0, vanillaAmount);

            // vanilla x tier x settlement x YARD(material) x SKILL(builder) x RHYTHM(streak),
            // rounded exactly once. The yard belongs to the material, the last two belong to the
            // builder and therefore reach every piece, listed material or not.
            float mult = PieceCostFactor(piece)
                       * BuildersGuildModule.MaterialFactor(piece, req, forRefund)
                       * BuildersGuildModule.ExtraFactorFor(piece, req, forRefund);
            return ScaledAmount(vanillaAmount, mult, BuildersGuildModule.FloorFor(req));
        }

        protected override void Bind()
        {
            _self = this;

            _costMultiplier = BindSynced("CostMultiplier", 0.5f,
                "Cost of a recipe or build piece whose tier is behind the frontier, as a fraction " +
                "of vanilla. 0.5 = half price. 1 = no discount. Values above 1 are clamped to 1: " +
                "this module never makes anything more expensive.",
                Opt.N("Cost of a trailing-tier recipe or build piece, as a fraction of vanilla",
                    0.01, 1, 0.05));

            _extraPerTierBehind = BindSynced("ExtraPerTierBehind", 0.0f,
                "Extra discount per FURTHER tier behind the frontier. 0 = the same discount " +
                "whether the recipe is 1 or 3 tiers behind. 0.1 with CostMultiplier 0.5 means " +
                "0.5 at the threshold, 0.4 one tier further back, 0.3 two tiers further back. " +
                "The multiplier is clamped to a minimum of 0.01.",
                Opt.N("Extra discount per further tier behind the frontier", 0, 0.5, 0.05));

            _minAmount = BindSynced("MinAmount", 1,
                "Floor for a discounted requirement. 1 = a cost never drops to zero. A " +
                "requirement that already costs less than this is left alone.",
                Opt.N("Lowest a discounted requirement can drop to", 0, 10, 1));

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
                "result still respects MinAmount. Empty = off.",
                Opt.T("Extra per-station cost discounts, e.g. for another mod's recipes")
                    .Pick(new PickerSpec(PickerSource.Stations,
                        new PickerField("Multiplier", 0.01, 1.0, 0.75, false))));

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

            // The deconstruct refund reads m_amount directly too - same treatment, same factor,
            // so a refund can never exceed what the piece costs. See DropResourcesPre.
            var dropResources = AccessTools.Method(typeof(Piece), "DropResources", new[] { typeof(HitData) });
            if (dropResources == null) throw new Exception("Piece.DropResources(HitData) not found");
            Harmony.Patch(dropResources,
                          prefix: Post(nameof(DropResourcesPre)),
                          finalizer: Post(nameof(DropResourcesFin)));

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

        /// <summary>
        /// <paramref name="__instance"/> is the requirement being priced - needed since 0.9.3
        /// because [Builders] prices wood and iron differently inside the same piece, and a
        /// Requirement knows its own material even though it does not know its piece.
        /// </summary>
        private static void GetAmountPost(Piece.Requirement __instance, ref int __result)
        {
            if (__result <= 0) return;
            var ctx = _ctx;

            // Build pieces only, and only inside a context that knows it is one. Everything a
            // build piece's cost depends on - tier, settlement, yard, framing - lives in the one
            // shared function, so the HUD row and the consume path cannot drift apart.
            if (ctx.IsPiece)
            {
                if (!PieceLive()) return;
                __result = PieceAmount(ctx.PieceRef, __instance, __result, false);
                return;
            }

            // Crafting recipes: tier x station only, exactly as before.
            if (!Live()) return;
            float mult = 1f;
            if (ctx.Tier > 0 && Tiers.IsBehind(ctx.Tier)) mult = MultiplierFor(ctx.Tier);
            if (ctx.Station > 0f && ctx.Station < 1f) mult *= ctx.Station;
            if (mult >= 1f) return;

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
            if (!PieceLive()) { _ctx = default(Ctx); return; }

            // The tier is derivable from the requirements alone; the station is not, so it is
            // inherited from whatever context wraps this call - DoCrafting for a recipe, nothing
            // (= no station discount) for the build path.
            //
            // Whether this is a BUILD PIECE is inherited the same way when a piece context already
            // wraps the call; otherwise it is decided by identity. Vanilla's only piece-consuming
            // call site is Player.UpdatePlacement, which passes `selectedPiece.m_resources` - the
            // very array object hanging off the currently selected piece - so a reference compare
            // is exact, costs nothing, and cannot mistake a recipe for a piece.
            var pieceRef = __state.IsPiece ? __state.PieceRef : SelectedPieceFor(requirements);
            _ctx = new Ctx
            {
                Tier = Tiers.OfRequirements(requirements),
                Station = __state.Station,
                IsPiece = pieceRef != null,
                PieceRef = pieceRef
            };
        }

        /// <summary>The build piece whose own requirement array this is, or null.</summary>
        private static Piece SelectedPieceFor(Piece.Requirement[] requirements)
        {
            if (requirements == null) return null;
            var player = Player.m_localPlayer;
            if (player == null || player.m_buildPieces == null) return null;
            var selected = player.m_buildPieces.GetSelectedPiece();
            return selected != null && ReferenceEquals(selected.m_resources, requirements) ? selected : null;
        }

        private static void PieceCtxPre(Piece piece, out Ctx __state)
        {
            __state = _ctx;
            // Station multipliers are a crafting-recipe feature; a build piece's m_craftingStation
            // is a proximity requirement, not where its cost comes from, so it never matches.
            _ctx = PieceLive()
                       ? new Ctx { Tier = Tiers.OfPiece(piece), Station = 1f, IsPiece = true, PieceRef = piece }
                       : default(Ctx);
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
            __state = ScaleInPlace(piece, false);
        }

        private static void HavePieceFin(Piece piece, int[] __state)
        {
            RestoreInPlace(piece, __state);
        }

        // ---- the deconstruct refund, which never calls GetAmount either -------------------------

        /// <summary>
        /// Piece.DropResources reads requirement.m_amount DIRECTLY, so without this a discounted
        /// piece would refund the full vanilla amount - build a wall for 3 wood, break it for 4,
        /// repeat. The same scale-and-restore used for the CanBuild check, through the SAME shared
        /// pricing function, makes the refund exactly the current price and never more - or, for a
        /// piece that recorded what it was paid ([Builders] framing), exactly what it was paid.
        /// (Vanilla's own modifiers - the Feast stack percentage and the /3 for pieces not placed
        /// by a player - then apply on top, unchanged.)
        /// </summary>
        private static void DropResourcesPre(Piece __instance, out int[] __state)
        {
            __state = ScaleInPlace(__instance, true);
        }

        private static void DropResourcesFin(Piece __instance, int[] __state)
        {
            RestoreInPlace(__instance, __state);
        }

        /// <summary>Rewrite a piece's m_amount fields for the duration of one call, one requirement
        /// at a time (the yard prices wood and iron differently inside the same piece). Returns the
        /// saved originals, or null when nothing was touched.</summary>
        private static int[] ScaleInPlace(Piece piece, bool forRefund)
        {
            if (!PieceLive() || piece == null || piece.m_resources == null) return null;

            var reqs = piece.m_resources;
            int[] saved = null;
            for (int i = 0; i < reqs.Length; i++)
            {
                var r = reqs[i];
                if (r == null || r.m_amount <= 0) continue;

                int now = PieceAmount(piece, r, r.m_amount, forRefund);
                if (now == r.m_amount) continue;

                if (saved == null)
                {
                    saved = new int[reqs.Length];
                    for (int j = 0; j < reqs.Length; j++) saved[j] = reqs[j] == null ? 0 : reqs[j].m_amount;
                }
                r.m_amount = now;
            }
            return saved;
        }

        /// <summary>Put them back. A finalizer, so vanilla throwing cannot leave a piece cheap
        /// (or expensive) for the rest of the session.</summary>
        private static void RestoreInPlace(Piece piece, int[] saved)
        {
            if (saved == null || piece == null || piece.m_resources == null) return;
            var reqs = piece.m_resources;
            int n = Math.Min(reqs.Length, saved.Length);
            for (int i = 0; i < n; i++)
                if (reqs[i] != null) reqs[i].m_amount = saved[i];
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
            return ScaledAmount(amount, mult, -1);
        }

        /// <summary>
        /// The one place a build cost is rounded. <paramref name="floorOverride"/> is [Builders]
        /// MinAmounts: 0 or more REPLACES every other floor for this one material (0 lets the
        /// requirement round away and drop off the piece entirely, which is the point of the
        /// setting); -1 means "use the normal floors".
        /// </summary>
        internal static int ScaledAmount(int amount, float mult, int floorOverride)
        {
            if (amount <= 0 || mult >= 1f) return amount;

            int v = Mathf.RoundToInt(amount * mult);
            // Whichever discounts are in play, take the STRICTEST floor of the ones that are:
            // [Settlement] MinAmountFloor() returns 0 when that module is not contributing.
            int floor = floorOverride >= 0
                            ? floorOverride
                            : Mathf.Max(_minAmount != null ? _minAmount.Value : 1,
                                        SettlementDiscountModule.MinAmountFloor());
            floor = Mathf.Min(amount, Mathf.Max(0, floor));
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
