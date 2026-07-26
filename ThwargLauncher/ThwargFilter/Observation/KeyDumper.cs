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
    /// The "dumpkeys" probe: dump the COMPLETE property bag of matching objects, so a
    /// discriminator can be found by DIFFING two live states rather than guessed at.
    ///
    /// WHY: equipped ammunition provably does not carry Wielder. A live client showed
    /// "561 Quarrels" in the ammunition indicator while the oracle reported ammo: null,
    /// wieldedByMe: 1 (the sword alone) and contained: 26. So the equipped quarrel stack
    /// sits among the "contained" objects, exactly the shape of the earlier worn-clothing
    /// discovery: present in the client's collection, invisible to a Wielder-only filter.
    ///
    /// Which key DOES mark it is not knowable from the assembly. LongValueKey has 103
    /// members and several plausible candidates (EquippedSlots, EquipableSlots, EquipType,
    /// MissileType, UsageMask, SlotLegacy, ItemSlots, WieldingSlot), and CharacterFilter
    /// has no ammunition member at all (checked: no Ammo/Ammun/Quiver/Missile/Arrow/Bolt
    /// member exists). So this verb dumps EVERYTHING and lets a diff answer it.
    ///
    /// USE: with an ammo stack equipped, run
    ///     dumpkeys quarrel
    /// then unequip it and run the same command again. Diff the two objectkeys files. The
    /// keys that changed are the discriminator. /ub useip is a TOGGLE (equip if unequipped,
    /// unequip if equipped, swap if the slot is occupied), which is both how to produce the
    /// two states cheaply and why that command reads as unreliable when driven blind.
    ///
    /// Output: objectkeys_[pid].json, full overwrite, with a seq so a poller can tell runs
    /// apart. MUST be called on the game thread.
    /// </summary>
    class KeyDumper
    {
        private const int MAX_OBJECTS = 20;

        private object _locker = new object();
        private Queue<string> _pending = new Queue<string>();
        private bool _subscribed;
        private int _sequence;

        /// <summary>Thread safe. Queue a key dump for the next rendered frame.</summary>
        public void RequestDump(string target)
        {
            try
            {
                if (target == null) { target = ""; }
                log.WriteInfo("dumpkeys: requested for '{0}'", target);
                lock (_locker)
                {
                    _pending.Enqueue(target);
                    if (!_subscribed)
                    {
                        _subscribed = true;
                        CoreManager.Current.RenderFrame += new EventHandler<EventArgs>(Current_RenderFrame);
                    }
                }
            }
            catch (Exception exc)
            {
                log.WriteError("KeyDumper.RequestDump exception: {0}", exc);
            }
        }

        void Current_RenderFrame(object sender, EventArgs e)
        {
            try
            {
                string target = null;
                bool have = false;
                lock (_locker)
                {
                    if (_pending.Count > 0) { target = _pending.Dequeue(); have = true; }
                    if (_pending.Count == 0 && _subscribed)
                    {
                        CoreManager.Current.RenderFrame -= new EventHandler<EventArgs>(Current_RenderFrame);
                        _subscribed = false;
                    }
                }
                if (have) { DoDump(target); }
            }
            catch (Exception exc)
            {
                log.WriteError("KeyDumper.Current_RenderFrame exception: {0}", exc);
            }
        }

        private void DoDump(string target)
        {
            Dictionary<string, object> doc = new Dictionary<string, object>();
            List<string> notes = new List<string>();
            _sequence++;
            doc["utc"] = DateTime.UtcNow.ToString("o");
            doc["seq"] = _sequence;
            doc["target"] = target;

            try
            {
                string trimmed = (target == null ? "" : target.Trim());
                if (trimmed.Length == 0)
                {
                    notes.Add("no target given; use 'dumpkeys <name-substring|wcid>'");
                    doc["objects"] = "unavailable: no target given";
                    Write(doc, notes);
                    return;
                }

                int playerId = 0;
                try { playerId = CoreManager.Current.CharacterFilter.Id; }
                catch (Exception) { playerId = 0; }
                doc["playerId"] = playerId;
                if (playerId == 0)
                {
                    doc["objects"] = "unavailable: no character id";
                    notes.Add("not logged in");
                    Write(doc, notes);
                    return;
                }

                WorldFilter worldFilter = CoreManager.Current.WorldFilter;
                if (worldFilter == null)
                {
                    doc["objects"] = "unavailable: WorldFilter is null";
                    Write(doc, notes);
                    return;
                }

                // Union of both queries, deduped: the object we are hunting may be in
                // either, and which one is part of what we are trying to learn.
                Dictionary<int, WorldObject> seen = new Dictionary<int, WorldObject>();
                AddFrom(seen, worldFilter.GetInventory(), "GetInventory");
                AddFrom(seen, worldFilter.GetByOwner(playerId), "GetByOwner");
                doc["scannedTotal"] = seen.Count;

                List<object> dumped = new List<object>();
                int matched = 0;
                foreach (KeyValuePair<int, WorldObject> pair in seen)
                {
                    WorldObject wo = pair.Value;
                    if (!Matches(wo, trimmed)) { continue; }
                    matched++;
                    if (dumped.Count >= MAX_OBJECTS) { continue; }
                    Dictionary<string, object> one = DumpObject(wo, playerId);
                    dumped.Add(one);
                    LogObject(one);
                }
                doc["matched"] = matched;
                doc["objects"] = dumped;
                if (matched > dumped.Count) { doc["objectsTruncated"] = true; }
                log.WriteInfo("dumpkeys '{0}': {1} of {2} scanned objects matched", trimmed, matched, seen.Count);
            }
            catch (Exception exc)
            {
                doc["objects"] = "unavailable: " + exc.Message;
                notes.Add(exc.Message);
                log.WriteError("dumpkeys exception: {0}", exc);
            }
            Write(doc, notes);
        }

        /// <summary>Human-readable dump to the filter log, for eyeball diffing.</summary>
        private static void LogObject(Dictionary<string, object> entry)
        {
            try
            {
                log.WriteInfo("itemkeys: ===== {0} (id {1}) =====", entry["name"], entry["id"]);
                LogMap("named", entry["named"] as Dictionary<string, object>);
                LogMap("long", entry["longKeys"] as Dictionary<string, object>);
                LogMap("bool", entry["boolKeys"] as Dictionary<string, object>);
                LogMap("double", entry["doubleKeys"] as Dictionary<string, object>);
                LogMap("string", entry["stringKeys"] as Dictionary<string, object>);
            }
            catch (Exception exc)
            {
                log.WriteError("KeyDumper.LogObject exception: {0}", exc);
            }
        }

        private static void LogMap(string label, Dictionary<string, object> map)
        {
            if (map == null) { return; }
            foreach (KeyValuePair<string, object> pair in map)
            {
                log.WriteInfo("itemkeys   {0}.{1} = {2}", label, pair.Key,
                    (pair.Value == null ? "null" : pair.Value.ToString()));
            }
        }

        private static void AddFrom(Dictionary<int, WorldObject> seen, WorldObjectCollection collection, string source)
        {
            if (collection == null) { return; }
            try
            {
                foreach (WorldObject wo in collection)
                {
                    if (wo == null) { continue; }
                    int id = 0;
                    try { id = wo.Id; } catch (Exception) { continue; }
                    if (id == 0) { continue; }
                    if (!seen.ContainsKey(id)) { seen[id] = wo; }
                }
            }
            catch (Exception exc)
            {
                log.WriteError("dumpkeys: error scanning {0}: {1}", source, exc);
            }
        }

        /// <summary>
        /// Resolution, in order: "id:N" is an exact object id, a bare integer is a wcid
        /// (consistent with the wield verb), anything else is a name substring.
        /// </summary>
        private static bool Matches(WorldObject wo, string target)
        {
            try
            {
                if (target.Length > 3
                    && string.Compare(target.Substring(0, 3), "id:", StringComparison.OrdinalIgnoreCase) == 0)
                {
                    int wantedId = 0;
                    if (!int.TryParse(target.Substring(3).Trim(), out wantedId)) { return false; }
                    return wo.Id == wantedId;
                }
                int wcid = 0;
                if (int.TryParse(target, out wcid))
                {
                    int type = 0;
                    if (wo.Exists(LongValueKey.Type, out type)) { return type == wcid; }
                    return false;
                }
                string name = wo.Name;
                if (name == null) { return false; }
                return name.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Every key in every space, with the enum name where one exists. Unnamed keys are
        /// reported by raw number, because the discriminator may well be a key Decal's enum
        /// does not name.
        /// </summary>
        private static Dictionary<string, object> DumpObject(WorldObject wo, int playerId)
        {
            Dictionary<string, object> entry = new Dictionary<string, object>();
            SafeSet(entry, "id", wo, "id");
            SafeSet(entry, "name", wo, "name");
            SafeSet(entry, "objectClass", wo, "objectClass");
            SafeSet(entry, "hasIdData", wo, "hasIdData");
            entry["playerId"] = playerId;

            // Convenience block: the specific keys this investigation cares about, named
            // and pulled out of the bag so a diff does not have to hunt for them. The full
            // key maps below are still the authority, and the UNMAPPED keys there are the
            // most interesting: equipped and packed ammo are known to be identical on every
            // field the oracle currently emits, so the discriminator is very likely a key
            // Decal's enum does not name.
            Dictionary<string, object> wrapper = new Dictionary<string, object>();
            wrapper["container"] = LongOrNull(wo, LongValueKey.Container);
            wrapper["wielder"] = LongOrNull(wo, LongValueKey.Wielder);
            wrapper["wieldingSlot"] = LongOrNull(wo, LongValueKey.WieldingSlot);
            wrapper["equippedSlots"] = LongOrNull(wo, LongValueKey.EquippedSlots);
            wrapper["equipableSlots"] = LongOrNull(wo, LongValueKey.EquipableSlots);
            wrapper["equipType"] = LongOrNull(wo, LongValueKey.EquipType);
            wrapper["coverage"] = LongOrNull(wo, LongValueKey.Coverage);
            wrapper["stackCount"] = LongOrNull(wo, LongValueKey.StackCount);
            wrapper["missileType"] = LongOrNull(wo, LongValueKey.MissileType);
            wrapper["usageMask"] = LongOrNull(wo, LongValueKey.UsageMask);
            wrapper["slotLegacy"] = LongOrNull(wo, LongValueKey.SlotLegacy);
            wrapper["itemSlots"] = LongOrNull(wo, LongValueKey.ItemSlots);
            wrapper["type"] = LongOrNull(wo, LongValueKey.Type);
            entry["named"] = wrapper;

            entry["longKeys"] = DumpLongKeys(wo);
            entry["boolKeys"] = DumpBoolKeys(wo);
            entry["doubleKeys"] = DumpDoubleKeys(wo);
            entry["stringKeys"] = DumpStringKeys(wo);

            // Also emit as a chatlog record so two dumps can be diffed programmatically
            // without reading the side file.
            WriteChatlogRecord(entry);
            return entry;
        }

        private static object LongOrNull(WorldObject wo, LongValueKey key)
        {
            try
            {
                int value = 0;
                if (wo.Exists(key, out value)) { return value; }
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>
        /// One ItemKeys line per dumped object, carrying the same content as the file so a
        /// harness can diff two states straight out of chatlog_[pid].jsonl.
        /// </summary>
        private static void WriteChatlogRecord(Dictionary<string, object> objectEntry)
        {
            try
            {
                Dictionary<string, object> entry = new Dictionary<string, object>();
                entry["utc"] = DateTime.UtcNow.ToString("o");
                entry["source"] = "filter";
                entry["type"] = "ItemKeys";
                foreach (KeyValuePair<string, object> pair in objectEntry)
                {
                    entry[pair.Key] = pair.Value;
                }
                ChatLogWriter.WriteEntry(entry);
            }
            catch (Exception exc)
            {
                log.WriteError("KeyDumper.WriteChatlogRecord exception: {0}", exc);
            }
        }

        private static Dictionary<string, object> DumpLongKeys(WorldObject wo)
        {
            Dictionary<string, object> map = new Dictionary<string, object>();
            try
            {
                List<int> keys = wo.LongKeys;
                if (keys == null) { return map; }
                for (int i = 0; i < keys.Count; i++)
                {
                    int key = keys[i];
                    string name = KeyName(typeof(LongValueKey), key);
                    try { map[name] = wo.Values((LongValueKey)key); }
                    catch (Exception) { map[name] = null; }
                }
            }
            catch (Exception exc)
            {
                map["error"] = exc.Message;
            }
            return map;
        }

        private static Dictionary<string, object> DumpBoolKeys(WorldObject wo)
        {
            Dictionary<string, object> map = new Dictionary<string, object>();
            try
            {
                List<int> keys = wo.BoolKeys;
                if (keys == null) { return map; }
                for (int i = 0; i < keys.Count; i++)
                {
                    int key = keys[i];
                    string name = KeyName(typeof(BoolValueKey), key);
                    try { map[name] = wo.Values((BoolValueKey)key); }
                    catch (Exception) { map[name] = null; }
                }
            }
            catch (Exception exc)
            {
                map["error"] = exc.Message;
            }
            return map;
        }

        private static Dictionary<string, object> DumpDoubleKeys(WorldObject wo)
        {
            Dictionary<string, object> map = new Dictionary<string, object>();
            try
            {
                List<int> keys = wo.DoubleKeys;
                if (keys == null) { return map; }
                for (int i = 0; i < keys.Count; i++)
                {
                    int key = keys[i];
                    string name = KeyName(typeof(DoubleValueKey), key);
                    try { map[name] = wo.Values((DoubleValueKey)key); }
                    catch (Exception) { map[name] = null; }
                }
            }
            catch (Exception exc)
            {
                map["error"] = exc.Message;
            }
            return map;
        }

        private static Dictionary<string, object> DumpStringKeys(WorldObject wo)
        {
            Dictionary<string, object> map = new Dictionary<string, object>();
            try
            {
                List<int> keys = wo.StringKeys;
                if (keys == null) { return map; }
                for (int i = 0; i < keys.Count; i++)
                {
                    int key = keys[i];
                    string name = KeyName(typeof(StringValueKey), key);
                    try { map[name] = wo.Values((StringValueKey)key); }
                    catch (Exception) { map[name] = null; }
                }
            }
            catch (Exception exc)
            {
                map["error"] = exc.Message;
            }
            return map;
        }

        /// <summary>
        /// "Name(number)" when the enum names the key, otherwise "unnamed(number)". The
        /// number is always present so a diff never loses a key the enum does not cover.
        /// </summary>
        public static string KeyName(Type enumType, int key)
        {
            try
            {
                string name = Enum.GetName(enumType, key);
                if (name != null) { return name + "(" + key + ")"; }
            }
            catch (Exception) { }
            return "unnamed(" + key + ")";
        }

        private static void SafeSet(Dictionary<string, object> entry, string field, WorldObject wo, string which)
        {
            try
            {
                if (which == "id") { entry[field] = wo.Id; }
                else if (which == "name") { entry[field] = wo.Name; }
                else if (which == "objectClass") { entry[field] = wo.ObjectClass.ToString(); }
                else if (which == "hasIdData") { entry[field] = wo.HasIdData; }
            }
            catch (Exception exc)
            {
                entry[field] = "unavailable: " + exc.Message;
            }
        }

        private void Write(Dictionary<string, object> doc, List<string> notes)
        {
            try
            {
                doc["notes"] = notes;
                string json = JsonConvert.SerializeObject(doc, Formatting.Indented);
                string filepath = FileLocations.GetObjectKeysFilepath();
                using (FileStream file = File.Open(filepath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    using (StreamWriter outstr = new StreamWriter(file, new UTF8Encoding(false)))
                    {
                        outstr.Write(json);
                    }
                }
                log.WriteInfo("dumpkeys wrote seq {0} to '{1}'", _sequence, filepath);
            }
            catch (Exception exc)
            {
                log.WriteError("KeyDumper.Write exception: {0}", exc);
            }
        }
    }
}
