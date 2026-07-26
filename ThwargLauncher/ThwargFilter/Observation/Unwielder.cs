using System;
using System.Collections.Generic;

using Decal.Adapter;
using Decal.Adapter.Wrappers;

namespace ThwargFilter
{
    /// <summary>
    /// The "unwield" verb: move a worn or wielded item back into the pack, so a rig can
    /// free a hand and swap loadout.
    ///
    /// WHY THIS EXISTS: there was no way to empty a character's hands from the command
    /// channel at all. Server side, /trywield refuses while a hand is occupied
    /// (Player_Inventory.cs CheckWeaponCollision, EquipMask.Held), moving a wielded item
    /// between slots is blocked by WieldedLocationIsAvailable, and every other dequip path
    /// either drops the WHOLE inventory on the ground (/fumble) or needs the item to be the
    /// client's last-appraised object. Equipped items cannot be appraised: TargetResolver
    /// scans landscape and players only, so an equipped shield resolves notfound. Net
    /// effect: no caster swap, no ammo swap, no loadout change of any kind.
    ///
    /// This solves it entirely client side. The target is resolved from the EQUIPPED SET,
    /// which already knows every wielded item, its id and its slot, so the appraisal
    /// problem never arises.
    ///
    /// THE API, verified by reflection including parameter names:
    ///   Decal.Adapter.Wrappers.HooksWrapper
    ///     MoveItem(Int32 objectId, Int32 destinationId)
    ///     MoveItem(Int32 objectId, Int32 destinationId, Int32 moveFlags)
    ///     MoveItem(Int32 objectId, Int32 packId, Int32 slot, Boolean stack)   <- used
    ///   backed by Decal.Interop.Core.IACHooks
    ///     MoveItem(Int32 lObjectID, Int32 lPackID, Int32 lSlot, Boolean bStack)
    /// The parameter names settle the argument order outright: object first, then the
    /// destination PACK, then the slot within it, then whether to stack.
    ///
    /// The main pack's container id is the CHARACTER's own id. That is not a guess: items
    /// sitting in the main pack carry LongValueKey.Container equal to the character id,
    /// which is what the oracle's inventory section reports.
    ///
    /// DROPPING IS NOT USED. Actions.DropItem exists but litters the world, so it is
    /// deliberately not a fallback here.
    ///
    /// THREADING: the one-shot RenderFrame pattern used throughout this filter. The move is
    /// verified on a later frame by re-reading the equipped set, with one retry.
    /// </summary>
    class Unwielder
    {
        private const int VERIFY_DELAY_MS = 500;
        private const int MAX_ATTEMPTS = 2;
        private const int MAX_LOGGED_CANDIDATES = 20;
        /// <summary>Let the client choose a free slot rather than fighting it for one.</summary>
        private const int ANY_SLOT = 0;

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

        private object _locker = new object();
        private Queue<string> _pending = new Queue<string>();
        private bool _subscribed;
        private Phase _phase = Phase.Idle;
        private string _target;
        private EquippedItem _match;
        private int _attempts;
        private DateTime _verifyAtUtc = DateTime.MaxValue;

        /// <summary>Thread safe. Queue an unwield for the next rendered frame.</summary>
        public void RequestUnwield(string target)
        {
            try
            {
                if (target == null) { target = ""; }
                log.WriteInfo("unwield: requested for '{0}'", target);
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
                log.WriteError("Unwielder.RequestUnwield exception: {0}", exc);
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
                        _target = _pending.Dequeue();
                        _attempts = 0;
                        _match = null;
                        DoResolveAndMove();
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
                log.WriteError("Unwielder.Current_RenderFrame exception: {0}", exc);
                lock (_locker) { _phase = Phase.Idle; }
            }
        }

        // Runs on the game thread with _locker held.
        private void DoResolveAndMove()
        {
            string target = (_target == null ? "" : _target.Trim());
            if (target.Length == 0)
            {
                log.WriteError("unwield: no target given; use 'unwield <name-substring|slot>'");
                WriteResult(target, OUTCOME_NotFound, null, "no target given");
                _phase = Phase.Idle;
                return;
            }

            int playerId = 0;
            try { playerId = CoreManager.Current.CharacterFilter.Id; }
            catch (Exception) { playerId = 0; }
            if (playerId == 0)
            {
                log.WriteError("unwield: no character id; not logged in");
                WriteResult(target, OUTCOME_Failed, null, "no character id");
                _phase = Phase.Idle;
                return;
            }

            List<EquippedItem> equipped = EquippedItems.Read(playerId);
            if (equipped == null)
            {
                log.WriteError("unwield '{0}': equipped set not readable", target);
                WriteResult(target, OUTCOME_Failed, null, "equipped set not readable");
                _phase = Phase.Idle;
                return;
            }

            bool bySlot;
            List<EquippedItem> hits = EquippedItems.Match(equipped, target, out bySlot);
            if (hits.Count == 0)
            {
                log.WriteInfo(
                    "unwield '{0}': no {1} match among {2} equipped items; nothing moved",
                    target, (bySlot ? "slot" : "name"), equipped.Count);
                LogEquipped(equipped);
                WriteResult(target, OUTCOME_NotFound, null, "no match in the equipped set");
                _phase = Phase.Idle;
                return;
            }
            if (hits.Count > 1)
            {
                // Same rule as appraise and attack: never guess. Unwielding the wrong item
                // silently changes the loadout a test is measuring.
                log.WriteInfo(
                    "unwield '{0}': ambiguous, {1} matches; nothing moved. Narrow the substring or use a slot.",
                    target, hits.Count);
                LogEquipped(hits);
                WriteResult(target, OUTCOME_Ambiguous, null, hits.Count + " matches");
                _phase = Phase.Idle;
                return;
            }

            _match = hits[0];
            _attempts = 0;
            AttemptMove(playerId);
        }

        private void AttemptMove(int playerId)
        {
            _attempts++;
            try
            {
                // objectId, packId (the character's own id IS the main pack), slot, stack.
                CoreManager.Current.Actions.MoveItem(_match.Id, playerId, ANY_SLOT, false);
                log.WriteInfo(
                    "unwield: MoveItem({0}, pack {1}, slot any, stack false) attempt {2} for {3}",
                    _match.Id, playerId, _attempts, _match.Describe());
            }
            catch (Exception exc)
            {
                log.WriteError("unwield: MoveItem threw on attempt {0}: {1}", _attempts, exc);
            }
            _verifyAtUtc = DateTime.UtcNow.AddMilliseconds(VERIFY_DELAY_MS);
            _phase = Phase.Verify;
        }

        private void DoVerify()
        {
            int playerId = 0;
            try { playerId = CoreManager.Current.CharacterFilter.Id; }
            catch (Exception) { playerId = 0; }

            List<EquippedItem> equipped = EquippedItems.Read(playerId);
            if (equipped == null)
            {
                log.WriteError("unwield: cannot re-read the equipped set to verify");
                WriteResult(_target, OUTCOME_Failed, _match, "equipped set unreadable at verify");
                _phase = Phase.Idle;
                return;
            }

            bool stillEquipped = EquippedItems.ContainsId(equipped, _match.Id);
            log.WriteInfo(
                "unwield: verify attempt {0}: item {1} is {2}",
                _attempts, _match.Id, (stillEquipped ? "STILL equipped" : "no longer equipped"));

            if (!stillEquipped)
            {
                WriteResult(_target, OUTCOME_Requested, _match, "moved to pack");
                _phase = Phase.Idle;
                return;
            }

            if (_attempts < MAX_ATTEMPTS)
            {
                AttemptMove(playerId);
                return;
            }

            // Report rather than hang. A full pack is the likeliest cause and the rig can
            // act on that.
            log.WriteError(
                "unwield: {0} still equipped after {1} attempts; pack may be full",
                _match.Describe(), _attempts);
            WriteResult(_target, OUTCOME_Failed, _match,
                "still equipped after " + _attempts + " attempts; pack may be full");
            _phase = Phase.Idle;
        }

        private static void LogEquipped(List<EquippedItem> items)
        {
            if (items == null) { return; }
            for (int i = 0; i < items.Count; i++)
            {
                if (i >= MAX_LOGGED_CANDIDATES)
                {
                    log.WriteInfo("unwield: ...and {0} more", items.Count - i);
                    break;
                }
                log.WriteInfo("unwield candidate: {0}", items[i].Describe());
            }
        }

        private static void WriteResult(string target, string outcome, EquippedItem match, string detail)
        {
            try
            {
                Dictionary<string, object> entry = new Dictionary<string, object>();
                entry["utc"] = DateTime.UtcNow.ToString("o");
                entry["source"] = "filter";
                entry["type"] = "UnwieldResult";
                entry["target"] = target;
                entry["outcome"] = outcome;
                if (match != null)
                {
                    entry["resolvedId"] = match.Id;
                    entry["resolvedName"] = match.Name;
                    entry["objectClass"] = match.ObjectClass;
                    entry["fromSlot"] = (match.WieldingSlot >= 0 ? (object)match.WieldingSlot : null);
                    entry["wasWielded"] = match.Wielded;
                }
                entry["detail"] = detail;
                ChatLogWriter.WriteEntry(entry);
            }
            catch (Exception exc)
            {
                log.WriteError("Unwielder.WriteResult exception: {0}", exc);
            }
        }
    }
}
