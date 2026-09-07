using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// Storage and book-keeping for the player's guardian-power slots.
    ///
    /// Slot 0 is vanilla: Player.m_guardianPower / m_guardianSE / m_guardianPowerCooldown, saved
    /// by Player.Save/Load like it always was. Slots 1..N-1 are ours and live in
    /// Player.m_customData, a Dictionary&lt;string,string&gt; that the vanilla profile writer
    /// persists verbatim (Player.Save writes m_customData.Count then every key/value pair;
    /// Player.Load reads them back for save version >= 26 - verified in the 0.221.12 decompile,
    /// Player.cs:4384 and :4586). So the second power survives a relog with **no sidecar file and
    /// no save-format change**; a vanilla client that loads the same profile simply keeps two
    /// unknown strings in its custom data and ignores them.
    ///
    /// Keys, per the spec: slot 1 -> "nvlb.gp2" (power name) and "nvlb.gp2cd" (cooldown, seconds
    /// remaining, invariant-culture float - the same representation vanilla persists for slot 0).
    /// A third slot would be "nvlb.gp3"/"nvlb.gp3cd" and needs no code change here.
    ///
    /// Everything is local-player only: the extra slots are never replicated, exactly like the
    /// vanilla one, which is a purely client-side profile field.
    /// </summary>
    internal static class PowerSlots
    {
        /// <summary>Total slots the storage layer understands, vanilla slot included.</summary>
        public const int MaxSlots = 3;

        private const string KeyPrefix = "nvlb.gp";

        // --- pushed in by DualPowersModule.Bind/OnConfigChanged -------------------------------
        public static int SlotCount = 2;
        public static bool IndependentCooldowns = true;
        public static float CooldownMultiplier = 1f;

        /// <summary>Number of NON-vanilla slots currently in use (0 when Slots = 1).</summary>
        public static int ExtraCount
        {
            get { return Mathf.Clamp(SlotCount - 1, 0, MaxSlots - 1); }
        }

        private sealed class Extra
        {
            public string Name = "";
            public int Hash;
            public StatusEffect Se;
            public float Cooldown;
        }

        private static readonly Extra[] Extras = NewExtras();
        private static Player _owner;

        private static Extra[] NewExtras()
        {
            var a = new Extra[MaxSlots - 1];
            for (int i = 0; i < a.Length; i++) a[i] = new Extra();
            return a;
        }

        // ---- key helpers -------------------------------------------------------------------

        /// <summary>customData key holding slot's power name. Slot 1 -> "nvlb.gp2".</summary>
        public static string NameKey(int slot) { return KeyPrefix + (slot + 1); }

        /// <summary>customData key holding slot's remaining cooldown. Slot 1 -> "nvlb.gp2cd".</summary>
        public static string CooldownKey(int slot) { return KeyPrefix + (slot + 1) + "cd"; }

        public static string EncodeCooldown(float seconds)
        {
            return Mathf.Max(0f, seconds).ToString("0.###", CultureInfo.InvariantCulture);
        }

        public static float DecodeCooldown(string s)
        {
            float v;
            if (string.IsNullOrEmpty(s)) return 0f;
            if (!float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return 0f;
            return (v > 0f && !float.IsNaN(v) && !float.IsInfinity(v)) ? v : 0f;
        }

        // ---- owner binding -----------------------------------------------------------------

        /// <summary>
        /// Make sure our in-memory slots belong to <paramref name="p"/>. A new Player object
        /// (first spawn, respawn, world change) re-reads them straight out of its custom data.
        /// </summary>
        public static void Bind(Player p)
        {
            if (p == null || ReferenceEquals(_owner, p)) return;
            _owner = p;
            LoadFrom(p);
        }

        public static bool IsOwner(Player p) { return p != null && ReferenceEquals(_owner, p); }

        /// <summary>Read every extra slot out of the player's custom data.</summary>
        public static void LoadFrom(Player p)
        {
            for (int i = 0; i < Extras.Length; i++)
            {
                Extras[i].Name = "";
                Extras[i].Hash = 0;
                Extras[i].Se = null;
                Extras[i].Cooldown = 0f;
            }
            if (p == null || p.m_customData == null) return;

            for (int slot = 1; slot <= Extras.Length; slot++)
            {
                string name;
                if (!p.m_customData.TryGetValue(NameKey(slot), out name) || string.IsNullOrEmpty(name))
                    continue;
                var e = Extras[slot - 1];
                e.Name = name;
                e.Hash = name.GetStableHashCode();
                e.Se = null;                       // resolved lazily, ObjectDB may not be ready yet
                string cd;
                p.m_customData.TryGetValue(CooldownKey(slot), out cd);
                e.Cooldown = DecodeCooldown(cd);
            }
        }

        /// <summary>Write every extra slot back into the player's custom data (call before Save).</summary>
        public static void SaveTo(Player p)
        {
            if (p == null || p.m_customData == null) return;
            for (int slot = 1; slot <= Extras.Length; slot++)
            {
                var e = Extras[slot - 1];
                if (string.IsNullOrEmpty(e.Name))
                {
                    p.m_customData.Remove(NameKey(slot));
                    p.m_customData.Remove(CooldownKey(slot));
                }
                else
                {
                    p.m_customData[NameKey(slot)] = e.Name;
                    p.m_customData[CooldownKey(slot)] = EncodeCooldown(GetCooldown(p, slot));
                }
            }
        }

        // ---- slot accessors ----------------------------------------------------------------

        public static string GetName(Player p, int slot)
        {
            if (slot == 0) return p != null ? p.m_guardianPower : "";
            if (p == null || slot < 1 || slot > Extras.Length) return "";
            return Extras[slot - 1].Name ?? "";
        }

        public static StatusEffect GetSe(Player p, int slot)
        {
            if (slot == 0) return p != null ? p.m_guardianSE : null;
            if (p == null || slot < 1 || slot > Extras.Length) return null;
            var e = Extras[slot - 1];
            if (e.Se == null && e.Hash != 0 && ObjectDB.instance != null)
                e.Se = ObjectDB.instance.GetStatusEffect(e.Hash);
            return e.Se;
        }

        public static float GetCooldown(Player p, int slot)
        {
            if (p == null) return 0f;
            if (slot == 0 || !IndependentCooldowns) return p.m_guardianPowerCooldown;
            if (slot < 1 || slot > Extras.Length) return 0f;
            return Extras[slot - 1].Cooldown;
        }

        public static void SetCooldown(Player p, int slot, float seconds)
        {
            if (p == null) return;
            seconds = Mathf.Max(0f, seconds);
            if (!IndependentCooldowns)
            {
                // Shared vanilla cooldown: one timer for every slot.
                p.m_guardianPowerCooldown = seconds;
                for (int i = 0; i < Extras.Length; i++) Extras[i].Cooldown = seconds;
                return;
            }
            if (slot == 0) { p.m_guardianPowerCooldown = seconds; return; }
            if (slot < 1 || slot > Extras.Length) return;
            Extras[slot - 1].Cooldown = seconds;
        }

        /// <summary>Put a power into a slot. Slot 0 goes through vanilla SetGuardianPower.</summary>
        public static void SetPower(Player p, int slot, string name)
        {
            if (p == null) return;
            if (slot == 0) { p.SetGuardianPower(name ?? ""); return; }
            if (slot < 1 || slot > Extras.Length) return;

            Bind(p);
            var e = Extras[slot - 1];
            e.Name = name ?? "";
            e.Hash = string.IsNullOrEmpty(e.Name) ? 0 : e.Name.GetStableHashCode();
            e.Se = (e.Hash != 0 && ObjectDB.instance != null) ? ObjectDB.instance.GetStatusEffect(e.Hash) : null;
            e.Cooldown = 0f;

            // Vanilla SetGuardianPower also records the power as a "unique key" (that is what makes
            // the boss-stone hover text and the compendium remember it). Mirror it.
            try { if (e.Hash != 0 && ZoneSystem.instance != null) p.AddUniqueKey(e.Name); }
            catch (Exception ex) { NoVikingLeftBehindPlugin.Log.LogWarning("[DualPowers] AddUniqueKey failed: " + ex.Message); }

            SaveTo(p);
        }

        /// <summary>Which slot already holds this power? -1 for none.</summary>
        public static int FindSlot(Player p, string name)
        {
            if (p == null || string.IsNullOrEmpty(name)) return -1;
            if (GetName(p, 0) == name) return 0;
            for (int slot = 1; slot <= ExtraCount; slot++)
                if (GetName(p, slot) == name) return slot;
            return -1;
        }

        /// <summary>Lowest slot with no power in it, or -1 when every configured slot is full.</summary>
        public static int FirstEmptySlot(Player p)
        {
            if (p == null) return -1;
            if (string.IsNullOrEmpty(GetName(p, 0))) return 0;
            for (int slot = 1; slot <= ExtraCount; slot++)
                if (string.IsNullOrEmpty(GetName(p, slot))) return slot;
            return -1;
        }

        // ---- ticking -----------------------------------------------------------------------

        /// <summary>Runs alongside vanilla Player.UpdateGuardianPower(dt) with the same dt.</summary>
        public static void Tick(Player p, float dt)
        {
            if (p == null) return;
            if (!IndependentCooldowns)
            {
                // Vanilla already decremented its own timer; keep our copies in step so that a
                // save made in shared mode still round-trips a sane number.
                for (int i = 0; i < Extras.Length; i++) Extras[i].Cooldown = p.m_guardianPowerCooldown;
                return;
            }
            for (int i = 0; i < ExtraCount; i++)
            {
                if (Extras[i].Cooldown <= 0f) continue;
                Extras[i].Cooldown -= dt;
                if (Extras[i].Cooldown < 0f) Extras[i].Cooldown = 0f;
            }
        }

        /// <summary>Shorten every EXTRA slot's cooldown. No-op in shared-cooldown mode.</summary>
        public static void ReduceCooldowns(float seconds)
        {
            if (seconds <= 0f || !IndependentCooldowns) return;
            for (int i = 0; i < ExtraCount; i++)
            {
                if (Extras[i].Cooldown <= 0f) continue;
                Extras[i].Cooldown = Mathf.Max(0f, Extras[i].Cooldown - seconds);
            }
        }

        public static void ResetCooldowns()
        {
            for (int i = 0; i < Extras.Length; i++) Extras[i].Cooldown = 0f;
        }

        /// <summary>Drop cached StatusEffect references - ObjectDB was rebuilt underneath us.</summary>
        public static void InvalidateStatusEffects()
        {
            for (int i = 0; i < Extras.Length; i++) Extras[i].Se = null;
        }

        // ---- reporting ---------------------------------------------------------------------

        public static string Describe(Player p)
        {
            var sb = new List<string>();
            for (int slot = 0; slot < Mathf.Clamp(SlotCount, 1, MaxSlots); slot++)
            {
                string name = GetName(p, slot);
                float cd = GetCooldown(p, slot);
                sb.Add("slot" + (slot + 1) + "=" + (string.IsNullOrEmpty(name) ? "-" : name) +
                       (string.IsNullOrEmpty(name) ? "" : (cd > 0f ? "(" + cd.ToString("0") + "s)" : "(ready)")));
            }
            return string.Join(" ", sb.ToArray());
        }
    }
}
