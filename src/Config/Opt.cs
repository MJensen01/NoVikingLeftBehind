using System;

namespace NoVikingLeftBehind
{
    /// <summary>Who is allowed to change a setting from the in-game settings tab.</summary>
    internal enum SettingTier
    {
        /// <summary>Any player on the server (subject to [Access] TweakAccess).</summary>
        Everyone,
        /// <summary>Only a player listed in the server's adminlist.txt.</summary>
        Admin
    }

    /// <summary>
    /// Per-setting metadata, attached at bind time so one pass over the modules describes every
    /// setting once for every consumer: the in-game settings tab, <c>nvlb.catalog</c>, and the
    /// server-side tweak door's validation.
    ///
    /// Deliberately tiny and fluent so the ~200 bind sites stay readable:
    /// <code>
    ///   BindSynced("RegrowDays", 7f, "long description...",
    ///              Opt.N("Days before a mined ore node comes back", 0, 60, 0.5f));
    ///   BindSynced("NoBuildCost", false, "...", Opt.B("Building costs nothing").Admin());
    ///   BindSynced("OreNodes", "rock4_copper,...", "...", Opt.T("Prefab names that regrow"));
    ///   BindLocal("Align", Side.Right, "...", Opt.C("Which side the panel sits on"));
    /// </code>
    ///
    /// Nothing here is passed to BepInEx as an <c>AcceptableValueRange</c>: BepInEx *clamps* to an
    /// acceptable range on load, which would silently rewrite a server's existing cfg if a range
    /// picked here turned out to be too tight. The range is advisory for the UI and enforced only
    /// by the tweak door, where a rejection is visible and reversible.
    /// </summary>
    internal sealed class Opt
    {
        /// <summary>Short plain-language effect, &lt;= 12 words. Shown under the row's label.</summary>
        public string Hint;

        /// <summary>False when a change only takes effect after a restart. Row is greyed out.</summary>
        public bool Live = true;

        public bool HasRange;
        public double Min;
        public double Max;
        public double Step;

        /// <summary>Explicit choice list for string settings. Enums fill this in automatically.</summary>
        public string[] Choices;

        public SettingTier Tier = SettingTier.Everyone;

        /// <summary>Friendly label. Null = derived from the key by splitting CamelCase.</summary>
        public string Label;

        /// <summary>Free text whose contents the owning module validates (prefab lists etc).</summary>
        public bool FreeText;

        // ---- factories -------------------------------------------------------------------

        /// <summary>A boolean.</summary>
        public static Opt B(string hint)
        {
            return new Opt { Hint = hint };
        }

        /// <summary>A number with a sane slider range. step 0 = inferred from the value type.</summary>
        public static Opt N(string hint, double min, double max, double step = 0)
        {
            return new Opt { Hint = hint, HasRange = true, Min = min, Max = max, Step = step };
        }

        /// <summary>A free-text string the owning module parses (prefab lists, colour hex, ...).</summary>
        public static Opt T(string hint)
        {
            return new Opt { Hint = hint, FreeText = true };
        }

        /// <summary>A pick-one. Pass the choices for a string setting; enums fill themselves in.</summary>
        public static Opt C(string hint, params string[] choices)
        {
            return new Opt { Hint = hint, Choices = (choices != null && choices.Length > 0) ? choices : null };
        }

        // ---- modifiers -------------------------------------------------------------------

        /// <summary>Only an admin (adminlist.txt) may change this.</summary>
        public Opt Admin() { Tier = SettingTier.Admin; return this; }

        /// <summary>A change only takes effect after a restart; the row is greyed out.</summary>
        public Opt Restart() { Live = false; return this; }

        /// <summary>Override the label derived from the key.</summary>
        public Opt As(string label) { Label = label; return this; }

        /// <summary>Attach a range to a setting created with another factory.</summary>
        public Opt Range(double min, double max, double step = 0)
        {
            HasRange = true; Min = min; Max = max; Step = step; return this;
        }
    }
}
