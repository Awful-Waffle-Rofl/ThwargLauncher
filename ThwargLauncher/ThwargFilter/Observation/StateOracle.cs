using System;
using System.Collections.Generic;

using Decal.Adapter;
using Decal.Adapter.Wrappers;

using Filter.Shared;

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
        //   Container     218103810  id of the container this item sits in
        //   Type          218103808  the WEENIE CLASS ID (wcid), see AddInventory
        //   EquippedSlots 10         slot mask, present ONLY while the item is equipped
        //
        // EQUIPPED TEST, settled by an A/B probe on one quarrel stack across a single
        // equip toggle:
        //   unequipped -> Wielder key ABSENT, EquippedSlots key ABSENT
        //   equipped   -> Wielder = 0, EquippedSlots = 0x800000 (MissileAmmo)
        //   equipped weapon -> Wielder = the CHARACTER ID
        // So an item is equipped iff it CARRIES the Wielder key at all; the VALUE is the
        // character id for weapons but ZERO for ammunition. The old test compared the value
        // against the character id, which silently excluded every ammo stack.
        // EquippedSlots corroborates and also names the slot.
        // Wielder is the discriminator for wielded gear. Worn armour and clothing turned
        // out NOT to carry Wielder client-side (live: withCoverage 10 vs withWielder 2),
        // so the equipped set is the union of "wielded by me" and "carries Coverage but
        // sits in no container". The container test matters: a spare shirt in a pack also
        // carries Coverage, and without it packed clothing would be reported as worn.
        private const int MAX_EQUIPMENT_ENTRIES = 40;

        // EQUIPPED TEST: EquippedSlots != 0, and NOTHING ELSE.
        //
        // The Wielder-presence arm was REMOVED after live evidence (ledger L8-5):
        //   equipped weapon -> Wielder = characterId, EquippedSlots non-zero (wand 0x1000000)
        //   equipped ammo   -> EquippedSlots = 0x800000; Wielder is 0 in one session and
        //                      ABSENT ENTIRELY on a fresh login, so it is not even
        //                      consistently present
        //   after unequip   -> the client ZEROES Wielder and EquippedSlots rather than
        //                      REMOVING the keys
        //
        // That last line is what made the OR-form a real bug: Exists(Wielder) stayed TRUE
        // for a just-unequipped item, so unwield verified itself as still-equipped and
        // reported outcome "failed" for a move that had actually SUCCEEDED. A false
        // failure is worse than a false success here, because a rig retries or aborts on
        // it. Wielder presence is neither necessary (weapons carry non-zero EquippedSlots
        // too) nor sufficient (equipped ammo may carry no Wielder key at all).
        // wielderValue is still REPORTED, for information: characterId means weapon,
        // 0-or-null means ammunition.

        /// <summary>
        /// EquipMask.MissileAmmo, verified against ACE EquipMask.cs:35 and confirmed live:
        /// an equipped quarrel stack reported EquippedSlots = 8388608 = 0x800000.
        /// </summary>
        private const int MASK_MissileAmmo = 0x00800000;
        private const int MAX_INVENTORY_ENTRIES = 200;
        private const int MAX_ENCHANTMENT_IDS = 100;

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
            AddInventory(oracle, notes, playerId, havePlayer);
            AddCombatMode(oracle, notes);
            AddSelection(oracle, notes);
            AddAttributes(oracle, notes);
            AddSkills(oracle, notes);
            AddEnchantments(oracle, notes);
            AddSpellBar(oracle, notes);

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
                if (ownerScan.Equipped.Count == 0)
                {
                    // Only pay for the wider walk when the narrow query answered nothing.
                    inventoryScan = ScanCollection(worldFilter.GetInventory(), playerId);
                    if (inventoryScan.Equipped.Count > 0) { source = "GetInventory"; }
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

                List<WorldObject> wielded = ownerScan.Equipped;
                if (source == "GetInventory" && inventoryScan != null) { wielded = inventoryScan.Equipped; }
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

                    int coverage = 0;
                    if (TryGetLong(wo, LongValueKey.Coverage, out coverage)) { item["coverage"] = coverage; }
                    else { item["coverage"] = null; }

                    // How this item qualified, plus the raw values, because the Wielder
                    // VALUE differs by item kind: the character id for a weapon, 0 for
                    // ammunition. wielderValue 0 with equippedSlots 0x800000 IS the ammo
                    // signature.
                    int wielderOf = 0;
                    bool byWielder = TryGetLong(wo, LongValueKey.Wielder, out wielderOf);
                    int slotsOf = 0;
                    bool bySlots = TryGetLong(wo, LongValueKey.EquippedSlots, out slotsOf) && slotsOf != 0;
                    item["wielderValue"] = (byWielder ? (object)wielderOf : null);
                    item["equippedSlots"] = (bySlots ? (object)slotsOf : null);
                    // How the item was ADMITTED, which is now only ever equippedSlots or
                    // coverage. Use wielderValue to tell a weapon (characterId) from
                    // ammunition (0 or null).
                    item["equippedVia"] = (bySlots ? "equippedSlots" : "coverage");

                    item["type"] = TryGetLongOrNull(wo, LongValueKey.Type);

                    int stack = 0;
                    bool hasStack = TryGetLong(wo, LongValueKey.StackCount, out stack);
                    if (hasStack) { item["stackCount"] = stack; }

                    // AMMO IS NOW A LOOKUP, NOT A HEURISTIC. Equipped ammunition is the
                    // equipped item whose EquippedSlots is MissileAmmo (0x800000). The
                    // fallback (a stack whose Wielder value is 0) covers a client that
                    // reports the key without the mask.
                    bool isAmmoBySlot = (bySlots && slotsOf == MASK_MissileAmmo);
                    bool isAmmoByShape = (hasStack && byWielder && wielderOf == 0);
                    if (isAmmoBySlot || isAmmoByShape)
                    {
                        ammoCandidates++;
                        if (ammo == null)
                        {
                            ammo = new Dictionary<string, object>();
                            ammo["id"] = item["id"];
                            ammo["name"] = item["name"];
                            ammo["stackCount"] = (hasStack ? (object)stack : null);
                            ammo["equippedSlots"] = (bySlots ? (object)slotsOf : null);
                            ammo["wielderValue"] = (byWielder ? (object)wielderOf : null);
                            ammo["matchedBy"] = (isAmmoBySlot ? "equippedSlots==MissileAmmo" : "stack with wielder 0");
                        }
                    }
                    equipment.Add(item);
                }

                oracle["equipment"] = equipment;
                oracle["equipmentCount"] = equipment.Count;
                if (truncated) { oracle["equipmentTruncated"] = true; }

                // RETRACTED HEURISTIC. This used to report "the wielded item carrying a
                // StackCount", with null meaning "nothing equipped that stacks". Live
                // evidence proves that heuristic CANNOT work: with 561 Quarrels showing in
                // the client's ammunition indicator, the oracle saw wieldedByMe = 1 (the
                // sword alone) and the quarrel stack among the 26 CONTAINED objects. So
                // equipped ammunition does NOT carry Wielder == me, exactly like worn
                // clothing, and a Wielder-gated scan can never see it.
                //
                // Reporting null here would assert "there is no equipped ammo", which is
                // false whenever ammo IS equipped. Until the discriminator is identified
                // (see the dumpkeys probe), the honest answer is that we do not know.
                // null here now means a genuine "nothing in the ammo slot", because the
                // discriminator is known rather than guessed.
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
            /// <summary>Items whose Wielder is me. Hand/wielded gear.</summary>
            public List<WorldObject> Wielded = new List<WorldObject>();
            /// <summary>Wielded plus worn: the union reported as "equipment".</summary>
            public List<WorldObject> Equipped = new List<WorldObject>();
            /// <summary>Items sitting inside a container. Pack contents.</summary>
            public List<WorldObject> Contained = new List<WorldObject>();
            public int Total;
            public int WithWielder;
            /// <summary>Wielder key PRESENT, whatever its value. The equipped test.</summary>
            public int WithWielderKey;
            /// <summary>Non-zero EquippedSlots: corroborates equipped and names the slot.</summary>
            public int WithEquippedSlots;
            /// <summary>
            /// Admitted by the Coverage arm ALONE, i.e. no non-zero EquippedSlots. Worn
            /// clothing was observed carrying EquippedSlots (196/384/14), which makes the
            /// Coverage arm probably redundant. It is RETAINED as a safety net rather than
            /// removed blind; if this count stays 0 across live runs it can go.
            /// </summary>
            public int WithCoverageOnlyAdmitted;
            public int WithCoverage;
            public int WithCoverageNoContainer;
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

                    int container = 0;
                    bool hasContainer = TryGetLong(wo, LongValueKey.Container, out container) && container != 0;

                    int coverage = 0;
                    bool hasCoverage = TryGetLong(wo, LongValueKey.Coverage, out coverage);
                    if (hasCoverage)
                    {
                        result.WithCoverage++;
                        if (!hasContainer) { result.WithCoverageNoContainer++; }
                    }

                    int wielder = 0;
                    // PRESENCE, not value: ammunition carries Wielder = 0 when equipped.
                    bool hasWielderKey = TryGetLong(wo, LongValueKey.Wielder, out wielder);
                    int equippedSlots = 0;
                    bool hasEquippedSlots = TryGetLong(wo, LongValueKey.EquippedSlots, out equippedSlots)
                        && equippedSlots != 0;
                    // EquippedSlots ONLY. See the equipped-test note on the class.
                    bool wieldedByMe = hasEquippedSlots;

                    if (hasWielderKey) { result.WithWielderKey++; }
                    if (wielder != 0) { result.WithWielder++; }
                    if (hasEquippedSlots) { result.WithEquippedSlots++; }

                    if (wieldedByMe)
                    {
                        result.Wielded.Add(wo);
                        result.Equipped.Add(wo);
                    }
                    else if (hasCoverage && !hasContainer)
                    {
                        // Worn armour or clothing: carries a body coverage mask and is not
                        // inside any container. Probably redundant now that EquippedSlots is
                        // the test (worn items were observed carrying it), but retained as a
                        // safety net and counted so its redundancy can be PROVEN.
                        result.WithCoverageOnlyAdmitted++;
                        result.Equipped.Add(wo);
                    }
                    else if (hasContainer)
                    {
                        result.Contained.Add(wo);
                    }
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
            // withWielderKey is the count that drives the equipped test now. If it ever
            // greatly exceeds the number of things actually equipped, the presence test is
            // over-matching and this is the field that shows it.
            entry["withWielderKey"] = scan.WithWielderKey;
            entry["withEquippedSlots"] = scan.WithEquippedSlots;
            // If this stays 0 across live runs, the Coverage arm is provably redundant.
            entry["withCoverageOnlyAdmitted"] = scan.WithCoverageOnlyAdmitted;
            entry["withCoverage"] = scan.WithCoverage;
            entry["withCoverageNoContainer"] = scan.WithCoverageNoContainer;
            entry["equippedUnion"] = scan.Equipped.Count;
            entry["contained"] = scan.Contained.Count;
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

        /// <summary>
        /// Pack contents: everything carried that sits inside a container, which is the
        /// complement of the equipped set. GetInventory() returns the full carried set
        /// including NESTED packs, so nested contents come for free; each entry reports its
        /// "container" so the nesting is visible.
        ///
        /// COST: this is the expensive query, unlike the equipment scan which is scoped via
        /// GetByOwner. It is capped at 200 entries with inventoryTruncated, same pattern as
        /// nearby. If snapshot cost becomes a problem at a 1-2s poll, this is the section to
        /// drop first.
        /// </summary>
        private static void AddInventory(
            Dictionary<string, object> oracle,
            List<string> notes,
            int playerId,
            bool havePlayer)
        {
            if (!havePlayer)
            {
                oracle["inventory"] = "unavailable: no character id";
                oracle["inventoryCount"] = 0;
                return;
            }
            try
            {
                WorldFilter worldFilter = CoreManager.Current.WorldFilter;
                if (worldFilter == null)
                {
                    oracle["inventory"] = "unavailable: WorldFilter is null";
                    oracle["inventoryCount"] = 0;
                    return;
                }
                ScanResult scan = ScanCollection(worldFilter.GetInventory(), playerId);
                string notReady = GetNotReadyReason(worldFilter, playerId, scan, null);
                if (notReady != null)
                {
                    oracle["inventory"] = "unavailable: worldfilter not yet populated (" + notReady + ")";
                    oracle["inventoryCount"] = 0;
                    notes.Add("state.inventory: " + notReady);
                    return;
                }

                List<object> items = new List<object>();
                bool truncated = false;
                for (int i = 0; i < scan.Contained.Count; i++)
                {
                    if (items.Count >= MAX_INVENTORY_ENTRIES) { truncated = true; break; }
                    WorldObject wo = scan.Contained[i];
                    Dictionary<string, object> item = new Dictionary<string, object>();
                    item["id"] = SafeId(wo);
                    item["name"] = SafeName(wo);
                    item["objectClass"] = SafeObjectClass(wo);
                    item["stackCount"] = TryGetLongOrNull(wo, LongValueKey.StackCount);
                    item["container"] = TryGetLongOrNull(wo, LongValueKey.Container);
                    // LongValueKey.Type is the WEENIE CLASS ID. Verified positionally on
                    // both sides: ACE writes Name, WeenieClassId, IconId, ItemType, flags
                    // (WorldObject_Networking.cs:76-80) and Decal's schema names those
                    // fields name, type, icon, category, behavior (messages.xml GameData).
                    item["type"] = TryGetLongOrNull(wo, LongValueKey.Type);
                    items.Add(item);
                }
                oracle["inventory"] = items;
                oracle["inventoryCount"] = items.Count;
                oracle["inventoryTotal"] = scan.Contained.Count;
                if (truncated) { oracle["inventoryTruncated"] = true; }
            }
            catch (Exception exc)
            {
                oracle["inventory"] = "unavailable: " + exc.Message;
                oracle["inventoryCount"] = 0;
                notes.Add("state.inventory: " + exc.Message);
            }
        }

        /// <summary>
        /// The six attributes, from CharacterFilter. CLIENT-CACHED, like vitals: this is
        /// what the server last told the client. /showstats remains server truth.
        /// AttributeInfoWrapper exposes Base, Buffed, Creation, Exp and Name.
        /// </summary>
        private static void AddAttributes(Dictionary<string, object> oracle, List<string> notes)
        {
            try
            {
                CharacterFilter filter = CoreManager.Current.CharacterFilter;
                if (filter == null)
                {
                    oracle["attributes"] = "unavailable: CharacterFilter is null";
                    return;
                }
                Dictionary<string, object> attributes = new Dictionary<string, object>();
                AddAttribute(attributes, filter, "strength", CharFilterAttributeType.Strength);
                AddAttribute(attributes, filter, "endurance", CharFilterAttributeType.Endurance);
                AddAttribute(attributes, filter, "quickness", CharFilterAttributeType.Quickness);
                AddAttribute(attributes, filter, "coordination", CharFilterAttributeType.Coordination);
                AddAttribute(attributes, filter, "focus", CharFilterAttributeType.Focus);
                AddAttribute(attributes, filter, "self", CharFilterAttributeType.Self);
                attributes["truthSource"] = "client-cached";
                oracle["attributes"] = attributes;
            }
            catch (Exception exc)
            {
                oracle["attributes"] = "unavailable: " + exc.Message;
                notes.Add("state.attributes: " + exc.Message);
            }
        }

        private static void AddAttribute(
            Dictionary<string, object> attributes,
            CharacterFilter filter,
            string name,
            CharFilterAttributeType type)
        {
            try
            {
                AttributeInfoWrapper info = filter.Attributes[type];
                if (info == null)
                {
                    attributes[name] = "unavailable: no attribute info";
                    return;
                }
                Dictionary<string, object> entry = new Dictionary<string, object>();
                entry["base"] = info.Base;
                entry["buffed"] = info.Buffed;
                entry["creation"] = info.Creation;
                entry["exp"] = info.Exp;
                attributes[name] = entry;
            }
            catch (Exception exc)
            {
                attributes[name] = "unavailable: " + exc.Message;
            }
        }

        /// <summary>
        /// Skills, from CharacterFilter. CLIENT-CACHED.
        ///
        /// Only skills whose training state is not Unusable are emitted: there are 48
        /// CharFilterSkillType values and dumping all of them every snapshot would bloat a
        /// file meant for 1-2 second polling. skillsProbed and skillsOmitted make the
        /// filtering visible so a validator is never guessing why a skill is absent.
        /// SkillInfoWrapper exposes Current, Base, Buffed, Training and Known.
        /// </summary>
        private static void AddSkills(Dictionary<string, object> oracle, List<string> notes)
        {
            try
            {
                CharacterFilter filter = CoreManager.Current.CharacterFilter;
                if (filter == null)
                {
                    oracle["skills"] = "unavailable: CharacterFilter is null";
                    return;
                }
                Dictionary<string, object> skills = new Dictionary<string, object>();
                Array values = Enum.GetValues(typeof(CharFilterSkillType));
                int probed = 0;
                int omitted = 0;
                for (int i = 0; i < values.Length; i++)
                {
                    CharFilterSkillType type = (CharFilterSkillType)values.GetValue(i);
                    probed++;
                    try
                    {
                        SkillInfoWrapper info = filter.Skills[type];
                        if (info == null) { omitted++; continue; }
                        if (info.Training == TrainingType.Unusable) { omitted++; continue; }
                        Dictionary<string, object> entry = new Dictionary<string, object>();
                        entry["current"] = info.Current;
                        entry["base"] = info.Base;
                        entry["buffed"] = info.Buffed;
                        entry["training"] = info.Training.ToString();
                        entry["known"] = info.Known;
                        skills[type.ToString()] = entry;
                    }
                    catch (Exception)
                    {
                        omitted++;
                    }
                }
                oracle["skills"] = skills;
                oracle["skillsProbed"] = probed;
                oracle["skillsOmitted"] = omitted;
                oracle["skillsTruthSource"] = "client-cached";
            }
            catch (Exception exc)
            {
                oracle["skills"] = "unavailable: " + exc.Message;
                notes.Add("state.skills: " + exc.Message);
            }
        }

        /// <summary>
        /// Active enchantments. The goal is visibility: auto-buffing plugins silently move
        /// a rig's baseline, so a validator needs to see that buffs are present at all.
        /// Count is the primary signal; spell ids are included because they are cheap
        /// (one int per enchantment) and let a rig assert on a specific buff.
        /// EnchantmentWrapper exposes SpellId, Family, Layer, Duration and TimeRemaining.
        /// </summary>
        private static void AddEnchantments(Dictionary<string, object> oracle, List<string> notes)
        {
            try
            {
                CharacterFilter filter = CoreManager.Current.CharacterFilter;
                if (filter == null)
                {
                    oracle["enchantments"] = "unavailable: CharacterFilter is null";
                    return;
                }
                int count = filter.Enchantments.Count;
                Dictionary<string, object> entry = new Dictionary<string, object>();
                entry["count"] = count;
                List<object> spellIds = new List<object>();
                bool truncated = false;
                for (int i = 0; i < count; i++)
                {
                    if (spellIds.Count >= MAX_ENCHANTMENT_IDS) { truncated = true; break; }
                    try
                    {
                        EnchantmentWrapper ench = filter.Enchantments[i];
                        if (ench == null) { continue; }
                        spellIds.Add(ench.SpellId);
                    }
                    catch (Exception)
                    {
                        // One unreadable enchantment must not cost the count.
                    }
                }
                entry["spellIds"] = spellIds;
                if (truncated) { entry["spellIdsTruncated"] = true; }
                entry["truthSource"] = "client-cached";
                oracle["enchantments"] = entry;
            }
            catch (Exception exc)
            {
                oracle["enchantments"] = "unavailable: " + exc.Message;
                notes.Add("state.enchantments: " + exc.Message);
            }
        }

        /// <summary>
        /// The client's spell bar, which is how CLIENT-INITIATED casting is aimed.
        /// CLIENT-INSTANT: CharacterFilter.SpellBar(tab) is a local read.
        ///
        /// Slots are reported 1-based to match the hotkeys that fire them, while the
        /// underlying collection is 0-based. An empty slot reports spellId null, which is a
        /// successful read of "nothing here"; a failed read reports "unavailable: ..." as
        /// everywhere else.
        /// </summary>
        private static void AddSpellBar(Dictionary<string, object> oracle, List<string> notes)
        {
            try
            {
                CharacterFilter filter = CoreManager.Current.CharacterFilter;
                if (filter == null)
                {
                    oracle["spellbar"] = "unavailable: CharacterFilter is null";
                    return;
                }
                System.Collections.ObjectModel.ReadOnlyCollection<int> bar = SpellBar.ReadBar(SpellBar.DEFAULT_TAB);
                if (bar == null)
                {
                    oracle["spellbar"] = "unavailable: SpellBar(" + SpellBar.DEFAULT_TAB + ") not readable";
                    notes.Add("state.spellbar: SpellBar not readable");
                    return;
                }
                List<object> slots = new List<object>();
                int occupied = 0;
                for (int i = 0; i < bar.Count; i++)
                {
                    Dictionary<string, object> slot = new Dictionary<string, object>();
                    slot["slot"] = i + 1;
                    int spellId = bar[i];
                    if (spellId != 0)
                    {
                        occupied++;
                        slot["spellId"] = spellId;
                        slot["known"] = SpellBar.IsSpellKnown(spellId);
                    }
                    else
                    {
                        slot["spellId"] = null;
                        slot["known"] = null;
                    }
                    // The hotkey that would fire this slot, so a rig never has to derive it.
                    NamedKey key = NamedKeys.FindSlotKey(i + 1);
                    slot["hotkey"] = (key == null ? null : key.Name);
                    slots.Add(slot);
                }
                oracle["spellbar"] = slots;
                oracle["spellbarTab"] = SpellBar.DEFAULT_TAB;
                oracle["spellbarSlots"] = bar.Count;
                oracle["spellbarOccupied"] = occupied;
                oracle["spellbarTruthSource"] = "client-instant";
            }
            catch (Exception exc)
            {
                oracle["spellbar"] = "unavailable: " + exc.Message;
                notes.Add("state.spellbar: " + exc.Message);
            }
        }

        /// <summary>Boxed int when the key exists, null when it does not.</summary>
        private static object TryGetLongOrNull(WorldObject wo, LongValueKey key)
        {
            int value = 0;
            if (TryGetLong(wo, key, out value)) { return value; }
            return null;
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
