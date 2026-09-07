using System;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// Recipes and build pieces whose tier is behind the frontier cost less.
    ///
    /// The scaling itself happens in one place - a postfix on Piece.Requirement.GetAmount(int) -
    /// but a Requirement has no idea which recipe or piece it belongs to, and the spec's rule is
    /// "the recipe's tier is the MAX tier over its requirements" (so the wood in a bronze recipe
    /// is discounted too). So every caller that DOES know the whole requirement array publishes
    /// that tier into a thread-static context for the duration of its own call, and the postfix
    /// only fires while a context is set. No context = vanilla numbers, which is the safe default.
    ///
    /// Context-setting call sites (0.221.12), chosen so the number SHOWN, the number CHECKED and
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
    /// </summary>
    internal sealed class TrailingTierDiscountModule : FeatureModule
    {
        public override string Name => "TrailingTierDiscount";
        public override ModuleSide Side => ModuleSide.Client;
        public override string Section => "Discount";

        private static ConfigEntry<float> _costMultiplier;
        private static ConfigEntry<float> _extraPerTierBehind;
        private static ConfigEntry<int> _minAmount;
        private static TrailingTierDiscountModule _self;

        /// <summary>Tier of the recipe/piece currently being priced, 0 = none. Game logic is
        /// single-threaded, but ThreadStatic costs nothing and makes the invariant explicit.</summary>
        [ThreadStatic] private static int _ctxTier;

        // InventoryGui.m_selectedRecipe is a private field of the private struct RecipeDataPair,
        // whose Recipe is a get-only property. Resolved once at patch time, not per frame.
        private static FieldInfo _fiSelectedRecipe;
        private static MethodInfo _piRecipeGet;

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
        }

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
            int tier = _ctxTier;
            if (tier <= 0 || __result <= 0) return;
            if (!Live()) return;
            if (!Tiers.IsBehind(tier)) return;
            __result = DiscountedAmount(__result, tier);
        }

        // ---- context setters ------------------------------------------------------------------

        private static void RecipeCtxPre(Recipe recipe, out int __state)
        {
            __state = _ctxTier;
            _ctxTier = Live() ? Tiers.OfRecipe(recipe) : 0;
        }

        private static void ConsumeCtxPre(Piece.Requirement[] requirements, out int __state)
        {
            __state = _ctxTier;
            _ctxTier = Live() ? Tiers.OfRequirements(requirements) : 0;
        }

        private static void PieceCtxPre(Piece piece, out int __state)
        {
            __state = _ctxTier;
            _ctxTier = Live() ? Tiers.OfPiece(piece) : 0;
        }

        private static void GuiCtxPre(InventoryGui __instance, out int __state)
        {
            __state = _ctxTier;
            _ctxTier = 0;
            if (!Live()) return;
            try
            {
                object pair = _fiSelectedRecipe.GetValue(__instance);
                var recipe = pair == null ? null : _piRecipeGet.Invoke(pair, null) as Recipe;
                _ctxTier = Tiers.OfRecipe(recipe);
            }
            catch (Exception e)
            {
                Log.LogWarning("[TrailingTierDiscount] could not read the selected recipe: " + e.Message);
            }
        }

        /// <summary>Restores the caller's context. A finalizer, so an exception in vanilla code
        /// can never leave a stale tier behind.</summary>
        private static void CtxFin(int __state)
        {
            _ctxTier = __state;
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

        /// <summary>Vanilla amount -> discounted amount. Rounded to nearest, floored at MinAmount,
        /// and never above the vanilla amount.</summary>
        internal static int DiscountedAmount(int amount, int tier)
        {
            if (amount <= 0) return amount;
            float mult = MultiplierFor(tier);
            if (mult >= 1f) return amount;

            int v = Mathf.RoundToInt(amount * mult);
            int floor = Mathf.Min(amount, Mathf.Max(0, _minAmount != null ? _minAmount.Value : 1));
            if (v < floor) v = floor;
            if (v > amount) v = amount;
            return v;
        }

        // ---- reporting --------------------------------------------------------------------------

        private string Numbers()
        {
            return "cost x" + _costMultiplier.Value.ToString("0.##") +
                   (_extraPerTierBehind.Value != 0f
                        ? " (-" + _extraPerTierBehind.Value.ToString("0.##") + " per further tier behind)"
                        : "") +
                   " min " + _minAmount.Value +
                   ", behind-the-frontier tiers: " + Frontier.BehindRangeText();
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
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
              .Append(" MinAmount=").Append(_minAmount != null ? _minAmount.Value : -1);

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
            return sb.ToString();
        }
    }
}
