using System;
using UnityEngine;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// The server-side heartbeat for OreRegrowth. A FeatureModule is a plain object with no
    /// Update(), so the module parks one hidden DontDestroyOnLoad GameObject carrying this
    /// component and lets Unity call it. Everything it does is gated by the module itself
    /// (Active + ServerActive), so it is inert on a client and while the feature is off.
    /// </summary>
    internal sealed class RegrowthTicker : MonoBehaviour
    {
        private float _next;
        private bool _announced;

        private void Update()
        {
            var m = OreRegrowthModule.Instance;
            if (m == null || !m.Active) return;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (ZNetScene.instance == null || ZDOMan.instance == null) return;

            if (Time.time < _next) return;
            _next = Time.time + m.CheckIntervalSec;

            try
            {
                m.EnsureAllowlist();

                if (!_announced)
                {
                    _announced = true;
                    NoVikingLeftBehindPlugin.Log.LogInfo("[OreRegrowth] ticker running: every " +
                        m.CheckIntervalSec.ToString("0.#") + "s, day=" + OreRegrowthModule.CurrentDay() +
                        " (" + OreRegrowthModule.DaySource() + ")");
                }

                if (m.SelfTestWanted) { m.RunSelfTest(); return; }

                m.RunSweep();
            }
            catch (Exception e)
            {
                NoVikingLeftBehindPlugin.Log.LogError("[OreRegrowth] ticker: " + e);
            }
        }
    }
}
