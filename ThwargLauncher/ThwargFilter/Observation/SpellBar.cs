using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

using Decal.Adapter;
using Decal.Adapter.Wrappers;

using Filter.Shared;

namespace ThwargFilter
{
    /// <summary>
    /// Spell bar reading, spell bar management, and CLIENT-INITIATED casting.
    ///
    /// WHY THIS EXISTS: the server-side /castspell primitive bypasses the client cast path
    /// entirely and fires faster than the client would allow. Anything that measures cast
    /// TIMING or modifies cast MECHANICS is therefore invisible to it, which retroactively
    /// explains ledger rows L6-52/53 (netherrush and flatcastspeed marked "structurally
    /// unobservable"): those abilities modify exactly the path /castspell skips. Real
    /// cast-speed and cast-stacking tests need the native client cast, which is what the
    /// numbered spell bar hotkeys trigger.
    ///
    /// WHAT DECAL ACTUALLY EXPOSES (all verified by reflection, all present):
    ///   READ   CharacterFilter.SpellBar(int tab) -> ReadOnlyCollection[int] of spell ids
    ///          CharacterFilter.SpellBook          -> ReadOnlyCollection[int] known spells
    ///          CharacterFilter.IsSpellKnown(int)
    ///   WRITE  Actions.SpellTabAdd(int, int, int)
    ///          Actions.SpellTabDelete(int, int)
    /// So the bar is both readable and writable; this is NOT a blind-and-verify-by-effect
    /// situation.
    ///
    /// ARGUMENT ORDER CAVEAT: the parameter order of SpellTabAdd/SpellTabDelete is not
    /// documented and cannot be settled from the assembly alone. ChangeSpellbarEventArgs
    /// carries Tab, Slot and SpellId, which tells us the triple but not its order. Rather
    /// than guess, the setter WRITES THEN READS BACK, and if the spell did not land it
    /// retries with the other plausible order and reports which one worked. That turns an
    /// undocumented signature into a fact the first live run establishes.
    ///
    /// THREADING: every entry point marshals onto the game thread with the one-shot
    /// RenderFrame pattern used elsewhere in this filter.
    /// </summary>
    class SpellBar
    {
        /// <summary>AC spell bars are per-tab; tab 0 is the first bar.</summary>
        public const int DEFAULT_TAB = 0;

        private const string ORDER_TabSlotSpell = "tab,slot,spell";
        private const string ORDER_TabSpellSlot = "tab,spell,slot";

        private object _locker = new object();
        private Queue<PendingOp> _pending = new Queue<PendingOp>();
        private bool _subscribed;

        private class PendingOp
        {
            public string Verb;
            public int Slot;
            public int SpellId;
            public int Tab = DEFAULT_TAB;
        }

        // ---------- entry points, callable from any thread ----------

        public void RequestClear(int tab)
        {
            Enqueue("clear", 0, 0, tab);
        }

        public void RequestSet(int slot, int spellId, int tab)
        {
            Enqueue("set", slot, spellId, tab);
        }

        public void RequestCast(int slot)
        {
            Enqueue("cast", slot, 0, DEFAULT_TAB);
        }

        private void Enqueue(string verb, int slot, int spellId, int tab)
        {
            try
            {
                PendingOp op = new PendingOp();
                op.Verb = verb;
                op.Slot = slot;
                op.SpellId = spellId;
                op.Tab = tab;
                log.WriteInfo("spellbar: queued '{0}' slot={1} spellId={2} tab={3}", verb, slot, spellId, tab);
                lock (_locker)
                {
                    _pending.Enqueue(op);
                    if (!_subscribed)
                    {
                        _subscribed = true;
                        CoreManager.Current.RenderFrame += new EventHandler<EventArgs>(Current_RenderFrame);
                    }
                }
            }
            catch (Exception exc)
            {
                log.WriteError("SpellBar.Enqueue exception: {0}", exc);
            }
        }

        void Current_RenderFrame(object sender, EventArgs e)
        {
            try
            {
                PendingOp op = null;
                lock (_locker)
                {
                    if (_pending.Count > 0) { op = _pending.Dequeue(); }
                    if (_pending.Count == 0 && _subscribed)
                    {
                        CoreManager.Current.RenderFrame -= new EventHandler<EventArgs>(Current_RenderFrame);
                        _subscribed = false;
                    }
                }
                if (op == null) { return; }
                if (op.Verb == "clear") { DoClear(op.Tab); }
                else if (op.Verb == "set") { DoSet(op.Tab, op.Slot, op.SpellId); }
                else if (op.Verb == "cast") { DoCast(op.Slot); }
            }
            catch (Exception exc)
            {
                log.WriteError("SpellBar.Current_RenderFrame exception: {0}", exc);
            }
        }

        // ---------- game-thread work ----------

        private void DoClear(int tab)
        {
            try
            {
                ReadOnlyCollection<int> bar = ReadBar(tab);
                if (bar == null)
                {
                    log.WriteError("spellbar clear: cannot read bar for tab {0}", tab);
                    return;
                }
                int removed = 0;
                // Walk high to low: removing a low slot may shift the ones above it.
                for (int slot = bar.Count - 1; slot >= 0; slot--)
                {
                    int spellId = bar[slot];
                    if (spellId == 0) { continue; }
                    if (TryDelete(tab, slot, spellId)) { removed++; }
                }
                ReadOnlyCollection<int> after = ReadBar(tab);
                int remaining = CountOccupied(after);
                log.WriteInfo("spellbar clear: removed {0}, {1} slots still occupied", removed, remaining);
            }
            catch (Exception exc)
            {
                log.WriteError("spellbar clear exception: {0}", exc);
            }
        }

        /// <summary>
        /// Place a spell, then READ THE BAR BACK to confirm. If it did not land, retry with
        /// the other plausible argument order. Reports which order worked so the ambiguity
        /// is settled by the first live run rather than by guesswork.
        /// </summary>
        private void DoSet(int tab, int slot, int spellId)
        {
            try
            {
                bool known = IsSpellKnown(spellId);
                if (!known)
                {
                    // Not fatal: report it and continue, because the client may still accept
                    // the placement and the harness needs to see the discrepancy either way.
                    log.WriteError("spellbar set: spell {0} is NOT in the spellbook; placing anyway", spellId);
                }

                string order = null;
                if (TryAdd(tab, slot, spellId, ORDER_TabSlotSpell)) { order = ORDER_TabSlotSpell; }
                else if (TryAdd(tab, slot, spellId, ORDER_TabSpellSlot)) { order = ORDER_TabSpellSlot; }

                if (order == null)
                {
                    log.WriteError(
                        "spellbar set: spell {0} did not land in tab {1} slot {2} under either argument order",
                        spellId, tab, slot);
                }
                else
                {
                    log.WriteInfo(
                        "spellbar set: spell {0} landed in tab {1} slot {2} using SpellTabAdd argument order '{3}'",
                        spellId, tab, slot, order);
                }
            }
            catch (Exception exc)
            {
                log.WriteError("spellbar set exception: {0}", exc);
            }
        }

        private bool TryAdd(int tab, int slot, int spellId, string order)
        {
            try
            {
                if (order == ORDER_TabSlotSpell)
                {
                    CoreManager.Current.Actions.SpellTabAdd(tab, slot, spellId);
                }
                else
                {
                    CoreManager.Current.Actions.SpellTabAdd(tab, spellId, slot);
                }
            }
            catch (Exception exc)
            {
                log.WriteError("spellbar set: SpellTabAdd order '{0}' threw: {1}", order, exc);
                return false;
            }
            return SlotHolds(tab, slot, spellId);
        }

        private bool TryDelete(int tab, int slot, int spellId)
        {
            try
            {
                CoreManager.Current.Actions.SpellTabDelete(tab, slot);
            }
            catch (Exception exc)
            {
                log.WriteError("spellbar clear: SpellTabDelete(tab,slot) threw: {0}", exc);
            }
            if (!SlotHolds(tab, slot, spellId)) { return true; }
            try
            {
                CoreManager.Current.Actions.SpellTabDelete(tab, spellId);
            }
            catch (Exception exc)
            {
                log.WriteError("spellbar clear: SpellTabDelete(tab,spellId) threw: {0}", exc);
            }
            return !SlotHolds(tab, slot, spellId);
        }

        private bool SlotHolds(int tab, int slot, int spellId)
        {
            ReadOnlyCollection<int> bar = ReadBar(tab);
            if (bar == null) { return false; }
            if (slot < 0 || slot >= bar.Count) { return false; }
            return bar[slot] == spellId;
        }

        /// <summary>
        /// Post the numbered hotkey for a 1-based slot. This is the whole point: it is the
        /// NATIVE client cast trigger, so animations and cast timing are the client's own.
        /// </summary>
        private void DoCast(int slot)
        {
            int spellId = 0;
            string spellName = null;
            string combatMode = null;
            try { combatMode = CoreManager.Current.Actions.CombatMode.ToString(); }
            catch (Exception) { combatMode = null; }

            try
            {
                ReadOnlyCollection<int> bar = ReadBar(DEFAULT_TAB);
                // The oracle and the hotkeys disagree on base: the bar collection is
                // 0-based, the hotkeys are 1-based.
                if (bar != null && slot >= 1 && slot <= bar.Count) { spellId = bar[slot - 1]; }
            }
            catch (Exception) { }
            if (spellId != 0) { spellName = "spellId " + spellId.ToString(); }

            NamedKey key = NamedKeys.FindSlotKey(slot);
            if (key == null)
            {
                log.WriteError("cast: slot {0} has no hotkey; valid slots are 1 to {1}", slot, NamedKeys.MAX_SLOT);
                WriteCastResult(slot, "invalidslot", spellId, spellName, combatMode, null);
                return;
            }

            bool posted = false;
            try
            {
                PostMessageTools.SendNamedKeyDown(key);
                PostMessageTools.SendNamedKeyUp(key);
                posted = true;
                log.WriteInfo(
                    "cast: posted {0} for slot {1} (spellId {2}, combat mode {3})",
                    key.Name, slot, spellId, combatMode);
            }
            catch (Exception exc)
            {
                log.WriteError("cast: could not post hotkey for slot {0}: {1}", slot, exc);
            }
            WriteCastResult(slot, (posted ? "requested" : "failed"), spellId, spellName, combatMode, key.Name);
        }

        // ---------- shared reads, also used by the oracle ----------

        public static ReadOnlyCollection<int> ReadBar(int tab)
        {
            try
            {
                return CoreManager.Current.CharacterFilter.SpellBar(tab);
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static bool IsSpellKnown(int spellId)
        {
            try
            {
                return CoreManager.Current.CharacterFilter.IsSpellKnown(spellId);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static int CountOccupied(ReadOnlyCollection<int> bar)
        {
            if (bar == null) { return 0; }
            int n = 0;
            for (int i = 0; i < bar.Count; i++) { if (bar[i] != 0) { n++; } }
            return n;
        }

        private static void WriteCastResult(
            int slot,
            string outcome,
            int spellId,
            string spellName,
            string combatMode,
            string keyName)
        {
            try
            {
                Dictionary<string, object> entry = new Dictionary<string, object>();
                entry["utc"] = DateTime.UtcNow.ToString("o");
                entry["source"] = "filter";
                entry["type"] = "CastResult";
                entry["slot"] = slot;
                entry["outcome"] = outcome;
                entry["spellId"] = (spellId != 0 ? (object)spellId : null);
                entry["spellName"] = spellName;
                // Pre-cast combat mode: a cast from Peace will not fire, and this is the
                // field that shows it without a separate dumpstate.
                entry["combatMode"] = combatMode;
                entry["key"] = keyName;
                ChatLogWriter.WriteEntry(entry);
            }
            catch (Exception exc)
            {
                log.WriteError("SpellBar.WriteCastResult exception: {0}", exc);
            }
        }
    }
}
