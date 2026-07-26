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

                List<WorldObject> wielded = CollectWielded(worldFilter, playerId, worldFilter.GetByOwner(playerId));
                string source = "GetByOwner";
                if (wielded.Count == 0)
                {
                    List<WorldObject> wider = CollectWielded(worldFilter, playerId, worldFilter.GetInventory());
                    if (wider.Count > 0)
                    {
                        wielded = wider;
                        source = "GetInventory";
                    }
                }
                oracle["equipmentSource"] = source;

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

        private static List<WorldObject> CollectWielded(WorldFilter worldFilter, int playerId, WorldObjectCollection collection)
        {
            List<WorldObject> found = new List<WorldObject>();
            if (collection == null) { return found; }
            try
            {
                foreach (WorldObject wo in collection)
                {
                    if (wo == null) { continue; }
                    int wielder = 0;
                    if (!TryGetLong(wo, LongValueKey.Wielder, out wielder)) { continue; }
                    if (wielder != playerId) { continue; }
                    found.Add(wo);
                }
            }
            catch (Exception)
            {
                // Return what we managed to read; the caller still reports a count.
            }
            return found;
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
