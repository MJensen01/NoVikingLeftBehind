using System;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// Where a list setting's candidates come from. Each one is resolved on a CLIENT at the
    /// moment the settings menu is opened, out of <c>ObjectDB</c> and <c>ZNetScene</c>, so the
    /// list always matches the world actually being played - including whatever other mods have
    /// added to it. On the main menu neither exists yet and the picker says so.
    /// </summary>
    internal enum PickerSource
    {
        /// <summary>Mineable ore nodes: MineRock / MineRock5 / Destructible prefabs that can be mined.</summary>
        OreNodes,
        /// <summary>Every item in ObjectDB. Large - the picker leans on its search box.</summary>
        Items,
        /// <summary>Items that are crafting materials.</summary>
        Materials,
        /// <summary>Items that can be eaten.</summary>
        Foods,
        /// <summary>Torches and anything else that burns in the hand.</summary>
        Torches,
        /// <summary>Crafting stations (workbench, forge, ...).</summary>
        Stations,
        /// <summary>Stations food is cooked on.</summary>
        CookingStations,
        /// <summary>Anything with a Fireplace on it - hearths, torches on walls, braziers.</summary>
        Fireplaces,
        /// <summary>Buildable pieces.</summary>
        Pieces,
        /// <summary>EnvMan environments (weather).</summary>
        Envs,
        /// <summary>The wandering traders.</summary>
        Traders
    }

    /// <summary>
    /// One number carried alongside a chosen entry - the tier on an ore node, the multiplier on a
    /// station, the stack and price on a trader's stock. Rendered as a small box next to the
    /// entry's name, enabled only while the entry is ticked.
    /// </summary>
    internal sealed class PickerField
    {
        public string Label;
        public double Min;
        public double Max;
        public double Default;
        public bool WholeNumbers;

        public PickerField(string label, double min, double max, double def, bool whole)
        {
            Label = label; Min = min; Max = max; Default = def; WholeNumbers = whole;
        }
    }

    /// <summary>
    /// How to edit a comma-separated list setting with a list of tick boxes instead of a text
    /// field. Attached to a setting through <see cref="Opt.Pick"/>.
    ///
    /// The point of this type is that **no module changes how it parses its own setting**. A
    /// picker knows how to take the setting's existing string apart and put it back together in
    /// exactly the same shape, and that round trip is asserted byte-for-byte by the self test.
    /// An entry already in the value that the picker cannot find in this world - a prefab from a
    /// mod that is not loaded, say - is never dropped: it is kept, listed first, and flagged.
    /// </summary>
    internal sealed class PickerSpec
    {
        /// <summary>Where the candidate names come from.</summary>
        public PickerSource Source;

        /// <summary>Provider-specific narrowing, or null. Free-form; each provider documents its own.</summary>
        public string Filter;

        /// <summary>
        /// The numbers each chosen entry carries, in the order they appear after the name.
        /// Empty for a plain list of names.
        /// </summary>
        public PickerField[] Fields;

        /// <summary>What separates one entry from the next in the stored string.</summary>
        public char EntrySeparator = ',';

        /// <summary>What separates an entry's name from its numbers.</summary>
        public char FieldSeparator = ':';

        /// <summary>
        /// True when a trailing field may be left off - `prefab:tier` and `prefab:tier:respawn`
        /// are both legal for the same setting. The serialiser then writes the shortest form that
        /// round-trips.
        /// </summary>
        public bool TrailingFieldsOptional;

        /// <summary>
        /// A last field that names another PREFAB rather than a number (OreRegrowth's
        /// `fractured:tier:whole`). Null when there is none.
        /// </summary>
        public string TrailingPrefabLabel;

        public PickerSpec(PickerSource source, params PickerField[] fields)
        {
            Source = source;
            Fields = fields ?? new PickerField[0];
        }

        public PickerSpec With(char entrySep, char fieldSep)
        {
            EntrySeparator = entrySep; FieldSeparator = fieldSep; return this;
        }

        public PickerSpec Optional() { TrailingFieldsOptional = true; return this; }

        public PickerSpec ThenPrefab(string label) { TrailingPrefabLabel = label; return this; }

        public PickerSpec Narrow(string filter) { Filter = filter; return this; }
    }
}
