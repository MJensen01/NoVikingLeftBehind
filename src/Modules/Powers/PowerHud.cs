using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// The second guardian-power HUD widget.
    ///
    /// The mod ships no UI assets. Vanilla's widget is Hud.m_gpRoot (a RectTransform holding
    /// Hud.m_gpIcon, Hud.m_gpName and Hud.m_gpCooldown - Hud.decompiled.cs:131-137, driven by the
    /// private Hud.UpdateGuardianPower(Player) at :1470). We Instantiate that whole subtree under
    /// the same parent, nudge it by an offset, and then find *our* copies of the three leaf
    /// components by their INDEX in the original's GetComponentsInChildren list - Instantiate
    /// preserves child order, so index i in the clone is the same widget as index i in the
    /// original. No name matching, no hard-coded paths.
    ///
    /// Every step is guarded: if any vanilla field is missing, or the clone comes back without the
    /// leaves we need, the HUD is logged once and permanently skipped - the feature itself keeps
    /// working, you just do not get a second icon.
    /// </summary>
    internal static class PowerHud
    {
        public static Vector2 Offset = new Vector2(0f, -56f);

        private static Hud _hud;
        private static GameObject _clone;
        private static RectTransform _rt;
        private static TMP_Text _name;
        private static TMP_Text _cooldown;
        private static Image _icon;
        private static bool _failed;
        private static bool _built;

        /// <summary>Postfix of Hud.UpdateGuardianPower: refresh (and, first time, build) the clone.</summary>
        public static void Refresh(Hud hud, Player player, int slot)
        {
            if (_failed || hud == null || player == null) return;
            try
            {
                if (!Ensure(hud)) return;

                var se = PowerSlots.GetSe(player, slot);
                if (se == null)
                {
                    if (_clone.activeSelf) _clone.SetActive(false);
                    return;
                }

                float cd = PowerSlots.GetCooldown(player, slot);
                if (!_clone.activeSelf) _clone.SetActive(true);

                if (_icon != null)
                {
                    _icon.sprite = se.m_icon;
                    _icon.color = (cd <= 0f) ? Color.white : Hud.s_colorRedBlueZeroAlpha;
                }
                if (_name != null)
                    _name.text = Localization.instance.Localize(se.m_name);
                if (_cooldown != null)
                    _cooldown.text = (cd > 0f)
                        ? StatusEffect.GetTimeString(cd)
                        : Localization.instance.Localize("$hud_ready");
            }
            catch (Exception e)
            {
                _failed = true;
                NoVikingLeftBehindPlugin.Log.LogWarning("[DualPowers] second HUD element disabled after an error: " + e.Message);
                Destroy();
            }
        }

        private static bool Ensure(Hud hud)
        {
            if (_built && _clone != null && ReferenceEquals(_hud, hud)) return true;

            // The Hud was rebuilt (scene change) - start over.
            if (!ReferenceEquals(_hud, hud)) Destroy();

            if (hud.m_gpRoot == null || hud.m_gpIcon == null || hud.m_gpName == null || hud.m_gpCooldown == null)
            {
                _failed = true;
                NoVikingLeftBehindPlugin.Log.LogWarning("[DualPowers] Hud.m_gpRoot/m_gpIcon/m_gpName/m_gpCooldown missing - " +
                                              "no second power icon (the feature still works, use the hotkey).");
                return false;
            }

            var root = hud.m_gpRoot;
            var origTexts = root.GetComponentsInChildren<TMP_Text>(true);
            var origImages = root.GetComponentsInChildren<Image>(true);
            int nameIdx = IndexOf(origTexts, hud.m_gpName);
            int cdIdx = IndexOf(origTexts, hud.m_gpCooldown);
            int iconIdx = IndexOf(origImages, hud.m_gpIcon);

            _clone = UnityEngine.Object.Instantiate(root.gameObject, root.parent);
            _clone.name = "NVLB_GP2";
            _rt = _clone.GetComponent<RectTransform>();
            if (_rt == null)
            {
                _failed = true;
                NoVikingLeftBehindPlugin.Log.LogWarning("[DualPowers] cloned HUD root has no RectTransform - no second power icon.");
                Destroy();
                return false;
            }
            _rt.anchoredPosition = root.anchoredPosition + Offset;
            _rt.localScale = root.localScale;

            var cloneTexts = _rt.GetComponentsInChildren<TMP_Text>(true);
            var cloneImages = _rt.GetComponentsInChildren<Image>(true);
            _name = At(cloneTexts, nameIdx);
            _cooldown = At(cloneTexts, cdIdx);
            _icon = At(cloneImages, iconIdx);

            if (_icon == null)
            {
                _failed = true;
                NoVikingLeftBehindPlugin.Log.LogWarning("[DualPowers] could not map the cloned power icon (" +
                                              cloneImages.Length + " images, wanted index " + iconIdx +
                                              ") - no second power icon.");
                Destroy();
                return false;
            }
            if (_name == null || _cooldown == null)
                NoVikingLeftBehindPlugin.Log.LogWarning("[DualPowers] second HUD element has an icon but no " +
                                              (_name == null ? "name" : "cooldown") + " label.");

            _clone.SetActive(false);
            _hud = hud;
            _built = true;
            NoVikingLeftBehindPlugin.Log.LogInfo("[DualPowers] second power HUD element created under '" +
                                       (root.parent != null ? root.parent.name : "?") + "' at offset " + Offset +
                                       " (icon=" + iconIdx + " name=" + nameIdx + " cooldown=" + cdIdx + ")");
            return true;
        }

        private static int IndexOf<T>(T[] arr, T item) where T : class
        {
            if (arr == null) return -1;
            for (int i = 0; i < arr.Length; i++) if (ReferenceEquals(arr[i], item)) return i;
            return -1;
        }

        private static T At<T>(T[] arr, int i) where T : class
        {
            return (arr != null && i >= 0 && i < arr.Length) ? arr[i] : null;
        }

        public static void SetOffset(Vector2 offset)
        {
            Offset = offset;
            if (_rt != null && _hud != null && _hud.m_gpRoot != null)
                _rt.anchoredPosition = _hud.m_gpRoot.anchoredPosition + Offset;
        }

        public static void Destroy()
        {
            try { if (_clone != null) UnityEngine.Object.Destroy(_clone); }
            catch (Exception e) { NoVikingLeftBehindPlugin.Log.LogWarning("[DualPowers] HUD teardown: " + e.Message); }
            _clone = null; _rt = null; _name = null; _cooldown = null; _icon = null;
            _hud = null; _built = false;
        }

        /// <summary>Allow a retry after a transient failure (config toggle).</summary>
        public static void Reset()
        {
            Destroy();
            _failed = false;
        }
    }
}
