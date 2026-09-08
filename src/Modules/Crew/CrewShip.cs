using UnityEngine;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// The two things the crew-sailing modules (Lookout, SeaLegs) both need, in one place.
    ///
    /// Not a FeatureModule - plain static helpers, so DiscoverModules() never picks it up.
    ///
    /// The crew COUNT deliberately does not come from <c>Ship.m_players</c> on a reader.
    /// <c>m_players</c> is filled by <c>Ship.OnTriggerEnter</c>/<c>OnTriggerExit</c>, i.e. by the
    /// local physics scene, so at a zone edge (or before a peer's Player object has been
    /// instantiated on your machine) two clients looking at the same longship legitimately hold
    /// different lists. Anything derived from it per client would desync. The ship OWNER - the one
    /// machine that also runs <c>Ship.CustomFixedUpdate</c>, which early-returns on everyone else -
    /// writes the number into the ship's ZDO, and every machine (owner included) reads it back
    /// from there, so the sail force the owner computes and the sail the passenger watches agree.
    /// </summary>
    internal static class CrewShip
    {
        /// <summary>ZDO int key holding the ship's crew count (helmsman included).</summary>
        internal const string CrewKey = "nvlb_crew";

        /// <summary>Hard clamp on the stored/read crew count.</summary>
        internal const int MaxCrew = 8;

        /// <summary>
        /// The ship the given (local) player is aboard, or null. Standing on the deck is the
        /// normal case; the attached case (sitting on a bench / at a rudder) has no direct
        /// accessor, so it falls back to Ship's own static "ships whose volume the local player
        /// is inside" list - and ONLY when that list holds exactly one ship, because with two
        /// hulls overlapping (Ship.GetLocalShip() just takes the last one) the answer would be a
        /// coin flip. One ambiguous frame is worth nothing; being wrong about which boat is.
        /// </summary>
        internal static Ship Aboard(Player player)
        {
            if (player == null) return null;

            var ship = player.GetStandingOnShip();
            if (ship != null) return ship;

            if (player.IsAttachedToShip() &&
                Ship.s_currentShips != null && Ship.s_currentShips.Count == 1)
            {
                return Ship.s_currentShips[0];
            }
            return null;
        }

        /// <summary>
        /// The ship's crew count as agreed by everyone: the owner-written ZDO int, never the
        /// local m_players list. 0 when the ship has no valid ZNetView yet (= vanilla behaviour
        /// for every caller).
        /// </summary>
        internal static int CrewOf(Ship ship)
        {
            if (ship == null) return 0;
            var nview = ship.m_nview;
            if (nview == null || !nview.IsValid()) return 0;
            var zdo = nview.GetZDO();
            if (zdo == null) return 0;
            return Mathf.Clamp(zdo.GetInt(CrewKey, 0), 0, MaxCrew);
        }
    }
}
