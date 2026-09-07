using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// Fires burn fuel far slower (campfires, hearths, standing/wall torches, braziers, sconces -
    /// anything with a Fireplace component, since that is the only thing that distinguishes a fire
    /// from any other piece), and hand-held torches drain far slower too.
    ///
    /// Patch points, both verified in the 0.221.12 decompile:
    ///
    ///   Fireplace.Awake() postfix - runs once per Fireplace component, for every fire already
    ///   standing in a zone as it loads and for every new one placed. UpdateFireplace() consumes
    ///   fuel as `elapsedSeconds / m_secPerFuel` on the object's OWNER only (unchanged - still one
    ///   client per fire does the math, same as vanilla), so multiplying m_secPerFuel directly
    ///   makes fuel last that many times longer with no other change to the object's behaviour.
    ///   The vanilla value is cached in a dictionary keyed by PREFAB NAME (Tiers.CleanName), not
    ///   by the live component: the vanilla value is a prefab-level constant (every "piece_bonfire"
    ///   has the same design-time m_secPerFuel), and keying on the component itself would either
    ///   leak (a strong Dictionary key holds the GameObject's C# wrapper alive after ZNetView
    ///   destroys it on zone unload) or need extra teardown bookkeeping for no benefit.
    ///   [InfiniteFuel] flips the existing public m_infiniteFuel bool the same way, restoring each
    ///   prefab's OWN vanilla value (also cached) when turned back off, rather than assuming it was
    ///   false for every prefab.
    ///
    ///   Hot reload (FuelDurationMultiplier / InfiniteFuel changing) re-applies straight from the
    ///   cached per-prefab originals to every Fireplace currently loaded
    ///   (UnityEngine.Object.FindObjectsOfType&lt;Fireplace&gt;) instead of only affecting fires
    ///   placed after the change - an existing fire mid-burn keeps whatever absolute fuel amount
    ///   it already has (m_secPerFuel only changes the RATE fuel drains at from here on).
    ///
    ///   ObjectDB.UpdateRegisters() postfix - the single private funnel both ObjectDB.Awake() and
    ///   CopyOtherDB() call after m_items is populated, so one patch point covers "this client's
    ///   own ObjectDB woke up" and "the server just pushed its ObjectDB to this client" (same
    ///   pattern §12/§13 use for other client modules). For every prefab named in
    ///   [Fires] HandTorchItems, the vanilla m_durabilityDrain (durability lost PER SECOND while
    ///   equipped - Humanoid.DrainEquipedItemDurability() does
    ///   `item.m_durability -= item.m_shared.m_durabilityDrain * dt`, gated on
    ///   `m_shared.m_useDurability`, called from Humanoid.UpdateEquipment() for every equipped
    ///   slot including the held right-hand torch) is divided by HandTorchDurabilityMultiplier.
    ///   Every ItemData.Clone() of a given prefab (Humanoid/Inventory items are MemberwiseClone()d)
    ///   keeps pointing at the exact same SharedData object as the prefab in ObjectDB, so mutating
    ///   it here reaches every copy already sitting in every inventory too - deliberately, unlike
    ///   PortalTrail's m_teleportable, because m_durabilityDrain has no other reader whose
    ///   behaviour this would surprise (it is only ever read from this one drain calculation).
    /// </summary>
    internal sealed class LongFiresModule : FeatureModule
    {
        public override string Name => "LongFires";
        public override ModuleSide Side => ModuleSide.Client;
        public override string Section => "Fires";

        private ConfigEntry<float> _fuelMult;
        private ConfigEntry<bool> _infiniteFuel;
        private ConfigEntry<float> _torchMult;
        private ConfigEntry<string> _torchItems;

        private static LongFiresModule _self;

        // Keyed by prefab name (Tiers.CleanName), NOT by the live component/GameObject - see the
        // class doc for why. Values captured the first time each prefab is seen this session.
        private static readonly Dictionary<string, float> _origSecPerFuel =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, bool> _origInfiniteFuel =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, float> _origDurabilityDrain =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        private static HashSet<string> _torchSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static bool Live() { return _self != null && _self.Active && ClientActive(); }

        protected override void Bind()
        {
            _self = this;

            _fuelMult = BindSynced("FuelDurationMultiplier", 5f,
                "Campfires, hearths, standing/wall torches, braziers and sconces (anything with a " +
                "Fireplace component) consume fuel this many times slower. Fireplace.m_secPerFuel " +
                "is multiplied on Awake for every fire already standing and every new one placed. " +
                "1 = vanilla.");

            _infiniteFuel = BindSynced("InfiniteFuel", false,
                "Every Fireplace (campfires, hearths, torches, braziers, sconces) never runs out " +
                "of fuel, on top of whatever FuelDurationMultiplier is set to. False restores each " +
                "prefab's own vanilla m_infiniteFuel value.");

            _torchMult = BindSynced("HandTorchDurabilityMultiplier", 5f,
                "Hand-held torch items (named in HandTorchItems) lose durability this many times " +
                "slower while equipped (Humanoid.DrainEquipedItemDurability). 1 = vanilla.");

            _torchItems = BindSynced("HandTorchItems", "Torch,TorchMist",
                "Comma-separated ItemDrop prefab names treated as hand torches for " +
                "HandTorchDurabilityMultiplier. Verified against a live ObjectDB (0.221.12): " +
                "Torch and TorchMist both drain durability over time while equipped; Sparkler " +
                "does too but is a firework, not a light source, so it is deliberately left out " +
                "of the default. [World] SelfTest logs every item in ObjectDB with " +
                "m_useDurability and a positive m_durabilityDrain as a candidate, so this list can " +
                "be extended to cover any other mod's torch-like items.");

            ParseTorchItems();
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            if (entry == _torchItems) ParseTorchItems();
            if ((entry == _fuelMult || entry == _infiniteFuel) && Active && ClientActive()) ReapplyFireplaces();
            if ((entry == _torchMult || entry == _torchItems) && Active && ClientActive()) ApplyTorchDrain();
            if (Active) Log.LogInfo("[" + Name + "] " + Numbers());
        }

        private void ParseTorchItems()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var raw = _torchItems != null ? _torchItems.Value : "Torch,TorchMist";
            foreach (var chunk in (raw ?? "").Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var name = chunk.Trim();
                if (name.Length > 0) set.Add(name);
            }
            _torchSet = set;
        }

        protected override void ApplyPatches()
        {
            var awake = AccessTools.Method(typeof(Fireplace), "Awake");
            if (awake == null) throw new Exception("Fireplace.Awake() not found");
            Harmony.Patch(awake, postfix: new HarmonyMethod(typeof(LongFiresModule), nameof(FireplaceAwakePost)));

            var upd = AccessTools.Method(typeof(ObjectDB), "UpdateRegisters", Type.EmptyTypes);
            if (upd == null) throw new Exception("ObjectDB.UpdateRegisters() not found");
            Harmony.Patch(upd, postfix: new HarmonyMethod(typeof(LongFiresModule), nameof(ObjectDBReadyPost)));

            Log.LogInfo("[" + Name + "] " + Numbers());
        }

        // ---- fireplaces ---------------------------------------------------------------------

        private static void FireplaceAwakePost(Fireplace __instance)
        {
            if (!Live() || __instance == null) return;
            ApplyToFireplace(__instance);
        }

        private static void ApplyToFireplace(Fireplace fp)
        {
            string prefab = Tiers.CleanName(fp.gameObject.name);

            float origFuel;
            if (!_origSecPerFuel.TryGetValue(prefab, out origFuel))
            {
                origFuel = fp.m_secPerFuel;
                _origSecPerFuel[prefab] = origFuel;
            }

            bool origInf;
            if (!_origInfiniteFuel.TryGetValue(prefab, out origInf))
            {
                origInf = fp.m_infiniteFuel;
                _origInfiniteFuel[prefab] = origInf;
            }

            float mult = Mathf.Max(0.01f, _self != null ? _self._fuelMult.Value : 1f);
            fp.m_secPerFuel = origFuel * mult;
            fp.m_infiniteFuel = origInf || (_self != null && _self._infiniteFuel.Value);
        }

        private static void ReapplyFireplaces()
        {
            var all = UnityEngine.Object.FindObjectsOfType<Fireplace>(true);
            foreach (var fp in all)
            {
                if (fp == null) continue;
                ApplyToFireplace(fp);
            }
            Log.LogInfo("[LongFires] reapplied fuel settings to " + all.Length + " loaded Fireplace instance(s)");
        }

        // ---- hand torches --------------------------------------------------------------------

        private static void ObjectDBReadyPost()
        {
            if (!Live()) return;
            ApplyTorchDrain();
        }

        private static void ApplyTorchDrain()
        {
            var odb = ObjectDB.instance;
            if (odb == null || odb.m_items == null) return;
            float mult = Mathf.Max(0.01f, _self != null ? _self._torchMult.Value : 1f);

            foreach (var name in _torchSet)
            {
                var go = odb.GetItemPrefab(name);
                var drop = go != null ? go.GetComponent<ItemDrop>() : null;
                var shared = drop != null && drop.m_itemData != null ? drop.m_itemData.m_shared : null;
                if (shared == null) continue;

                float orig;
                if (!_origDurabilityDrain.TryGetValue(name, out orig))
                {
                    orig = shared.m_durabilityDrain;
                    _origDurabilityDrain[name] = orig;
                }
                shared.m_durabilityDrain = orig / mult;
            }
        }

        // ---- reporting ------------------------------------------------------------------------

        private string Numbers()
        {
            return "FuelDurationMultiplier=x" + _fuelMult.Value +
                   " InfiniteFuel=" + _infiniteFuel.Value +
                   " HandTorchDurabilityMultiplier=x" + _torchMult.Value +
                   " HandTorchItems=[" + string.Join(",", new List<string>(_torchSet).ToArray()) + "]";
        }

        public override string StatusDetail() { return Numbers(); }

        /// <summary>Headless proof: every Fireplace prefab's numbers, and every candidate/configured
        /// time-based durability-drain item's numbers.</summary>
        internal static string SelfTest()
        {
            var sb = new StringBuilder();
            float fuelMult = _self != null ? _self._fuelMult.Value : 1f;
            float torchMult = _self != null ? _self._torchMult.Value : 1f;
            sb.Append("[SelfTest][LongFires] FuelDurationMultiplier=").Append(fuelMult)
              .Append(" InfiniteFuel=").Append(_self != null && _self._infiniteFuel.Value)
              .Append(" HandTorchDurabilityMultiplier=").Append(torchMult)
              .Append(" HandTorchItems=[").Append(string.Join(",", new List<string>(_torchSet).ToArray())).Append("]");

            var scene = ZNetScene.instance;
            if (scene == null || scene.m_prefabs == null)
            {
                sb.Append("\n  ZNetScene has no prefabs - cannot list Fireplace prefabs");
            }
            else
            {
                int count = 0;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var go in scene.m_prefabs)
                {
                    if (go == null) continue;
                    var fp = go.GetComponent<Fireplace>();
                    if (fp == null) continue;
                    string prefab = Tiers.CleanName(go.name);
                    if (!seen.Add(prefab)) continue;
                    count++;
                    float orig = fp.m_secPerFuel;
                    sb.Append("\n  Fireplace ").Append(prefab)
                      .Append(": secPerFuel ").Append(orig.ToString("0.##"))
                      .Append(" -> ").Append((orig * Mathf.Max(0.01f, fuelMult)).ToString("0.##"))
                      .Append("  maxFuel=").Append(fp.m_maxFuel.ToString("0.##"))
                      .Append("  infiniteFuel=").Append(fp.m_infiniteFuel);
                }
                sb.Append("\n  Fireplace prefabs: ").Append(count);
            }

            var odb = ObjectDB.instance;
            if (odb == null || odb.m_items == null)
            {
                sb.Append("\n  ObjectDB has no items - cannot list durability drain");
                return sb.ToString();
            }

            int drainHit = 0;
            foreach (var go in odb.m_items)
            {
                if (go == null) continue;
                var drop = go.GetComponent<ItemDrop>();
                var shared = drop != null && drop.m_itemData != null ? drop.m_itemData.m_shared : null;
                if (shared == null || !shared.m_useDurability || shared.m_durabilityDrain <= 0f) continue;
                drainHit++;

                string prefab = Tiers.CleanName(go.name);
                bool configured = _torchSet.Contains(prefab);

                // If the patch has already run on this half (a real client), _origDurabilityDrain
                // holds the true vanilla value and shared.m_durabilityDrain is already divided.
                // If it has not (server half, where LongFires is disabled(side)), the field is
                // still vanilla, so treat it as its own "original" for this preview.
                float orig;
                if (!_origDurabilityDrain.TryGetValue(prefab, out orig)) orig = shared.m_durabilityDrain;
                float preview = configured ? orig / Mathf.Max(0.01f, torchMult) : orig;

                sb.Append("\n  useDurabilityDrain ").Append(prefab)
                  .Append(": ").Append(orig.ToString("0.####")).Append("/s -> ")
                  .Append(preview.ToString("0.####")).Append("/s");
                sb.Append(configured ? " (configured torch)" : " (candidate, not in HandTorchItems)");
            }
            sb.Append("\n  useDurability items with time-based drain: ").Append(drainHit);
            return sb.ToString();
        }
    }
}
