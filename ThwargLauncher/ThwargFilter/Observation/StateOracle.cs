using System;
using System.Collections.Generic;

using Decal.Adapter;
using Decal.Adapter.Wrappers;

namespace ThwargFilter
{
    /// <summary>
    /// The state-oracle section of the dumpstate snapshot: the sub-second, deterministic
    /// answer to "what state am I in" for rig validation. These are the questions with no
    /// other fast truth source, since the shard DB lags minutes and chat is silent for them.
    ///
    /// CONTRACT FOR VALIDATORS. Every field is one of:
    ///   a value            the read succeeded, this is the answer
    ///   null               the read succeeded and the answer is genuinely "nothing"
    ///   "unavailable: X"   the read FAILED, the answer is unknown
    /// A validator must be able to tell "empty hand" from "could not read the hand", so a
    /// failed sub-read is never silently omitted and never collapses to an empty list.
    ///
    /// Every section is independently try/caught: one unreadable section cannot cost the
    /// others. MUST be called on the game thread.
    /// </summary>
    class StateOracle
    {
        // Verified LongValueKey members (Decal.Adapter 2.9.7.5):
        //   Wielder       218103818  id of the creature wielding this item
        //   WieldingSlot  218103819  which slot it is wielded in
        //   StackCount    218103814  stack size, present on stackable items such as ammo
        //   Coverage      218103821  body coverage mask, carried by worn armour/clothing
        // Wielder is the discriminator for "equipped": an item merely sitting in a pack
        // carries Container instead, so filtering on Wielder == me yields exactly the
        // worn/wielded set without walking pack contents.
        private const int MAX_EQUIPMENT_ENTRIES = 40;

        public static void AddState(Dictionary<string, object> state, List<string> notes)
        {
            Dictionary<string, object> oracle = new Dictionary<string, object>();
            int playerId = 0;
            bool havePlayer = false;
            try
            {
                playerId = CoreManager.Current.CharacterFilter.Id;
                havePlayer = (playerId != 0);
            }
            catch (Exception exc)
            {
                notes.Add("state: cannot read character id: " + exc.Message);
            }

            AddEquipmentAndAmmo(oracle, notes, playerId, havePlayer);
            AddCombatMode(oracle, notes);
            AddSelection(oracle, notes);

            state["state"] = oracle;
        }

        /// <summary>
        /// Equipment and ammo come from one scan, because ammo is just the wielded item
        /// that carries a stack count.
        ///
        /// COST: scoped to wielded-only by querying WorldFilter.GetByOwner(playerId) rather
        /// than GetInventory(). GetInventory walks everything the character carries,
        /// including pack contents, which is the expensive shape for a 1-2s poll. GetByOwner
        /// is the narrower query; the Wielder filter then discards anything that is merely
        /// carried. If GetByOwner yields nothing at all we retry once through GetInventory,
        /// because an empty result there is ambiguous between "no equipment" and "this
        /// query is not populated", and the Wielder filter makes the wider scan produce the
        /// same answer.
        /// </summary>
        private static void AddEquipmentAndAmmo(
            Dictionary<string, object> oracle,
            List<string> notes,
            int playerId,
            bool havePlayer)
        {
            if (!havePlayer)
            {
                oracle["equipment"] = "unavailable: no character id";
                oracle["equipmentCount"] = 0;
                oracle["ammo"] = "unavailable: no character id";
                return;
            }

            List<object> equipment = new List<object>();
            Dictionary<string, object> ammo = null;
            int ammoCandidates = 0;
            bool truncated = false;
            try
            {
                WorldFilter worldFilter = CoreManager.Current.WorldFilter;
                if (worldFilter == null)
                {
                    oracle["equipment"] = "unavailable: WorldFilter is null";
                    oracle["equipmentCount"] = 0;
                    oracle["ammo"] = "unavailable: WorldFilter is null";
                    return;
                }

                // LOGIN-TIMING RACE. Live runs showed that about 4 seconds after entering
                // the world this scan returns zero wielded items for a character who is
                // demonstrably wearing several, because WorldFilter has not been populated
                // yet. An empty array there would mean "nothing equipped", which is exactly
                // the ambiguity the three-way contract exists to prevent, so the scan is
                // gated on a readiness check first: [] must MEAN empty.
                ScanResult ownerScan = ScanCollection(worldFilter.GetByOwner(playerId), playerId);
                ScanResult inventoryScan = null;
                string source = "GetByOwner";
                if (ownerScan.Wielded.Count == 0)
                {
                    // Only pay for the wider walk when the narrow query answered nothing.
                    inventoryScan = ScanCollection(worldFilter.GetInventory(), playerId);
                    if (inventoryScan.Wielded.Count > 0) { source = "GetInventory"; }
                }

                string notReady = GetNotReadyReason(worldFilter, playerId, ownerScan, inventoryScan);
                if (notReady != null)
                {
                    string reason = "unavailable: worldfilter not yet populated (" + notReady + ")";
                    oracle["equipment"] = reason;
                    oracle["equipmentCount"] = 0;
                    oracle["ammo"] = reason;
                    oracle["equipmentSource"] = source;
                    AddDiagnostics(oracle, ownerScan, inventoryScan);
                    notes.Add("state.equipment: " + notReady);
                    return;
                }

                List<WorldObject> wielded = ownerScan.Wielded;
                if (source == "GetInventory" && inventoryScan != null) { wielded = inventoryScan.Wielded; }
                oracle["equipmentSource"] = source;
                AddDiagnostics(oracle, ownerScan, inventoryScan);

                for (int i = 0; i < wielded.Count; i++)
                {
                    if (equipment.Count >= MAX_EQUIPMENT_ENTRIES) { truncated = true; break; }
                    WorldObject wo = wielded[i];
                    Dictionary<string, object> item = new Dictionary<string, object>();
                    item["id"] = SafeId(wo);
                    item["name"] = SafeName(wo);
                    item["objectClass"] = SafeObjectClass(wo);

                    int slot = 0;
                    if (TryGetLong(wo, LongValueKey.WieldingSlot, out slot)) { item["wieldingSlot"] = slot; }
                    else { item["wieldingSlot"] = null; }

                    int stack = 0;
                    if (TryGetLong(wo, LongValueKey.StackCount, out stack))
                    {
                        item["stackCount"] = stack;
                        // Ammo is the wielded thing that stacks. Weapons and armour do not
                        // carry StackCount, so this separates arrows from the bow without
                        // relying on an object class that Decal does not distinguish.
                        ammoCandidates++;
                        if (ammo == null)
                        {
                            ammo = new Dictionary<string, object>();
                            ammo["id"] = item["id"];
                            ammo["name"] = item["name"];
                            ammo["stackCount"] = stack;
                            ammo["wieldingSlot"] = item["wieldingSlot"];
                        }
                    }
                    equipment.Add(item);
                }

                oracle["equipment"] = equipment;
                oracle["equipmentCount"] = equipment.Count;
                if (truncated) { oracle["equipmentTruncated"] = true; }
                // null means "read succeeded, nothing equipped that stacks", which is the
                // arrows-ran-out signal. It is NOT the same as "unavailable".
                oracle["ammo"] = ammo;
                if (ammoCandidates > 1) { oracle["ammoCandidates"] = ammoCandidates; }
            }
            catch (Exception exc)
            {
                oracle["equipment"] = "unavailable: " + exc.Message;
                oracle["equipmentCount"] = 0;
                oracle["ammo"] = "unavailable: " + exc.Message;
                notes.Add("state.equipment: " + exc.Message);
            }
        }

        /// <summary>
        /// One pass over a collection, gathering both the wielded set and the counts that
        /// let us tell "not populated yet" from "genuinely empty", and that instrument the
        /// open question about worn clothing (see WithCoverage).
        /// </summary>
        private class ScanResult
        {
            public List<WorldObject> Wielded = new List<WorldObject>();
            public int Total;
            public int WithWielder;
            public int WithCoverage;
            public bool Read;
        }

        private static ScanResult ScanCollection(WorldObjectCollection collection, int playerId)
        {
            ScanResult result = new ScanResult();
            if (collection == null) { return result; }
            try
            {
                foreach (WorldObject wo in collection)
                {
                    if (wo == null) { continue; }
                    result.Total++;
                    int coverage = 0;
                    if (TryGetLong(wo, LongValueKey.Coverage, out coverage)) { result.WithCoverage++; }
                    int wielder = 0;
                    if (!TryGetLong(wo, LongValueKey.Wielder, out wielder)) { continue; }
                    result.WithWielder++;
                    if (wielder != playerId) { continue; }
                    result.Wielded.Add(wo);
                }
                result.Read = true;
            }
            catch (Exception)
            {
                // Keep whatever we managed to read; Read stays false so the readiness
                // check treats a partial enumeration as not-ready rather than as empty.
            }
            return result;
        }

        /// <summary>
        /// Returns null when the world data is trustworthy, otherwise a short reason.
        ///
        /// Signals, in order of how defensible they are:
        ///  1. the character's own object is not in WorldFilter yet;
        ///  2. that object has no long properties yet, so its bag is still filling;
        ///  3. neither query enumerated cleanly;
        ///  4. nothing at all is carried. A logged-in character always carries something,
        ///     so zero carried objects means the collections have not populated. This one
        ///     is a heuristic, and it is deliberately biased toward reporting unavailable:
        ///     a false "unavailable" only costs a validator a retry, while a false "[]"
        ///     asserts a wrong fact about the world.
        /// </summary>
        private static string GetNotReadyReason(
            WorldFilter worldFilter,
            int playerId,
            ScanResult ownerScan,
            ScanResult inventoryScan)
        {
            try
            {
                WorldObject player = worldFilter[playerId];
                if (player == null) { return "character object not in WorldFilter"; }
                try
                {
                    List<int> longKeys = player.LongKeys;
                    if (longKeys == null || longKeys.Count == 0)
                    {
                        return "character object has no properties yet";
                    }
                }
                catch (Exception)
                {
                    return "character object property bag not readable";
                }
            }
            catch (Exception exc)
            {
                return "character object lookup failed: " + exc.Message;
            }

            bool ownerRead = (ownerScan != null && ownerScan.Read);
            bool inventoryRead = (inventoryScan != null && inventoryScan.Read);
            if (!ownerRead && !inventoryRead) { return "no world object query could be enumerated"; }

            int carried = 0;
            if (ownerScan != null) { carried += ownerScan.Total; }
            if (inventoryScan != null && inventoryScan.Total > carried) { carried = inventoryScan.Total; }
            if (carried == 0) { return "no carried objects visible yet"; }

            return null;
        }

        /// <summary>
        /// Counts that let a live run settle what the API surface alone cannot: whether
        /// worn clothing is present but simply lacks the client-side Wielder key.
        /// </summary>
        private static void AddDiagnostics(
            Dictionary<string, object> oracle,
            ScanResult ownerScan,
            ScanResult inventoryScan)
        {
            Dictionary<string, object> diag = new Dictionary<string, object>();
            AddScanDiagnostics(diag, "byOwner", ownerScan);
            AddScanDiagnostics(diag, "inventory", inventoryScan);
            oracle["equipmentDiagnostics"] = diag;
        }

        private static void AddScanDiagnostics(Dictionary<string, object> diag, string prefix, ScanResult scan)
        {
            if (scan == null)
            {
                diag[prefix] = null;
                return;
            }
            Dictionary<string, object> entry = new Dictionary<string, object>();
            entry["read"] = scan.Read;
            entry["total"] = scan.Total;
            entry["withWielder"] = scan.WithWielder;
            entry["withCoverage"] = scan.WithCoverage;
            entry["wieldedByMe"] = scan.Wielded.Count;
            diag[prefix] = entry;
        }

        /// <summary>
        /// What the CLIENT believes the combat mode is.
        ///
        /// WHAT THIS IS NOT: it is not the server's motion/stance state. The filter cannot
        /// read the server's CurrentMotionState, so this reflects the client's own combat
        /// mode only, the same value the attack verb sets via Actions.SetCombatMode. If the
        /// server rejected or has not yet applied a mode change, this will disagree with it.
        /// </summary>
        private static void AddCombatMode(Dictionary<string, object> oracle, List<string> notes)
        {
            try
            {
                CombatState mode = CoreManager.Current.Actions.CombatMode;
                Dictionary<string, object> entry = new Dictionary<string, object>();
                entry["value"] = (int)mode;
                entry["name"] = mode.ToString();
                entry["truthSource"] = "client";
                oracle["combatMode"] = entry;
            }
            catch (Exception exc)
            {
                oracle["combatMode"] = "unavailable: " + exc.Message;
                notes.Add("state.combatMode: " + exc.Message);
            }
            // There is no client-side stance signal beyond combat mode; say so explicitly
            // rather than leaving a validator to wonder whether the field was dropped.
            oracle["stance"] = "unavailable: not exposed client-side (server CurrentMotionState is not readable from a filter)";
        }

        /// <summary>
        /// The client's current selection: the "do I have a target" primitive.
        /// id 0 means nothing is selected, which is a successful read, not a failure.
        /// </summary>
        private static void AddSelection(Dictionary<string, object> oracle, List<string> notes)
        {
            try
            {
                int selectedId = CoreManager.Current.Actions.CurrentSelection;
                Dictionary<string, object> entry = new Dictionary<string, object>();
                entry["id"] = selectedId;
                entry["hasSelection"] = (selectedId != 0);
                entry["name"] = null;
                if (selectedId != 0)
                {
                    try
                    {
                        WorldObject wo = CoreManager.Current.WorldFilter[selectedId];
                        if (wo != null) { entry["name"] = SafeName(wo); }
                    }
                    catch (Exception exc)
                    {
                        entry["name"] = "unavailable: " + exc.Message;
                    }
                }
                oracle["selection"] = entry;
            }
            catch (Exception exc)
            {
                oracle["selection"] = "unavailable: " + exc.Message;
                notes.Add("state.selection: " + exc.Message);
            }
        }

        private static bool TryGetLong(WorldObject wo, LongValueKey key, out int value)
        {
            value = 0;
            try { return wo.Exists(key, out value); }
            catch (Exception) { return false; }
        }

        private static int SafeId(WorldObject wo)
        {
            try { return wo.Id; }
            catch (Exception) { return 0; }
        }

        private static string SafeName(WorldObject wo)
        {
            try { return wo.Name; }
            catch (Exception) { return null; }
        }

        private static string SafeObjectClass(WorldObject wo)
        {
            try { return wo.ObjectClass.ToString(); }
            catch (Exception) { return "Unknown"; }
        }
    }
}
