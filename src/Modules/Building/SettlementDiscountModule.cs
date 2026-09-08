using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// SettlementDiscount - build pieces get cheaper as the clan grows: every boss the world has
    /// killed knocks a slice off construction costs, up to a hard ceiling.
    ///
    ///     factor = max(1 - PerTierDiscount * Frontier.WorldTier, 1 - MaxDiscount)
    ///
    /// With the defaults that is 10% per boss to a floor of x0.50: tier 0 = vanilla, tier 3 =
    /// x0.70, tier 5 and beyond = x0.50. Amounts are rounded to nearest and floored at MinAmount,
    /// and can never come out ABOVE the vanilla amount.
    ///
    /// BUILD PIECES ONLY. Crafting recipes are untouched - a bronze axe costs what it always cost.
    /// The discount reaches exactly the four places a piece's cost is read, and they are the same
    /// four <see cref="TrailingTierDiscountModule"/> already owns, so the number SHOWN, the number
    /// CHECKED and the number CONSUMED can never disagree:
    ///
    ///   Hud.SetupPieceInfo(Piece)                        the build hammer's cost row (display)
    ///   Player.HaveRequirements(Piece, RequirementMode)  "can I build this?" (reads m_amount directly)
    ///   Player.ConsumeResources(Requirement[], ...)      what actually leaves the inventory
    ///   Piece.DropResources(HitData)                     the deconstruct REFUND (see below)
    ///
    /// WHERE THE CODE LIVES. Rather than a second postfix on <c>Piece.Requirement.GetAmount(int)</c>
    /// - which would round twice and would race the first one's save/restore of <c>m_amount</c> -
    /// this module contributes a FACTOR to the single requirement-scaling context that already
    /// exists in TrailingTierDiscountModule. That module composes
    /// <c>tierMult * stationMult * settlementFactor</c> multiplicatively in one place and rounds
    /// once, so a behind-the-frontier stone piece in a five-boss world gets both discounts and
    /// still respects MinAmount. This class owns its own <c>[Settlement]</c> section and its own
    /// <c>Enabled</c>, so it can be switched off live and independently; because the shared
    /// machinery is installed by <c>[Discount]</c>, turning <c>[Discount] Enabled</c> off at boot
    /// also silences this module (stated in the Enabled description below and in docs/MODULES.md).
    /// It therefore installs no gameplay patches of its own - only the one-shot world-ready hook
    /// that writes its proof line.
    ///
    /// DECONSTRUCT REFUNDS ARE SCALED TOO. Vanilla <c>Piece.DropResources</c> returns
    /// <c>requirement.m_amount</c> - the raw field, never <c>GetAmount()</c> - so a discount that
    /// only touched the cost would let a player build a wall for 3 wood and break it for 4, over
    /// and over. TrailingTierDiscountModule therefore scales those same fields for the duration of
    /// <c>DropResources</c> with the IDENTICAL combined factor, so a refund is always exactly what
    /// the piece would cost right now and never more. (That also closes the same hole for the tier
    /// discount, which has existed since 0.3.0.) One documented consequence: a piece built before
    /// the discount applied refunds today's cheaper price, not the price it was paid at - the rule
    /// is "never refund more than it costs now", deliberately erring against the player.
    ///
    /// Side = Client, like TrailingTierDiscount: build costs are a client-side decision and a
    /// dedicated server never places a piece. <c>[Settlement] SelfTest</c> flips Side to Both so a
    /// headless server can still prove the maths against real prefabs (same trick as FastMining
    /// and RepairAll); every code path still gates on ClientActive() and stays inert there.
    /// </summary>
    internal sealed class SettlementDiscountModule : FeatureModule
    {
        public override string Name => "SettlementDiscount";
        public override string Section => "Settlement";

        /// <summary>Client, except while the local SelfTest flag is on - see the class doc.</summary>
        public override ModuleSide Side =>
            _selfTest != null && _selfTest.Value ? ModuleSide.Both : ModuleSide.Client;

        protected override string EnabledDescription =>
            "Build pieces cost less as the world clears bosses (build pieces only - crafting " +
            "recipes are never touched). Deconstruct refunds are scaled by the same factor, so a " +
            "refund can never exceed what the piece costs. Requires [Discount] Enabled at " +
            "startup: the shared build-cost machinery this module feeds is installed there.";

        private static ConfigEntry<float> _perTierDiscount;
        private static ConfigEntry<float> _maxDiscount;
        private static ConfigEntry<int> _minAmount;
        private static ConfigEntry<string> _excludePieces;
        private static ConfigEntry<string> _onlyCategories;
        private static ConfigEntry<bool> _selfTest;

        private static SettlementDiscountModule _self;
        private static bool _reported;

        private static HashSet<string> _excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<Piece.PieceCategory> _categories = new List<Piece.PieceCategory>();

        /// <summary>Enabled, patched, and running on a real client. The one gate everything uses.</summary>
        internal static bool Live()
        {
            return _self != null && _self.Active && ClientActive();
        }

        // ---- config -------------------------------------------------------------------------

        protected override void Bind()
        {
            _self = this;

            _perTierDiscount = BindSynced("PerTierDiscount", 0.10f,
                "Fraction knocked off every build piece's cost per boss the world has killed " +
                "([Frontier] WorldTier). 0.10 = 10% per boss: tier 3 pays 70%. 0 = off.");

            _maxDiscount = BindSynced("MaxDiscount", 0.50f,
                "Ceiling on the total settlement discount, as a fraction. 0.5 = build pieces " +
                "never drop below half price however many bosses are down. Clamped to 0..0.95.");

            _minAmount = BindSynced("MinAmount", 1,
                "Floor for a discounted requirement. 1 = a piece never becomes free. A " +
                "requirement that already costs less than this is left alone.");

            _excludePieces = BindSynced("ExcludePieces", "",
                "Comma-separated piece PREFAB names that never get the settlement discount " +
                "(portals, for example, if you would rather they stayed expensive). Empty = " +
                "nothing is excluded.");

            _onlyCategories = BindSynced("OnlyCategories", "",
                "Comma-separated build categories to restrict the discount to: Misc, Crafting, " +
                "BuildingWorkbench, BuildingStonecutter, Furniture, Feasts, Food, Meads. Empty " +
                "(the default) = every category.");

            _selfTest = BindLocal("SelfTest", false,
                "Diagnostic, local only, never synced. Flips this module's Side to Both so a " +
                "dedicated server can log the factor and price five real build pieces before and " +
                "after, once per world load. Changes no game state. Leave false in normal use.");

            ParseLists();
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            if (entry == _excludePieces || entry == _onlyCategories) ParseLists();
            if (Active) Log.LogInfo("[" + Name + "] " + Numbers());
        }

        public override void Disable()
        {
            base.Disable();
            if (_self == this) _self = null;
        }

        private static void ParseLists()
        {
            var ex = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var chunk in Split(_excludePieces)) ex.Add(chunk);
            _excluded = ex;

            _categories.Clear();
            foreach (var chunk in Split(_onlyCategories))
            {
                try
                {
                    _categories.Add((Piece.PieceCategory)Enum.Parse(typeof(Piece.PieceCategory), chunk, true));
                }
                catch
                {
                    Log.LogWarning("[Settlement] ignoring unknown category '" + chunk + "' in OnlyCategories");
                }
            }
        }

        private static IEnumerable<string> Split(ConfigEntry<string> entry)
        {
            var raw = entry != null ? entry.Value : null;
            foreach (var chunk in (raw ?? "").Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var s = chunk.Trim();
                if (s.Length > 0) yield return s;
            }
        }

        // ---- no gameplay patches of its own; just the proof line ---------------------------------

        protected override void ApplyPatches()
        {
            // Deliberately empty of gameplay hooks: the factor below is consumed by
            // TrailingTierDiscountModule's single requirement-scaling context. See the class doc.
            var start = AccessTools.Method(typeof(ZoneSystem), "Start");
            if (start == null) throw new Exception("ZoneSystem.Start() not found");
            Harmony.Patch(start, postfix: new HarmonyMethod(typeof(SettlementDiscountModule), nameof(WorldReady)));

            Log.LogInfo("[" + Name + "] " + Numbers());
        }

        // ---- the factor ---------------------------------------------------------------------------

        /// <summary>The settlement factor actually applied right now: the formula when this module
        /// is live, a flat 1 otherwise.</summary>
        internal static float Factor()
        {
            return Live() ? FactorRaw() : 1f;
        }

        /// <summary>The formula on its own, ignoring the Live() gate - so the summary line and the
        /// self-test can show the real arithmetic on a headless server, where there is no client
        /// for the module to be live on.</summary>
        internal static float FactorRaw()
        {
            float per = _perTierDiscount != null ? _perTierDiscount.Value : 0f;
            float max = _maxDiscount != null ? _maxDiscount.Value : 0f;
            if (float.IsNaN(per) || per <= 0f) return 1f;
            if (float.IsNaN(max)) max = 0f;
            max = Mathf.Clamp(max, 0f, 0.95f);

            float f = 1f - per * Frontier.WorldTier;
            float floor = 1f - max;
            if (f < floor) f = floor;
            return Mathf.Clamp(f, 0.01f, 1f);
        }

        /// <summary>The factor for one specific piece, honouring ExcludePieces / OnlyCategories.
        /// Returns 1 (no discount) for anything this module does not cover, including a null piece
        /// - which is what a crafting recipe's context looks like.</summary>
        internal static float FactorFor(Piece piece)
        {
            if (piece == null || !Live()) return 1f;

            if (_excluded.Count > 0)
            {
                var go = piece.gameObject;
                if (go != null && _excluded.Contains(Utils.GetPrefabName(go))) return 1f;
            }

            if (_categories.Count > 0 && !_categories.Contains(piece.m_category)) return 1f;

            return Factor();
        }

        /// <summary>MinAmount, or 0 when this module is not contributing - so the shared rounding
        /// can take the strictest floor of whichever discounts are actually in play.</summary>
        internal static int MinAmountFloor()
        {
            if (!Live() || _minAmount == null) return 0;
            return Mathf.Max(0, _minAmount.Value);
        }

        // ---- reporting ------------------------------------------------------------------------------

        private string Numbers()
        {
            return "PerTierDiscount=" + _perTierDiscount.Value.ToString("0.##", CultureInfo.InvariantCulture) +
                   " MaxDiscount=" + _maxDiscount.Value.ToString("0.##", CultureInfo.InvariantCulture) +
                   " MinAmount=" + _minAmount.Value +
                   " tier=" + Frontier.WorldTier +
                   " -> x" + FactorRaw().ToString("0.00", CultureInfo.InvariantCulture) +
                   " (build pieces)" + (Live() ? "" : " [inactive on this half]") +
                   (_excluded.Count > 0 ? " excluded=" + _excluded.Count : "") +
                   (_categories.Count > 0 ? " categories=" + string.Join(",", CategoryNames()) : "");
        }

        private static string[] CategoryNames()
        {
            var names = new string[_categories.Count];
            for (int i = 0; i < _categories.Count; i++) names[i] = _categories[i].ToString();
            return names;
        }

        public override string StatusDetail() { return Numbers(); }

        private static void WorldReady()
        {
            if (_reported || _self == null || !_self.Active) return;
            _reported = true;
            try
            {
                Log.LogInfo("[SettlementDiscount] " + _self.Numbers());
                if (_selfTest != null && _selfTest.Value)
                    foreach (var line in SelfTest().Split('\n')) Log.LogInfo(line);
            }
            catch (Exception e)
            {
                Log.LogError("[SettlementDiscount] world-ready report failed: " + e);
            }
        }

        /// <summary>
        /// Five real build pieces out of ZNetScene, priced before and after. Deliberately prices
        /// them through the SHARED function TrailingTierDiscountModule uses at runtime, so the
        /// table proves the composed tier x settlement factor and the rounding, not a copy of it.
        ///
        /// Live() is false on a headless server (no client), so the table names the factor the
        /// formula WOULD produce and says so.
        /// </summary>
        internal static string SelfTest()
        {
            var sb = new StringBuilder();
            float wouldBe = FactorRaw();
            sb.Append("[SelfTest][SettlementDiscount] ").Append(Frontier.Describe())
              .Append(" PerTierDiscount=")
              .Append((_perTierDiscount != null ? _perTierDiscount.Value : 0f).ToString("0.##", CultureInfo.InvariantCulture))
              .Append(" MaxDiscount=")
              .Append((_maxDiscount != null ? _maxDiscount.Value : 0f).ToString("0.##", CultureInfo.InvariantCulture))
              .Append(" MinAmount=").Append(_minAmount != null ? _minAmount.Value : -1)
              .Append(" -> settlement factor x").Append(wouldBe.ToString("0.00", CultureInfo.InvariantCulture))
              .Append(Live() ? " (live)" : " (would be; no client on this half)");

            var scene = ZNetScene.instance;
            if (scene == null)
            {
                sb.Append("\n  ZNetScene has no prefabs yet - cannot price anything");
                return sb.ToString();
            }

            // Candidates, not a fixed five: the table prints the first five that this game build
            // actually has, so a renamed prefab shrinks the sample instead of blanking a row.
            string[] candidates =
            {
                "wood_wall", "woodwall", "wood_floor", "wood_door", "wood_stair",
                "wood_beam", "wood_pole", "stone_wall_2x1", "piece_workbench", "piece_chest_wood"
            };
            int priced = 0;
            var absent = new List<string>();
            foreach (var want in candidates)
            {
                if (priced >= 5) break;
                var go = scene.GetPrefab(want.GetStableHashCode());
                var piece = go == null ? null : go.GetComponent<Piece>();
                if (piece == null || piece.m_resources == null || piece.m_resources.Length == 0)
                {
                    absent.Add(want);
                    continue;
                }
                priced++;

                int tier = Tiers.OfPiece(piece);
                bool behind = Tiers.IsBehind(tier);
                float tierMult = behind ? TrailingTierDiscountModule.MultiplierFor(tier) : 1f;
                float mult = tierMult * wouldBe;

                sb.Append("\n  ").Append(want)
                  .Append(": category=").Append(piece.m_category)
                  .Append(" pieceTier=").Append(tier)
                  .Append(behind ? " BEHIND x" + tierMult.ToString("0.##", CultureInfo.InvariantCulture)
                                 : " at/ahead of the frontier")
                  .Append(" * settlement x").Append(wouldBe.ToString("0.00", CultureInfo.InvariantCulture))
                  .Append(" = x").Append(mult.ToString("0.###", CultureInfo.InvariantCulture))
                  .Append(" ->");
                foreach (var req in piece.m_resources)
                {
                    if (req == null || req.m_resItem == null) continue;
                    int vanilla = req.m_amount;
                    sb.Append(' ').Append(Tiers.CleanName(req.m_resItem.name)).Append(' ')
                      .Append(vanilla).Append("->")
                      .Append(TrailingTierDiscountModule.ScaledAmount(vanilla, mult))
                      .Append(" (refund ")
                      .Append(TrailingTierDiscountModule.ScaledAmount(vanilla, mult))
                      .Append(");");
                }
            }

            sb.Append("\n  ").Append(priced).Append(" piece(s) priced");
            if (absent.Count > 0)
                sb.Append("; not build pieces in this build (skipped): ")
                  .Append(string.Join(", ", absent.ToArray()));
            sb.Append(". Refund == cost by construction: both go through the same ")
              .Append("TrailingTierDiscountModule.ScaledAmount(amount, PieceCostFactor(piece)).");
            return sb.ToString();
        }

    }
}
