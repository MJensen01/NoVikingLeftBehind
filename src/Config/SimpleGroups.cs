using System;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// The plain-language headings of the settings tab's **Simple** view, and the order they are
    /// shown in.
    ///
    /// They live here, once, for two reasons: ~60 bind sites reference a group by
    /// <c>Opt.Simple(SimpleGroups.X)</c> and so cannot misspell one, and reordering the page is a
    /// one-line edit of <see cref="Order"/> rather than a sweep through the modules. The names are
    /// written for someone who has never opened a config file - they deliberately do not match
    /// <c>FeatureModule.Theme</c>, which groups *modules* for the Advanced view.
    ///
    /// Nothing here is serialised: like <see cref="SettingLevel"/> this is compile-time metadata,
    /// so adding, renaming or reordering a group cannot touch anyone's cfg file.
    /// </summary>
    internal static class SimpleGroups
    {
        public const string Progression = "Progression & XP";
        public const string Inventory = "Inventory & carrying";
        public const string Crafting = "Crafting from storage";
        public const string Building = "Building costs";
        public const string Gathering = "Gathering & the world";
        public const string Death = "Death & getting back";
        public const string Server = "Server & safety";

        /// <summary>Every group, in the order the Simple view lists them.</summary>
        public static readonly string[] Order =
        {
            Progression, Inventory, Crafting, Building, Gathering, Death, Server
        };

        /// <summary>Position in <see cref="Order"/>, or -1 for a name that is not a group.</summary>
        public static int IndexOf(string group)
        {
            if (string.IsNullOrEmpty(group)) return -1;
            for (int i = 0; i < Order.Length; i++)
                if (string.Equals(Order[i], group, StringComparison.Ordinal)) return i;
            return -1;
        }

        /// <summary>True when this is one of the seven groups. The self-test's first check.</summary>
        public static bool IsKnown(string group)
        {
            return IndexOf(group) >= 0;
        }
    }
}
