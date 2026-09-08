using System;
using System.Globalization;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// Parsing and validating a setting's value from the string form that crosses the wire.
    /// Everything the tweak door accepts goes through here, on the SERVER, before anything is
    /// written - so a malformed or out-of-range value never reaches the cfg file, and the caller
    /// gets a sentence it can show a player.
    /// </summary>
    internal static class SettingValue
    {
        /// <summary>Canonical wire/file form of a value - BepInEx's own serialisation, so what
        /// the door writes to the cfg is byte-for-byte what BepInEx would have written.</summary>
        public static string Format(SettingInfo info, object value)
        {
            return ConfigCatalog.Serialize(value, info != null ? info.ValueType : null);
        }

        /// <summary>
        /// Parse <paramref name="raw"/> for <paramref name="info"/>.
        /// Returns null on success (and sets <paramref name="parsed"/>), otherwise the reason.
        /// </summary>
        public static string TryParse(SettingInfo info, string raw, out object parsed)
        {
            parsed = null;
            if (info == null) return "unknown setting";
            if (raw == null) return "no value";
            raw = raw.Trim();

            switch (info.TypeName)
            {
                case "bool":
                    {
                        bool b;
                        if (!TryParseBool(raw, out b)) return "expected true or false";
                        parsed = b;
                        return null;
                    }

                case "int":
                    {
                        // tolerate "5.0" from a slider that rounded through a float
                        double d;
                        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                            return "expected a whole number";
                        long l = (long)Math.Round(d);
                        string range = CheckRange(info, l);
                        if (range != null) return range;
                        if (info.ValueType == typeof(long)) parsed = l;
                        else
                        {
                            if (l < int.MinValue || l > int.MaxValue) return "number is too large";
                            parsed = (int)l;
                        }
                        return null;
                    }

                case "float":
                    {
                        double d;
                        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                            return "expected a number";
                        if (double.IsNaN(d) || double.IsInfinity(d)) return "expected a number";
                        string range = CheckRange(info, d);
                        if (range != null) return range;
                        parsed = info.ValueType == typeof(double) ? (object)d : (object)(float)d;
                        return null;
                    }

                case "enum":
                    {
                        foreach (var name in Enum.GetNames(info.ValueType))
                        {
                            if (!string.Equals(name, raw, StringComparison.OrdinalIgnoreCase)) continue;
                            parsed = Enum.Parse(info.ValueType, name);
                            return null;
                        }
                        return "expected one of: " + string.Join(", ", Enum.GetNames(info.ValueType));
                    }

                case "string":
                    {
                        // Take the quoting off before anything looks at it. A player who copies
                        // the example out of a hint types the quotes too, and an older build
                        // escaped what it stored - so "1,2", '1,2' and \"1,2\" all mean 1,2.
                        raw = ConfigCatalog.Unquote(raw);

                        if (raw.Length > 4000) return "that value is too long";
                        if (raw.IndexOf('\n') >= 0 || raw.IndexOf('\r') >= 0)
                            return "a value cannot span more than one line";
                        if (info.Choices != null)
                        {
                            foreach (var c in info.Choices)
                                if (string.Equals(c, raw, StringComparison.OrdinalIgnoreCase))
                                {
                                    parsed = c;
                                    return null;
                                }
                            return "expected one of: " + string.Join(", ", info.Choices);
                        }
                        // Free text (prefab lists, colour hex, key names): the owning module knows
                        // what its own strings mean, so it gets the last word.
                        if (info.Owner != null)
                        {
                            string moduleSays = null;
                            try { moduleSays = info.Owner.ValidateValue(info, raw); }
                            catch (Exception e) { moduleSays = "could not be checked (" + e.Message + ")"; }
                            if (!string.IsNullOrEmpty(moduleSays)) return moduleSays;
                        }
                        parsed = raw;
                        return null;
                    }

                default:
                    return "this setting cannot be changed from the menu";
            }
        }

        private static string CheckRange(SettingInfo info, double d)
        {
            if (!info.HasRange) return null;
            if (d < info.Min || d > info.Max)
                return "must be between " + Trim(info.Min) + " and " + Trim(info.Max);
            return null;
        }

        private static string Trim(double d)
        {
            return d == Math.Floor(d) && Math.Abs(d) < 1e15
                ? ((long)d).ToString(CultureInfo.InvariantCulture)
                : d.ToString("0.####", CultureInfo.InvariantCulture);
        }

        public static bool TryParseBool(string raw, out bool value)
        {
            switch (raw.Trim().ToLowerInvariant())
            {
                case "true": case "1": case "on": case "yes": value = true; return true;
                case "false": case "0": case "off": case "no": value = false; return true;
                default: value = false; return false;
            }
        }
    }
}
