using System;
using System.Collections.Generic;

using Decal.Adapter;
using Decal.Adapter.Wrappers;

namespace ThwargFilter
{
    /// <summary>
    /// One object in the world that matched a name substring.
    /// </summary>
    class TargetCandidate
    {
        public int Id;
        public string Name;
        public string ObjectClass;
        public double Distance = double.MaxValue;

        public string Describe()
        {
            string distanceText = (Distance == double.MaxValue
                ? "?"
                : Distance.ToString("F3"));
            return string.Format(
                "'{0}' id={1} class={2} distance={3}",
                Name,
                Id,
                ObjectClass,
                distanceText);
        }
    }

    /// <summary>
    /// Shared name-substring target resolution for the verbs that need to aim at something
    /// in the world (appraise, attack). Extracted so both use identical matching rules:
    /// a target that "appraise" resolves must be the same target that "attack" resolves,
    /// otherwise a rig that appraises then attacks can silently act on two different
    /// objects.
    ///
    /// MUST be called on the game thread: it touches WorldFilter and CharacterFilter.
    /// </summary>
    class TargetResolver
    {
        /// <summary>
        /// Case insensitive substring match over landscape objects and players, sorted
        /// nearest first. Returns null only if the world object list cannot be read at all,
        /// which is different from an empty list (a readable world with no matches).
        /// </summary>
        public static List<TargetCandidate> Collect(string target)
        {
            List<TargetCandidate> found = new List<TargetCandidate>();
            Dictionary<int, bool> seen = new Dictionary<int, bool>();
            int playerId = 0;
            WorldFilter worldFilter = null;
            try
            {
                worldFilter = CoreManager.Current.WorldFilter;
                if (worldFilter == null) { return null; }
                playerId = CoreManager.Current.CharacterFilter.Id;
            }
            catch (Exception exc)
            {
                log.WriteError("target resolve: cannot reach the world filter: {0}", exc);
                return null;
            }

            // Landscape covers creatures, NPCs and most world objects. Players are queried
            // separately as well so a player standing in an already-crowded cell cannot be
            // missed if the landscape view omits them.
            bool anyRead = false;
            if (AddMatches(found, seen, worldFilter.GetLandscape(), target, worldFilter, playerId)) { anyRead = true; }
            if (AddMatches(found, seen, worldFilter.GetByObjectClass(ObjectClass.Player), target, worldFilter, playerId)) { anyRead = true; }
            if (!anyRead) { return null; }

            found.Sort(new Comparison<TargetCandidate>(CompareByDistance));
            return found;
        }

        private static bool AddMatches(
            List<TargetCandidate> found,
            Dictionary<int, bool> seen,
            WorldObjectCollection collection,
            string target,
            WorldFilter worldFilter,
            int playerId)
        {
            if (collection == null) { return false; }
            try
            {
                foreach (WorldObject worldObject in collection)
                {
                    if (worldObject == null) { continue; }
                    TargetCandidate candidate = MakeCandidate(worldObject, worldFilter, playerId);
                    if (candidate == null) { continue; }
                    if (candidate.Name == null) { continue; }
                    if (candidate.Name.IndexOf(target, StringComparison.OrdinalIgnoreCase) < 0) { continue; }
                    if (seen.ContainsKey(candidate.Id)) { continue; }
                    seen[candidate.Id] = true;
                    found.Add(candidate);
                }
                return true;
            }
            catch (Exception exc)
            {
                log.WriteError("target resolve: error scanning world objects: {0}", exc);
                return false;
            }
        }

        private static TargetCandidate MakeCandidate(WorldObject worldObject, WorldFilter worldFilter, int playerId)
        {
            TargetCandidate candidate = new TargetCandidate();
            try { candidate.Id = worldObject.Id; }
            catch (Exception) { return null; }
            try { candidate.Name = worldObject.Name; }
            catch (Exception) { candidate.Name = null; }
            try { candidate.ObjectClass = worldObject.ObjectClass.ToString(); }
            catch (Exception) { candidate.ObjectClass = "Unknown"; }
            try { candidate.Distance = worldFilter.Distance(playerId, candidate.Id); }
            catch (Exception) { candidate.Distance = double.MaxValue; }
            return candidate;
        }

        private static int CompareByDistance(TargetCandidate left, TargetCandidate right)
        {
            return left.Distance.CompareTo(right.Distance);
        }

        /// <summary>
        /// Log candidates, bounded so an ambiguous match in a crowded cell cannot flood.
        /// </summary>
        public static void LogCandidates(string verb, List<TargetCandidate> candidates, int maxLogged)
        {
            if (candidates == null) { return; }
            int shown = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (shown >= maxLogged)
                {
                    log.WriteInfo("{0}: ...and {1} more", verb, candidates.Count - shown);
                    break;
                }
                log.WriteInfo("{0} candidate: {1}", verb, candidates[i].Describe());
                shown++;
            }
        }
    }
}
