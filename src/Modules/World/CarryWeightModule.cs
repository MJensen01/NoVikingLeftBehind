using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// Raises the base carry limit, and makes Megingjord worth its belt slot.
    ///
    /// Vanilla, verified in the 1.0.12 decompile:
    ///   Player.cs:179   public float m_maxCarryWeight = 300f;
    ///   Player.cs:5042  GetMaxCarryWeight() { float limit = m_maxCarryWeight;
    ///                                         m_seman.ModifyMaxCarryWeight(limit, ref limit);
    ///                                         return limit * Game.m_carryWeightRate; }
    ///   SEMan.cs:399    ModifyMaxCarryWeight() loops m_statusEffects and calls each one's override.
    ///   StatusEffect.cs:302  the virtual is empty; SE_Stats.cs:350 overrides it with
    ///                        `limit += m_addMaxCarryWeight; if (limit &lt; 0f) limit = 0f;`
    ///   SE_Stats.cs:538  the tooltip line: "$se_max_carryweight: &lt;color=orange&gt;{0}&lt;/color&gt;\n"
    ///                    formatted with m_addMaxCarryWeight.ToString("+0;-0").
    /// Megingjord is the item prefab "BeltStrength"; its bonus lives on the status effect hanging
    /// off ItemDrop.m_itemData.m_shared.m_equipStatusEffect (ItemDrop.cs:148), an SE_Stats with
    /// m_addMaxCarryWeight = 150.
    ///
    /// SIDE = Client, the same choice PortalTrail and LongFires make and for the same reason: the
    /// numbers are only ever computed on a player's own machine (SEMan.ModifyMaxCarryWeight has
    /// exactly ONE caller in the whole assembly - Player.GetMaxCarryWeight - and every caller of
    /// THAT is client-side UI or client-side movement: InventoryGui.cs:669, Player.cs:1789
    /// auto-pickup, Player.cs:5039 IsEncumbered, TombStone.cs:141). A dedicated server never runs
    /// any of them, so it correctly reports disabled(side) and patches nothing. The settings are
    /// still BindSynced, so the server owns the values and pushes them to every client - one server
    /// rule, computed locally, exactly like PortalTrail's tier check.
    ///
    /// NOTHING IS MUTATED. Both numbers are applied as Harmony POSTFIXES that adjust the value
    /// being returned:
    ///
    ///   Player.GetMaxCarryWeight postfix - adds (BaseCarryWeight - the instance's own
    ///   m_maxCarryWeight) * Game.m_carryWeightRate to __result. The vanilla base is read back off
    ///   the instance rather than assumed to be 300, so this composes with any other mod that
    ///   raised the field, and multiplying by Game.m_carryWeightRate (Game.cs:216, driven by the
    ///   world modifier GlobalKeys.CarryWeightRate at Game.cs:1357) means our extra weight obeys
    ///   the same world modifier vanilla's does. m_maxCarryWeight itself is never written: it is a
    ///   field other code and other mods read directly, and a value we stored there would outlive
    ///   a config change, a module toggle and an UnpatchSelf.
    ///
    ///   SE_Stats.ModifyMaxCarryWeight postfix - for a belt effect only, adds
    ///   (BeltBonus - __instance.m_addMaxCarryWeight) to limit. The template's
    ///   m_addMaxCarryWeight is deliberately NOT written, for the reason PortalTrail's class doc
    ///   gives about m_teleportable: SEMan.AddStatusEffect stores StatusEffect.Clone()
    ///   (StatusEffect.cs:77, a MemberwiseClone), so the template is shared by every clone and by
    ///   every other reader of that asset - the item tooltip, any other mod, a re-read after
    ///   ObjectDB is pushed from the server. Recomputing the difference at the moment the game asks
    ///   is pure and needs no teardown.
    ///
    /// Both patches only fire while the module is Active and only for the LOCAL player: a prefix on
    /// Player.GetMaxCarryWeight records whether this call is the local player's, the postfix clears
    /// it again, and the SE_Stats postfix (which gets no Character of its own) gates on that flag.
    /// That keeps the belt adjustment and the base adjustment exactly in step - there is no state
    /// in which one applies and the other does not.
    ///
    /// Module off, or [Carry] Enabled=false: every patch body returns immediately and the numbers
    /// are vanilla to the float.
    /// </summary>
    internal sealed class CarryWeightModule : FeatureModule
    {
        public override string Name => "CarryWeight";
        public override ModuleSide Side => ModuleSide.Client;
        public override string Section => "Carry";

        public override string Theme => "World";

        public override string Hint => "More carry weight, and a stronger Megingjord";

        /// <summary>Vanilla Player.m_maxCarryWeight (Player.cs:179), used only as a fallback for
        /// reporting when no Player instance exists (headless self-test).</summary>
        private const float VanillaBase = 300f;

        private ConfigEntry<float> _baseWeight;
        private ConfigEntry<float> _beltBonus;
        private ConfigEntry<string> _beltItems;

        private static CarryWeightModule _self;

        /// <summary>Set by the GetMaxCarryWeight prefix while the LOCAL player's limit is being
        /// computed; the only window in which either postfix may touch a number.</summary>
        private static bool _localCalc;

        // ---- resolved belts (lazy, invalidated when ObjectDB changes or BeltItems is edited) ----

        private static HashSet<string> _beltNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<int> _effectHashes = new HashSet<int>();
        private static readonly HashSet<string> _effectNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<string> _resolved = new List<string>();
        private static ObjectDB _resolvedAgainst;
        private static bool _beltsDirty = true;

        private static bool Live() { return _self != null && _self.Active && ClientActive(); }

        protected override void Bind()
        {
            _self = this;

            _baseWeight = BindSynced("BaseCarryWeight", 450f,
                "How much a Viking can carry before being encumbered, before any belt. Vanilla is " +
                "300 (Player.m_maxCarryWeight). Applied as a difference on top of whatever the " +
                "field actually holds, and scaled by the world's carry-weight modifier exactly " +
                "like vanilla's own number, so a world set to 'lighter'/'heavier' still works.",
                Opt.N("Base carry weight (vanilla 300)", 300, 1000, 10));

            _beltBonus = BindSynced("BeltBonus", 300f,
                "What Megingjord adds on top of BaseCarryWeight while it is equipped. Vanilla is " +
                "150 (the m_addMaxCarryWeight on the belt's equip status effect). The shared " +
                "status-effect asset is never written - the difference is applied as the game asks " +
                "for the number - so turning this module off restores vanilla exactly.",
                Opt.N("Megingjord carry bonus (vanilla 150)", 0, 1000, 10));

            _beltItems = BindSynced("BeltItems", "BeltStrength",
                "Comma-separated item prefab names whose equip status effect counts as 'the belt' " +
                "and therefore gets BeltBonus instead of its own m_addMaxCarryWeight. Only vanilla " +
                "Megingjord (BeltStrength) by default; add another mod's belt here to have it " +
                "follow the same server rule. An item with no equip status effect is ignored.",
                Opt.T("Which items count as a carry-weight belt")
                    .Pick(new PickerSpec(PickerSource.Items)));

            ParseBeltItems();
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            if (entry == _beltItems) ParseBeltItems();
            if (Active) Log.LogInfo("[" + Name + "] " + Numbers());
        }

        private void ParseBeltItems()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var raw = _beltItems != null ? _beltItems.Value : "BeltStrength";
            foreach (var chunk in (raw ?? "").Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var name = chunk.Trim();
                if (name.Length > 0) set.Add(name);
            }
            _beltNames = set;
            _beltsDirty = true;
        }

        protected override void ApplyPatches()
        {
            var get = AccessTools.Method(typeof(Player), "GetMaxCarryWeight", Type.EmptyTypes);
            if (get == null) throw new Exception("Player.GetMaxCarryWeight() not found");
            Harmony.Patch(get,
                prefix: new HarmonyMethod(typeof(CarryWeightModule), nameof(GetMaxCarryWeightPre)),
                postfix: new HarmonyMethod(typeof(CarryWeightModule), nameof(GetMaxCarryWeightPost)));

            var modify = AccessTools.Method(typeof(SE_Stats), "ModifyMaxCarryWeight",
                                            new[] { typeof(float), typeof(float).MakeByRefType() });
            if (modify == null) throw new Exception("SE_Stats.ModifyMaxCarryWeight(float, ref float) not found");
            Harmony.Patch(modify, postfix: new HarmonyMethod(typeof(CarryWeightModule), nameof(ModifyMaxCarryWeightPost)));

            var tooltip = AccessTools.Method(typeof(SE_Stats), "GetTooltipString", Type.EmptyTypes);
            if (tooltip == null) throw new Exception("SE_Stats.GetTooltipString() not found");
            Harmony.Patch(tooltip, postfix: new HarmonyMethod(typeof(CarryWeightModule), nameof(GetTooltipStringPost)));

            Log.LogInfo("[" + Name + "] " + Numbers());
        }

        // ---- base carry weight ------------------------------------------------------------

        /// <summary>Opens the window in which the two carry-weight postfixes may act. Never skips
        /// the original.</summary>
        private static void GetMaxCarryWeightPre(Player __instance)
        {
            _localCalc = Live() && __instance != null && ReferenceEquals(Player.m_localPlayer, __instance);
        }

        private static void GetMaxCarryWeightPost(Player __instance, ref float __result)
        {
            if (!_localCalc) return;
            _localCalc = false;

            // Read the vanilla base off the instance, never assume 300 - see the class doc.
            float delta = _self._baseWeight.Value - __instance.m_maxCarryWeight;
            if (delta == 0f) return;

            __result += delta * Game.m_carryWeightRate;
            if (__result < 0f) __result = 0f;
        }

        // ---- the belt ------------------------------------------------------------------------

        private static void ModifyMaxCarryWeightPost(SE_Stats __instance, ref float limit)
        {
            if (!_localCalc || __instance == null) return;
            if (!IsBeltEffect(__instance)) return;

            limit += _self._beltBonus.Value - __instance.m_addMaxCarryWeight;
            if (limit < 0f) limit = 0f;      // same clamp vanilla applies (SE_Stats.cs:353)
        }

        /// <summary>
        /// Show the configured bonus on the belt's own tooltip instead of vanilla's +150. Rebuilds
        /// the exact fragment SE_Stats.GetTooltipString wrote (SE_Stats.cs:540) from the instance's
        /// own m_addMaxCarryWeight and the same "+0;-0" format, so the swap either matches exactly
        /// or does nothing at all.
        /// </summary>
        private static void GetTooltipStringPost(SE_Stats __instance, ref string __result)
        {
            if (!Live() || __instance == null || string.IsNullOrEmpty(__result)) return;
            if (!IsBeltEffect(__instance)) return;

            float vanilla = __instance.m_addMaxCarryWeight;
            float configured = _self._beltBonus.Value;
            if (vanilla == configured) return;

            string was = "$se_max_carryweight: <color=orange>" + vanilla.ToString("+0;-0") + "</color>";
            string now = "$se_max_carryweight: <color=orange>" + configured.ToString("+0;-0") + "</color>";
            if (__result.IndexOf(was, StringComparison.Ordinal) >= 0) __result = __result.Replace(was, now);
        }

        // ---- resolving which status effect is "the belt" --------------------------------------

        /// <summary>
        /// Resolve [Carry] BeltItems -> equip status effects, once per ObjectDB (the server pushes
        /// its own ObjectDB to a joining client, which replaces the instance) and once per edit of
        /// BeltItems. Match by NameHash first: SEMan stores StatusEffect.Clone(), a MemberwiseClone
        /// (StatusEffect.cs:77) that copies the already-computed m_nameHash, so the clone's hash is
        /// the template's and no native name lookup is needed. The cleaned name is kept as a
        /// second key in case some other path hands us a genuinely renamed instance.
        /// </summary>
        private static void ResolveBelts()
        {
            var odb = ObjectDB.instance;
            if (odb == null || odb.m_items == null) return;
            if (!_beltsDirty && ReferenceEquals(odb, _resolvedAgainst)) return;

            _beltsDirty = false;
            _resolvedAgainst = odb;
            _effectHashes.Clear();
            _effectNames.Clear();
            _resolved.Clear();

            foreach (var itemName in _beltNames)
            {
                var se = EquipEffect(odb, itemName);
                if (se == null) continue;

                _effectHashes.Add(se.NameHash());
                _effectNames.Add(Tiers.CleanName(se.name));

                var stats = se as SE_Stats;
                _resolved.Add(itemName + "->" + Tiers.CleanName(se.name) +
                              (stats != null ? "(+" + stats.m_addMaxCarryWeight + ")" : "(not SE_Stats)"));
            }
        }

        private static StatusEffect EquipEffect(ObjectDB odb, string itemName)
        {
            var go = odb.GetItemPrefab(itemName);
            var drop = go != null ? go.GetComponent<ItemDrop>() : null;
            var shared = drop != null && drop.m_itemData != null ? drop.m_itemData.m_shared : null;
            return shared != null ? shared.m_equipStatusEffect : null;
        }

        private static bool IsBeltEffect(SE_Stats se)
        {
            ResolveBelts();
            if (_effectHashes.Count == 0) return false;
            if (_effectHashes.Contains(se.NameHash())) return true;
            return _effectNames.Contains(Tiers.CleanName(se.name));
        }

        // ---- reporting ------------------------------------------------------------------------

        private string Numbers()
        {
            var p = Player.m_localPlayer;
            float vanillaBase = p != null ? p.m_maxCarryWeight : VanillaBase;
            ResolveBelts();

            return "BaseCarryWeight=" + _baseWeight.Value + " (vanilla " + vanillaBase + ")" +
                   " BeltBonus=" + _beltBonus.Value + " (vanilla 150)" +
                   " effective with belt=" + (_baseWeight.Value + _beltBonus.Value) +
                   " carryWeightRate=x" + Game.m_carryWeightRate +
                   " BeltItems=[" + string.Join(",", new List<string>(_beltNames).ToArray()) + "]" +
                   " belt effect=" + (_resolved.Count > 0
                                          ? string.Join(",", _resolved.ToArray())
                                          : "unresolved");
        }

        public override string StatusDetail() { return Numbers(); }

        /// <summary>
        /// Headless proof: resolves the belt effect straight out of ObjectDB and prints the vanilla
        /// numbers next to the configured ones. Needs no Player instance, so a dedicated server -
        /// where this module is correctly disabled(side) - can still prove the resolution and the
        /// arithmetic. Same pattern as PortalTrail.SelfTest.
        /// </summary>
        internal static string SelfTest()
        {
            var sb = new StringBuilder();
            float baseW = _self != null ? _self._baseWeight.Value : VanillaBase;
            float belt = _self != null ? _self._beltBonus.Value : 150f;

            sb.Append("[SelfTest][CarryWeight] BaseCarryWeight=").Append(baseW)
              .Append(" (vanilla ").Append(VanillaBase).Append(")")
              .Append(" BeltBonus=").Append(belt)
              .Append(" carryWeightRate=x").Append(Game.m_carryWeightRate)
              .Append(" BeltItems=[").Append(string.Join(",", new List<string>(_beltNames).ToArray())).Append("]");

            var odb = ObjectDB.instance;
            if (odb == null || odb.m_items == null)
            {
                sb.Append("\n  ObjectDB not loaded - cannot resolve any belt effect");
                return sb.ToString();
            }

            ResolveBelts();
            int found = 0;
            foreach (var itemName in _beltNames)
            {
                var se = EquipEffect(odb, itemName);
                if (se == null)
                {
                    sb.Append("\n  ").Append(itemName).Append(": no equip status effect -> unresolved");
                    continue;
                }
                found++;
                var stats = se as SE_Stats;
                sb.Append("\n  ").Append(itemName)
                  .Append(": effect=").Append(Tiers.CleanName(se.name))
                  .Append(" nameHash=").Append(se.NameHash())
                  .Append(" token=").Append(se.m_name)
                  .Append(" vanilla m_addMaxCarryWeight=")
                  .Append(stats != null ? stats.m_addMaxCarryWeight.ToString() : "n/a (not SE_Stats)")
                  .Append(" -> configured ").Append(belt);
            }

            sb.Append("\n  belts resolved: ").Append(found).Append("/").Append(_beltNames.Count)
              .Append("  effective carry with belt = ").Append(baseW).Append(" + ").Append(belt)
              .Append(" = ").Append(baseW + belt)
              .Append(" (x").Append(Game.m_carryWeightRate).Append(" world rate = ")
              .Append((baseW + belt) * Game.m_carryWeightRate).Append(")");
            return sb.ToString();
        }
    }
}
