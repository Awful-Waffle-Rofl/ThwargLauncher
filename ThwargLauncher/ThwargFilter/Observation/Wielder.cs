using System;
using System.Collections.Generic;

using Decal.Adapter;
using Decal.Adapter.Wrappers;

namespace ThwargFilter
{
    /// <summary>
    /// The "wield" verb: equip an item out of the pack. Counterpart to "unwield".
    ///
    /// WHY THIS EXISTS: two lanes converged on the same wall.
    ///  1. NO available server command can put AMMUNITION into the ammo slot on this build.
    ///     /trywield with correct bare-hex guids taken from the server's own /ci audit lines
    ///     produced no message and no effect, for Arrow (wcid 300) and Quarrel (31716),
    ///     with and without a matching launcher wielded. Missile rigs were impossible.
    ///  2. /ub useip, the plugin path another lane used, is NOT deterministic: it was
    ///     observed to silently no-op, unequip, or swap depending on state, and all three
    ///     are indistinguishable from chat. Not something to build rigs on.
    ///
    /// THE API, verified by reflection including parameter names:
    ///   Decal.Adapter.Wrappers.HooksWrapper
    ///     AutoWield(Int32 item)
    ///     AutoWield(Int32 item, Int32 slot, Int32 explic, Int32 notexplic)
    ///     AutoWield(Int32 item, Int32 slot, Int32 explic, Int32 notexplic, Int32 zero1, Int32 zero2)
    ///   backed by Decal.Interop.Core.IACHooks
    ///     AutoWield(Int32 lObjectID)
    ///     AutoWieldEx(Int32 lObjectID, Int32 SlotID, Int32 Explicit, Int32 NotExplicit)
    ///     AutoWieldRaw(...)
    ///
    /// AutoWield is a DEDICATED equip member, so this is not a MoveItem trick: MoveItem's
    /// destination is a PACK (its parameter is literally named packId), and there is no
    /// EquipMask-style enum anywhere in Decal.Adapter, so an equipment slot is not
    /// expressible as a MoveItem destination at all. AutoWield is the client-side equip
    /// path, and being a client hook it is almost certainly what UtilityBelt's useip
    /// ultimately drives - with the difference that this verb VERIFIES the outcome instead
    /// of leaving it indistinguishable from a no-op.
    ///
    /// VERIFICATION also settles an open empirical question. After the wield we re-read the
    /// equipped set and report whether the item carries the Wielder key, a Coverage mask,
    /// and whether it would satisfy the oracle's ammo heuristic. For ammunition that
    /// directly answers "does equipped ammo carry Wielder", which the oracle could only
    /// guess at.
    ///
    /// THREADING: the one-shot RenderFrame pattern used throughout this filter.
    /// </summary>
    class Wielder
    {
        private const int VERIFY_DELAY_MS = 500;
        private const int MAX_ATTEMPTS = 2;
        private const int MAX_LOGGED_CANDIDATES = 20;
        /// <summary>Let the client choose the slot. AutoWield(item) is the auto-slot form.</summary>
        private const int SLOT_Auto = -1;
        // EquipMask.MissileAmmo, verified against ACE EquipMask.cs:35. That enum's header
        // states it is the loc value sent in the player description message F7B0, i.e. the
        // protocol mask, which is what the client uses too.
        private const int MASK_MissileAmmo = 0x00800000;
        // Attempts beyond the first are escalation RUNGS, not blind retries.
        private const int MAX_RUNGS = 3;

        private const string OUTCOME_Requested = "requested";
        private const string OUTCOME_Ambiguous = "ambiguous";
        private const string OUTCOME_NotFound = "notfound";
        private const string OUTCOME_Failed = "failed";

        private enum Phase
        {
            Idle = 0,
            Resolve = 1,
            Verify = 2
        }

        private class Request
        {
            public string Target;
            public int Slot = SLOT_Auto;
        }

        private object _locker = new object();
        private Queue<Request> _pending = new Queue<Request>();
        private bool _subscribed;
        private Phase _phase = Phase.Idle;
        private Request _current;
        private EquippedItem _match;
        private int _attempts;
        private DateTime _verifyAtUtc = DateTime.MaxValue;
        // Which AutoWield form the last rung used, reported in the record.
        private string _lastMethod = "";

        /// <summary>Thread safe. Queue a wield for the next rendered frame.</summary>
        public void RequestWield(string target, int slot)
        {
            try
            {
                Request request = new Request();
                request.Target = (target == null ? "" : target);
                request.Slot = slot;
                log.WriteInfo("wield: requested for '{0}' slot={1}",
                    request.Target, (slot == SLOT_Auto ? "auto" : slot.ToString()));
                lock (_locker)
                {
                    _pending.Enqueue(request);
                    if (!_subscribed)
                    {
                        _subscribed = true;
                        CoreManager.Current.RenderFrame += new EventHandler<EventArgs>(Current_RenderFrame);
                    }
                }
            }
            catch (Exception exc)
            {
                log.WriteError("Wielder.RequestWield exception: {0}", exc);
            }
        }

        void Current_RenderFrame(object sender, EventArgs e)
        {
            try
            {
                lock (_locker)
                {
                    if (_phase == Phase.Verify)
                    {
                        if (DateTime.UtcNow < _verifyAtUtc) { return; }
                        DoVerify();
                    }
                    else if (_pending.Count > 0)
                    {
                        _current = _pending.Dequeue();
                        _attempts = 0;
                        _match = null;
                        DoResolveAndWield();
                    }

                    if (_phase == Phase.Idle && _pending.Count == 0 && _subscribed)
                    {
                        CoreManager.Current.RenderFrame -= new EventHandler<EventArgs>(Current_RenderFrame);
                        _subscribed = false;
                    }
                }
            }
            catch (Exception exc)
            {
                log.WriteError("Wielder.Current_RenderFrame exception: {0}", exc);
                lock (_locker) { _phase = Phase.Idle; }
            }
        }

        private void DoResolveAndWield()
        {
            string target = (_current.Target == null ? "" : _current.Target.Trim());
            if (target.Length == 0)
            {
                log.WriteError("wield: no target given; use 'wield <name-substring|wcid> [slot]'");
                WriteResult(target, OUTCOME_NotFound, null, null, "no target given", null);
                _phase = Phase.Idle;
                return;
            }

            int playerId = 0;
            try { playerId = CoreManager.Current.CharacterFilter.Id; }
            catch (Exception) { playerId = 0; }
            if (playerId == 0)
            {
                log.WriteError("wield: no character id; not logged in");
                WriteResult(target, OUTCOME_Failed, null, null, "no character id", null);
                _phase = Phase.Idle;
                return;
            }

            List<EquippedItem> carried = EquippedItems.ReadCarried(playerId);
            if (carried == null)
            {
                log.WriteError("wield '{0}': inventory not readable", target);
                WriteResult(target, OUTCOME_Failed, null, null, "inventory not readable", null);
                _phase = Phase.Idle;
                return;
            }

            // Exact id first. With several stacks of one item this is the ONLY
            // deterministic form, and the oracle always supplies the ids.
            EquippedItem exact;
            if (EquippedItems.TryMatchById(carried, target, out exact))
            {
                if (exact == null)
                {
                    log.WriteInfo("wield: no carried item with that id ({0})", target);
                    WriteResult(target, OUTCOME_NotFound, null, null, "no carried item with that id", null);
                    _phase = Phase.Idle;
                    return;
                }
                _match = exact;
                _attempts = 0;
                AttemptWield();
                return;
            }

            bool byWcid;
            List<EquippedItem> hits = EquippedItems.MatchByNameOrWcid(carried, target, out byWcid);
            if (hits.Count == 0)
            {
                log.WriteInfo(
                    "wield '{0}': no {1} match among {2} carried items; nothing equipped",
                    target, (byWcid ? "wcid" : "name"), carried.Count);
                LogItems(carried);
                WriteResult(target, OUTCOME_NotFound, null, null, "no match in the inventory", null);
                _phase = Phase.Idle;
                return;
            }
            if (hits.Count > 1)
            {
                // Never guess: equipping the wrong item silently changes the loadout a test
                // is measuring, exactly the failure mode that made /ub useip unusable.
                log.WriteInfo(
                    "wield '{0}': ambiguous, {1} matches; nothing equipped. Narrow the substring or use a wcid.",
                    target, hits.Count);
                // NEVER guess. /ub useip uses the FIRST name match, and with several
                // stacks of one item which one it grabs is uncontrolled, merging stacks in
                // either direction. Picking arbitrarily here would reproduce exactly the
                // bug this verb replaces. The candidate list carries every id and stack
                // count so the caller can re-issue with id:<id>.
                EquippedItems.SortByStackDescending(hits);
                LogItems(hits);
                WriteResult(target, OUTCOME_Ambiguous, null, null,
                    hits.Count + " matches; re-issue as wield id:<id>",
                    EquippedItems.DescribeCandidates(hits, MAX_LOGGED_CANDIDATES));
                _phase = Phase.Idle;
                return;
            }

            _match = hits[0];
            _attempts = 0;
            AttemptWield();
        }

        // ESCALATION LADDER, each rung verified. Live proof established that the plain
        // single-argument AutoWield equips WEAPONS but does NOT equip AMMUNITION: a
        // Quarrel stack reported equippedAfter false with BOTH HANDS EMPTY, and the client
        // ammo indicator stayed absent, so that was a real failure and not a blind verify.
        //
        //   rung 1  AutoWield(item)                 proven for weapons
        //   rung 2  AutoWield(item, mask, 0, 0)     explicit slot
        //   rung 3  AutoWield(item, mask, 1, 0)     explicit-flag variant
        //
        // The mask is not guessed. A caller-supplied slot wins; otherwise the ITEM's own
        // LongValueKey.EquipableSlots is used, because the item declares where it can go;
        // only if the item lacks that key do we fall back to EquipMask.MissileAmmo.
        //
        // explic/notexplic map to AutoWieldEx's Explicit/NotExplicit, whose semantics are
        // NOT documented in the assembly, which is why both 0 and 1 are tried rather than
        // one being asserted as correct.
        private void AttemptWield()
        {
            _attempts++;
            int mask = ChooseSlotMask();
            try
            {
                if (_attempts == 1 && _current.Slot == SLOT_Auto)
                {
                    CoreManager.Current.Actions.AutoWield(_match.Id);
                    _lastMethod = "AutoWield(item)";
                }
                else if (_attempts <= 2)
                {
                    CoreManager.Current.Actions.AutoWield(_match.Id, mask, 0, 0);
                    _lastMethod = string.Format("AutoWield(item, 0x{0:X}, 0, 0)", mask);
                }
                else
                {
                    CoreManager.Current.Actions.AutoWield(_match.Id, mask, 1, 0);
                    _lastMethod = string.Format("AutoWield(item, 0x{0:X}, 1, 0)", mask);
                }
                log.WriteInfo("wield: rung {0} via {1} for {2}", _attempts, _lastMethod, _match.Describe());
            }
            catch (Exception exc)
            {
                log.WriteError("wield: AutoWield threw on rung {0}: {1}", _attempts, exc);
            }
            _verifyAtUtc = DateTime.UtcNow.AddMilliseconds(VERIFY_DELAY_MS);
            _phase = Phase.Verify;
        }

        // Caller slot wins; else the item's declared EquipableSlots; else MissileAmmo.
        private int ChooseSlotMask()
        {
            if (_current.Slot != SLOT_Auto) { return _current.Slot; }
            if (_match != null && _match.EquipableSlots != 0) { return _match.EquipableSlots; }
            return MASK_MissileAmmo;
        }

        private void DoVerify()
        {
            int playerId = 0;
            try { playerId = CoreManager.Current.CharacterFilter.Id; }
            catch (Exception) { playerId = 0; }

            List<EquippedItem> equipped = EquippedItems.Read(playerId);
            if (equipped == null)
            {
                log.WriteError("wield: cannot re-read the equipped set to verify");
                WriteResult(_current.Target, OUTCOME_Failed, _match, null, "equipped set unreadable at verify", null);
                _phase = Phase.Idle;
                return;
            }

            EquippedItem after = EquippedItems.FindById(equipped, _match.Id);
            log.WriteInfo(
                "wield: verify attempt {0}: item {1} is {2}",
                _attempts, _match.Id, (after == null ? "NOT equipped" : "equipped as " + after.Describe()));

            if (after != null)
            {
                // This is the observation that settles the ammo question: whether an
                // equipped stackable actually carries Wielder, or only Coverage.
                log.WriteInfo(
                    "wield: post-equip keys for {0}: wielder={1} worn/coverage={2} slot={3} stack={4}",
                    _match.Id, after.Wielded, after.Worn, after.WieldingSlot, after.StackCount);
                WriteResult(_current.Target, OUTCOME_Requested, _match, after, "equipped via " + _lastMethod, null);
                _phase = Phase.Idle;
                return;
            }

            if (_attempts < MAX_RUNGS)
            {
                log.WriteInfo("wield: rung {0} did not equip, escalating", _attempts);
                AttemptWield();
                return;
            }

            log.WriteError(
                "wield: {0} not equipped after {1} attempts; slot may be occupied (unwield first) or the item may not be wieldable",
                _match.Describe(), _attempts);
            WriteResult(_current.Target, OUTCOME_Failed, _match, null,
                "not equipped after " + _attempts + " rungs; last method " + _lastMethod, null);
            _phase = Phase.Idle;
        }

        private static void LogItems(List<EquippedItem> items)
        {
            if (items == null) { return; }
            for (int i = 0; i < items.Count; i++)
            {
                if (i >= MAX_LOGGED_CANDIDATES)
                {
                    log.WriteInfo("wield: ...and {0} more", items.Count - i);
                    break;
                }
                log.WriteInfo("wield candidate: {0} wcid={1} stack={2}",
                    items[i].Describe(), items[i].Wcid, items[i].StackCount);
            }
        }

        private static void WriteResult(
            string target,
            string outcome,
            EquippedItem match,
            EquippedItem after,
            string detail,
            List<object> candidates)
        {
            try
            {
                Dictionary<string, object> entry = new Dictionary<string, object>();
                entry["utc"] = DateTime.UtcNow.ToString("o");
                entry["source"] = "filter";
                entry["type"] = "WieldResult";
                entry["target"] = target;
                entry["outcome"] = outcome;
                if (match != null)
                {
                    entry["resolvedId"] = match.Id;
                    entry["equipableSlots"] = (match.EquipableSlots != 0 ? (object)match.EquipableSlots : null);
                    entry["resolvedName"] = match.Name;
                    entry["objectClass"] = match.ObjectClass;
                    entry["wcid"] = (match.Wcid != 0 ? (object)match.Wcid : null);
                    entry["stackCount"] = (match.StackCount != 0 ? (object)match.StackCount : null);
                }
                if (after != null)
                {
                    // The post-move observation. carriesWielder is the field that settles
                    // whether equipped ammo is Wielder-linked client side.
                    entry["equippedAfter"] = true;
                    entry["wieldingSlot"] = (after.WieldingSlot >= 0 ? (object)after.WieldingSlot : null);
                    entry["carriesWielder"] = after.Wielded;
                    entry["carriesCoverage"] = after.Worn;
                    entry["looksLikeAmmo"] = (after.Wielded && after.StackCount > 0);
                }
                else if (outcome == OUTCOME_Failed || outcome == OUTCOME_Requested)
                {
                    entry["equippedAfter"] = false;
                }
                if (candidates != null) { entry["candidates"] = candidates; }
                entry["detail"] = detail;
                ChatLogWriter.WriteEntry(entry);
            }
            catch (Exception exc)
            {
                log.WriteError("Wielder.WriteResult exception: {0}", exc);
            }
        }
    }
}
