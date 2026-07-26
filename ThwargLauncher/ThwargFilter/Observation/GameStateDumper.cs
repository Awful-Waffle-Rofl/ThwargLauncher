using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using Decal.Adapter;
using Decal.Adapter.Wrappers;

using Newtonsoft.Json;

namespace ThwargFilter
{
    /// <summary>
    /// Implements the "dumpstate" verb: snapshot the live game state to
    /// gamestate_[pid].txt (JSON, full overwrite) in the launcher Running folder.
    ///
    /// THREADING: RequestDump may be called from any thread. Launcher channel commands
    /// arrive on the heartbeat timer thread or on a FileSystemWatcher thread, and
    /// CoreManager.Current.WorldFilter / CharacterFilter must only be touched on the game
    /// thread. So RequestDump only raises a flag and subscribes to RenderFrame; the actual
    /// snapshot happens inside the RenderFrame handler, which unsubscribes itself after one
    /// frame (same pattern as AfterLoginCompleteMessageQueueManager.Current_RenderFrame).
    /// </summary>
    class GameStateDumper
    {
        private const int MAX_NEARBY_OBJECTS = 50;

        private object _locker = new object();
        private bool _dumpPending;
        private bool _subscribed;
        private int _sequence;

        private delegate object ValueGetter();

        /// <summary>
        /// Thread safe. Asks for a snapshot on the next rendered frame.
        /// </summary>
        public void RequestDump()
        {
            try
            {
                lock (_locker)
                {
                    _dumpPending = true;
                    if (!_subscribed)
                    {
                        _subscribed = true;
                        CoreManager.Current.RenderFrame += new EventHandler<EventArgs>(Current_RenderFrame);
                    }
                }
                log.WriteInfo("GameStateDumper: dump requested");
            }
            catch (Exception exc)
            {
                log.WriteError("GameStateDumper.RequestDump exception: {0}", exc);
            }
        }

        void Current_RenderFrame(object sender, EventArgs e)
        {
            try
            {
                bool shouldDump = false;
                lock (_locker)
                {
                    if (_subscribed)
                    {
                        CoreManager.Current.RenderFrame -= new EventHandler<EventArgs>(Current_RenderFrame);
                        _subscribed = false;
                    }
                    shouldDump = _dumpPending;
                    _dumpPending = false;
                }
                if (shouldDump)
                {
                    WriteSnapshot();
                }
            }
            catch (Exception exc)
            {
                log.WriteError("GameStateDumper.Current_RenderFrame exception: {0}", exc);
            }
        }

        private void WriteSnapshot()
        {
            Dictionary<string, object> state = new Dictionary<string, object>();
            List<string> notes = new List<string>();

            _sequence++;
            state["utc"] = DateTime.UtcNow.ToString("o");
            state["seq"] = _sequence;
            state["pid"] = System.Diagnostics.Process.GetCurrentProcess().Id;

            AddCharacter(state, notes);
            AddVitals(state, notes);
            AddPosition(state, notes);
            AddNearby(state, notes);
            // State-oracle section. Additive: every field above keeps its existing shape.
            StateOracle.AddState(state, notes);
            // Outstanding server confirmation dialog, if any. Also additive.
            Confirmer.AddState(state, notes);

            state["notes"] = notes;

            string json = JsonConvert.SerializeObject(state, Formatting.Indented);
            string filepath = FileLocations.GetGameStateFilepath();
            WriteTextToFile(json, filepath);
            log.WriteInfo("GameStateDumper wrote snapshot seq {0} to '{1}'", _sequence, filepath);
        }

        private static void AddCharacter(Dictionary<string, object> state, List<string> notes)
        {
            Dictionary<string, object> character = new Dictionary<string, object>();
            try
            {
                CharacterFilter filter = CoreManager.Current.CharacterFilter;
                if (filter == null)
                {
                    notes.Add("character: CharacterFilter is null");
                    state["loggedIn"] = false;
                    state["character"] = character;
                    return;
                }
                SetSafe(character, "name", delegate { return filter.Name; }, notes);
                SetSafe(character, "id", delegate { return filter.Id; }, notes);
                SetSafe(character, "level", delegate { return filter.Level; }, notes);
                SetSafe(character, "loginStatus", delegate { return filter.LoginStatus; }, notes);
                SetSafe(character, "accountName", delegate { return filter.AccountName; }, notes);
                SetSafe(character, "server", delegate { return filter.Server; }, notes);
                SetSafe(character, "race", delegate { return filter.Race; }, notes);
                SetSafe(character, "gender", delegate { return filter.Gender; }, notes);
                SetSafe(character, "classTemplate", delegate { return filter.ClassTemplate; }, notes);
                SetSafe(character, "totalXp", delegate { return filter.TotalXP; }, notes);
                SetSafe(character, "unassignedXp", delegate { return filter.UnassignedXP; }, notes);
                SetSafe(character, "burden", delegate { return filter.Burden; }, notes);
                SetSafe(character, "vitae", delegate { return filter.Vitae; }, notes);
                state["loggedIn"] = IsLoggedIn(filter);
            }
            catch (Exception exc)
            {
                notes.Add("character: " + exc.Message);
            }
            state["character"] = character;
        }

        private static bool IsLoggedIn(CharacterFilter filter)
        {
            try
            {
                return filter.Id != 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// "points" is the live pool value that CharacterFilter exposes directly;
        /// "max", "base" and "buffed" come from the vital's SkillInfoWrapper, where
        /// Current is Decal's maximum for that vital.
        /// </summary>
        private static void AddVitals(Dictionary<string, object> state, List<string> notes)
        {
            Dictionary<string, object> vitals = new Dictionary<string, object>();
            try
            {
                CharacterFilter filter = CoreManager.Current.CharacterFilter;
                if (filter == null)
                {
                    notes.Add("vitals: CharacterFilter is null");
                    state["vitals"] = vitals;
                    return;
                }
                AddVital(vitals, "health", CharFilterVitalType.Health, filter.Health, notes);
                AddVital(vitals, "stamina", CharFilterVitalType.Stamina, filter.Stamina, notes);
                AddVital(vitals, "mana", CharFilterVitalType.Mana, filter.Mana, notes);
            }
            catch (Exception exc)
            {
                notes.Add("vitals: " + exc.Message);
            }
            state["vitals"] = vitals;
        }

        private static void AddVital(
            Dictionary<string, object> vitals,
            string vitalName,
            CharFilterVitalType vitalType,
            int points,
            List<string> notes)
        {
            Dictionary<string, object> vital = new Dictionary<string, object>();
            vital["points"] = points;
            try
            {
                SkillInfoWrapper info = CoreManager.Current.CharacterFilter.Vitals[vitalType];
                if (info == null)
                {
                    notes.Add("vital " + vitalName + ": no vital info");
                }
                else
                {
                    vital["max"] = info.Current;
                    vital["base"] = info.Base;
                    vital["buffed"] = info.Buffed;
                    vital["bonus"] = info.Bonus;
                }
            }
            catch (Exception exc)
            {
                notes.Add("vital " + vitalName + ": " + exc.Message);
            }
            vitals[vitalName] = vital;
        }

        private static void AddPosition(Dictionary<string, object> state, List<string> notes)
        {
            Dictionary<string, object> position = new Dictionary<string, object>();
            try
            {
                int playerId = CoreManager.Current.CharacterFilter.Id;
                WorldObject player = CoreManager.Current.WorldFilter[playerId];
                if (player == null)
                {
                    notes.Add(string.Format("position: no world object for player id {0}", playerId));
                    state["position"] = position;
                    return;
                }
                int landcell = 0;
                if (player.Exists(LongValueKey.Landblock, out landcell))
                {
                    position["landcell"] = landcell;
                    position["landcellHex"] = string.Format("0x{0:X8}", landcell);
                }
                else
                {
                    notes.Add("position: LongValueKey.Landblock not present on player object");
                }
                CoordsObject coords = player.Coordinates();
                if (coords != null)
                {
                    position["ew"] = coords.EastWest;
                    position["ns"] = coords.NorthSouth;
                }
                Vector3Object raw = player.RawCoordinates();
                if (raw != null)
                {
                    position["x"] = raw.X;
                    position["y"] = raw.Y;
                    position["z"] = raw.Z;
                }
            }
            catch (Exception exc)
            {
                notes.Add("position: " + exc.Message);
            }
            state["position"] = position;
        }

        private static void AddNearby(Dictionary<string, object> state, List<string> notes)
        {
            List<object> nearby = new List<object>();
            state["nearbyTruncated"] = false;
            try
            {
                int playerId = CoreManager.Current.CharacterFilter.Id;
                WorldFilter worldFilter = CoreManager.Current.WorldFilter;
                WorldObjectCollection landscape = worldFilter.GetLandscape();
                if (landscape == null)
                {
                    notes.Add("nearby: GetLandscape returned null");
                    state["nearby"] = nearby;
                    return;
                }
                List<NearbyObject> found = new List<NearbyObject>();
                foreach (WorldObject worldObject in landscape)
                {
                    if (worldObject == null) { continue; }
                    if (worldObject.Id == playerId) { continue; }
                    found.Add(MakeNearbyObject(worldFilter, playerId, worldObject));
                }
                state["nearbyTotal"] = found.Count;
                found.Sort(new Comparison<NearbyObject>(CompareByDistance));
                for (int i = 0; i < found.Count; ++i)
                {
                    if (nearby.Count >= MAX_NEARBY_OBJECTS)
                    {
                        state["nearbyTruncated"] = true;
                        break;
                    }
                    nearby.Add(found[i].ToDictionary());
                }
            }
            catch (Exception exc)
            {
                notes.Add("nearby: " + exc.Message);
            }
            state["nearby"] = nearby;
        }

        private static NearbyObject MakeNearbyObject(WorldFilter worldFilter, int playerId, WorldObject worldObject)
        {
            NearbyObject item = new NearbyObject();
            try { item.Id = worldObject.Id; }
            catch (Exception) { }
            try { item.Name = worldObject.Name; }
            catch (Exception) { }
            try { item.ObjectClass = worldObject.ObjectClass.ToString(); }
            catch (Exception) { }
            try { item.Distance = worldFilter.Distance(playerId, item.Id); }
            catch (Exception) { item.Distance = double.MaxValue; }
            return item;
        }

        private static int CompareByDistance(NearbyObject left, NearbyObject right)
        {
            return left.Distance.CompareTo(right.Distance);
        }

        private class NearbyObject
        {
            public int Id;
            public string Name;
            public string ObjectClass;
            public double Distance = double.MaxValue;

            public Dictionary<string, object> ToDictionary()
            {
                Dictionary<string, object> item = new Dictionary<string, object>();
                item["id"] = Id;
                item["name"] = Name;
                item["objectClass"] = ObjectClass;
                if (Distance != double.MaxValue)
                {
                    item["distance"] = Distance;
                }
                return item;
            }
        }

        private static void SetSafe(Dictionary<string, object> target, string key, ValueGetter getter, List<string> notes)
        {
            try
            {
                target[key] = getter();
            }
            catch (Exception exc)
            {
                notes.Add(key + ": " + exc.Message);
            }
        }

        /// <summary>
        /// Full overwrite with FileShare.None, matching Channels.CommandWriter. A poller
        /// that catches us mid-write gets an IOException rather than a truncated file,
        /// and should simply retry.
        /// </summary>
        private static void WriteTextToFile(string contents, string filepath)
        {
            using (FileStream file = File.Open(filepath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using (StreamWriter outstr = new StreamWriter(file, new UTF8Encoding(false)))
                {
                    outstr.Write(contents);
                }
            }
        }
    }
}
