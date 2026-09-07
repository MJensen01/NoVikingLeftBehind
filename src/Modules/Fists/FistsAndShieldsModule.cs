using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// Fist weapons may be worn with a shield.
    ///
    /// WHY THEY CANNOT BE, IN VANILLA
    /// ------------------------------
    /// Flesh Rippers (FistFenrirClaw) and every modded fist weapon are declared
    /// ItemType.TwoHandedWeapon. Humanoid.EquipItem (0.221.x, Humanoid.cs ~1130) has one branch
    /// per item type, and the TwoHandedWeapon branch unconditionally unequips BOTH hands before
    /// taking the right hand; the Shield branch symmetrically unequips the right hand unless it
    /// holds a OneHandedWeapon or a Torch. So equipping one drops the other. Bare fists - which
    /// are not an item at all, they are Humanoid.m_unarmedWeapon - work with a shield perfectly
    /// well, which is the proof that the unarmed animation set is shield-compatible.
    ///
    /// WHAT THIS MODULE DOES
    /// ---------------------
    /// One field, once, per item prefab: m_shared.m_itemType TwoHandedWeapon -> OneHandedWeapon,
    /// for every ItemDrop in ObjectDB whose skill is Skills.SkillType.Unarmed (plus anything named
    /// in ExtraPrefabs, minus anything in ExcludePrefabs). SharedData is shared by every instance
    /// of a prefab, so items already sitting in a chest or on a character pick the change up with
    /// no migration. Nothing else is touched: damage, skill, attack, animation state and block
    /// values are all untouched fields.
    ///
    /// EVERYTHING IN THE GAME THAT KEYS OFF THE OLD VALUE (grepped over the whole 0.221.13
    /// decompile - TwoHandedWeapon appears in exactly three files, IsTwoHanded in two):
    ///
    ///  1. Humanoid.EquipItem  - the point of the change. The item now takes the OneHandedWeapon
    ///     branch, which keeps a Shield or a Torch in the left hand, and the Shield branch keeps a
    ///     OneHandedWeapon in the right. Both directions work, so it does not matter which is
    ///     equipped first.
    ///  2. Humanoid.SetupAnimationState - reads m_leftItem.m_shared.m_animationState when the left
    ///     hand is full, else the right item's, else the unarmed weapon's. With a shield equipped
    ///     that is the SHIELD's state - exactly what vanilla bare-fists-plus-shield already does.
    ///     With no shield it is the fist weapon's own state, unchanged from before. The equip
    ///     animation trigger is type-independent.
    ///  3. Humanoid.GetCurrentBlocker - returns m_leftItem if there is one, else the current
    ///     weapon. Blocking with the fists themselves when no shield is held is therefore
    ///     unchanged (they were the right item before and still are); with a shield, the shield
    ///     blocks, as it should.
    ///  4. ItemDrop.ItemData.IsTwoHanded() - used in exactly one place, Humanoid.Pickup's
    ///     auto-equip check (Humanoid.cs:634), and only against the LEFT item / hidden left item.
    ///     Fist weapons live in the right hand, so the check is unaffected.
    ///  5. ItemDrop.ItemData.IsWeapon() / IsEquipable() - both list OneHandedWeapon and
    ///     TwoHandedWeapon, so both still answer true.
    ///  6. ItemDrop tooltips - AddHandedTip switches on the type: the tooltip line changes from
    ///     "$item_twohanded" to "$item_onehanded", which is now the truth. The damage/stamina
    ///     block of the tooltip lists both types, so it is unchanged.
    ///  6b. Humanoid.EquipItem's Torch branch (Humanoid.cs:1057) is the one place that treats
    ///     OneHandedWeapon specially in the other direction: a torch joins a one-handed weapon in
    ///     the LEFT hand instead of replacing it. So fists + torch now works too, exactly as
    ///     sword + torch does. Deliberate, and consistent.
    ///  7. VisEquipment.AttachBackItem - the BACK attach point for a holstered weapon moves from
    ///     m_backTwohandedMelee to m_backMelee (it honours m_shared.m_attachOverride first, which
    ///     these items do not set). This only shows while the weapon is hidden behind a
    ///     hammer/hoe. The IN-HAND visual is untouched: SetRightHandEquipped/SetLeftHandEquipped
    ///     attach by hand slot and item hash, never by type, and a fist weapon's "attach_skin"
    ///     child is skinned onto the body model rather than parented to a hand joint - which is
    ///     why the claws show on both hands at once and why that visual cannot be affected here.
    ///
    /// NOTHING SERIALISES THE ITEM TYPE. Inventory.Save writes prefab name / stack / durability /
    /// grid pos / equipped / quality / variant / crafter / custom data / world level, and
    /// ItemDrop.SaveToZDO writes that same package; m_shared is always resolved back from the
    /// prefab on load. So there is no ZDO, save file or network packet carrying the old value, and
    /// a client without the mod simply sees vanilla behaviour for its own equipping - which is why
    /// the settings are server-synced (so a group agrees) but ServerSync compatibility is a
    /// non-issue: the mutation is identical and local on every process that runs it.
    ///
    /// CREATURE CLAWS. The Unarmed sweep also catches monster weapons that happen to live in
    /// ObjectDB - on a modded Ashlands build, charred_twitcher_scratch_l/_r. That is harmless:
    /// a creature carries no shield and no torch, so the OneHandedWeapon and TwoHandedWeapon
    /// branches of EquipItem do exactly the same thing for it (unequip the right hand, take the
    /// right hand), and its damage, attack and animation state are untouched fields. Name them in
    /// ExcludePrefabs if you would rather they were left alone.
    ///
    /// SIDE = BOTH. The dedicated server never equips anything, so this is a client feature; but
    /// the mutation only touches shared prefab data the server also holds, and running it there
    /// gives a proof line at boot naming the prefabs it detected (the server's ObjectDB has every
    /// item, including modded ones), which is the only way to verify detection headlessly.
    /// </summary>
    internal sealed class FistsAndShieldsModule : FeatureModule
    {
        public override string Name => "FistsAndShields";
        public override ModuleSide Side => ModuleSide.Both;
        public override string Section => "Fists";

        private static FistsAndShieldsModule _self;

        private static ConfigEntry<string> _extraPrefabs;
        private static ConfigEntry<string> _excludePrefabs;

        private static HashSet<string> _extra = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static HashSet<string> _exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Every SharedData we changed, with the type it had. Keyed by reference (SharedData is a
        /// class and there is exactly one per item prefab), which is what makes a second pass over
        /// the same ObjectDB a no-op and lets a live disable put every type back.
        /// </summary>
        private static readonly Dictionary<ItemDrop.ItemData.SharedData, ItemDrop.ItemData.ItemType> _converted =
            new Dictionary<ItemDrop.ItemData.SharedData, ItemDrop.ItemData.ItemType>();

        /// <summary>Prefab names of everything currently converted, for the log and nvlb.status.</summary>
        private static readonly List<string> _convertedNames = new List<string>();

        // ---- config -----------------------------------------------------------------------

        protected override void Bind()
        {
            _self = this;

            _extraPrefabs = BindSynced("ExtraPrefabs", "",
                "Extra two-handed weapons to make one-handed, comma-separated. Only needed for a " +
                "fist weapon that does not use the Unarmed skill - everything with " +
                "SkillType.Unarmed is found automatically. Matched against the item's PREFAB name " +
                "and its shared item name, so either works. Example: " +
                "BlackMetalClawsSMR, SilverGlovesSMR");

            _excludePrefabs = BindSynced("ExcludePrefabs", "",
                "Fist weapons to LEAVE two-handed, comma-separated. Takes priority over " +
                "ExtraPrefabs and over the automatic Unarmed detection. Matched against the " +
                "prefab name and the shared item name. Example: FistFenrirClaw");

            ParseNames();
        }

        private static void ParseNames()
        {
            _extra = ChestSource.ParseNames(_extraPrefabs.Value);
            _exclude = ChestSource.ParseNames(_excludePrefabs.Value);
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            ParseNames();

            // The mutation is persistent state, not a per-call decision, so the Enabled toggle has
            // to be honoured here rather than by a gate inside a patch body: OFF puts every type
            // back immediately, ON re-runs the conversion (possible live only because the patches
            // are already installed - a module that BOOTED disabled still needs a restart).
            if (!Applied) return;
            if (Enabled) Convert(ObjectDB.instance, "config change");
            else Restore("[Fists] disabled");
        }

        // ---- patches ----------------------------------------------------------------------

        protected override void ApplyPatches()
        {
            var awake = AccessTools.Method(typeof(ObjectDB), "Awake");
            if (awake == null) throw new Exception("ObjectDB.Awake() not found");

            // CopyOtherDB REPLACES m_items with the other DB's list (ObjectDB.cs:28), so the
            // second DB has to be walked too - same reason CorpseRunPlus patches both.
            var copy = AccessTools.Method(typeof(ObjectDB), "CopyOtherDB", new[] { typeof(ObjectDB) });
            if (copy == null) throw new Exception("ObjectDB.CopyOtherDB(ObjectDB) not found");

            // Fail loudly if the field this module exists to change ever moves or is renamed.
            if (AccessTools.Field(typeof(ItemDrop.ItemData.SharedData), "m_itemType") == null)
                throw new Exception("ItemDrop.ItemData.SharedData.m_itemType not found");
            if (AccessTools.Field(typeof(ItemDrop.ItemData.SharedData), "m_skillType") == null)
                throw new Exception("ItemDrop.ItemData.SharedData.m_skillType not found");

            var post = new HarmonyMethod(typeof(FistsAndShieldsModule), nameof(ObjectDBPostfix));
            Harmony.Patch(awake, postfix: post);
            Harmony.Patch(copy, postfix: post);

            // ObjectDB.Awake may already have fired before the plugin's own Awake got this far.
            if (ObjectDB.instance != null) Convert(ObjectDB.instance, "already loaded");
        }

        public override void Disable()
        {
            Restore("[Fists] module disabled");
            base.Disable();
        }

        private static void ObjectDBPostfix(ObjectDB __instance)
        {
            if (_self == null || !_self.Active) return;
            Convert(__instance, "ObjectDB");
        }

        // ---- the change --------------------------------------------------------------------

        /// <summary>
        /// Walk ObjectDB.m_items and make every fist weapon one-handed. Idempotent: an item whose
        /// SharedData is already in _converted is skipped, and nothing is logged unless this pass
        /// converted something new.
        /// </summary>
        private static void Convert(ObjectDB odb, string why)
        {
            if (odb == null || odb.m_items == null) return;

            var justDone = new List<string>();
            try
            {
                foreach (var go in odb.m_items)
                {
                    if (go == null) continue;
                    var drop = go.GetComponent<ItemDrop>();
                    if (drop == null) continue;

                    var shared = drop.m_itemData != null ? drop.m_itemData.m_shared : null;
                    if (shared == null) continue;
                    if (_converted.ContainsKey(shared)) continue;                    // already ours
                    if (shared.m_itemType != ItemDrop.ItemData.ItemType.TwoHandedWeapon) continue;

                    string prefab = Utils.GetPrefabName(go);
                    if (_exclude.Contains(prefab) || _exclude.Contains(shared.m_name)) continue;

                    bool wanted = shared.m_skillType == Skills.SkillType.Unarmed ||
                                  _extra.Contains(prefab) || _extra.Contains(shared.m_name);
                    if (!wanted) continue;

                    _converted[shared] = shared.m_itemType;
                    shared.m_itemType = ItemDrop.ItemData.ItemType.OneHandedWeapon;
                    _convertedNames.Add(prefab);
                    justDone.Add(prefab);
                }
            }
            catch (Exception e)
            {
                Log.LogError("[Fists] conversion failed (" + why + "): " + e);
                return;
            }

            if (justDone.Count == 0) return;
            justDone.Sort(StringComparer.OrdinalIgnoreCase);
            Log.LogInfo("[Fists] " + justDone.Count + " fist weapons now one-handed: " +
                        string.Join(", ", justDone.ToArray()) +
                        (justDone.Count == _convertedNames.Count
                            ? ""
                            : " (" + _convertedNames.Count + " total)"));
        }

        /// <summary>Put every type back exactly as it was found.</summary>
        private static void Restore(string why)
        {
            if (_converted.Count == 0) return;
            int n = _converted.Count;
            try
            {
                foreach (var kv in _converted) kv.Key.m_itemType = kv.Value;
            }
            catch (Exception e)
            {
                Log.LogWarning("[Fists] restore: " + e.Message);
            }
            _converted.Clear();
            _convertedNames.Clear();
            Log.LogInfo(why + ": " + n + " fist weapons put back to two-handed. Anyone already " +
                        "holding fists AND a shield keeps both until they re-equip.");
        }

        // ---- reporting -----------------------------------------------------------------------

        public override string StatusDetail()
        {
            var sb = new StringBuilder();
            sb.Append(_convertedNames.Count).Append(" one-handed");
            if (_convertedNames.Count > 0)
            {
                var names = new List<string>(_convertedNames);
                names.Sort(StringComparer.OrdinalIgnoreCase);
                sb.Append(" [").Append(string.Join(",", names.ToArray())).Append("]");
            }
            sb.Append(", extra=[").Append(_extraPrefabs.Value)
              .Append("] exclude=[").Append(_excludePrefabs.Value).Append("]");
            return sb.ToString();
        }
    }
}
