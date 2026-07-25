using System;
using System.Collections.Generic;

using Decal.Adapter;
using Decal.Adapter.Wrappers;

using Filter.Shared;

namespace ThwargFilter
{
    /// <summary>
    /// Implements the "attack" and "attackstop" verbs.
    ///
    /// WHY THIS IS NOT A SIMPLE API CALL: the referenced Decal.Adapter 2.9.7.5 exposes NO
    /// attack surface. Verified by full member enumeration of Decal.Adapter.Wrappers
    /// .HooksWrapper and of the raw COM interface behind it, Decal.Interop.Core.IACHooks
    /// (137 members). The only combat-adjacent members are:
    ///   SetCombatMode(CombatState)    enter or leave combat mode
    ///   CombatMode                    read the current mode
    ///   SelectItem(int) / CurrentSelection    client side selection
    ///   UseItem(int, int)             "use" an object
    /// There is no Attack, AttackSelected, MeleeAttack or equivalent, and Decal exposes no
    /// way to send a client to server message either (Decal.Interop.Net's Dispatch members
    /// are the inbound callback interface Decal calls ON filters, not a send path).
    ///
    /// So attacking is driven the only way left: select the target, set combat mode, and
    /// post the client's own attack key.
    ///
    /// THE RECIPE IS LIVE-VERIFIED (batch 5, against a target dummy, melee and missile):
    /// after SelectItem and SetCombatMode, post WM_KEYDOWN vk=VK_END(0x23) with
    /// lParam=0x014F0001, hold about 200ms, then WM_KEYUP with lParam=0xC14F0001. Note the
    /// extended-key bit: End is an extended key, and a lParam without it does nothing.
    ///
    /// ONE TAP IS ENOUGH. The tap starts the client's own repeating attack loop; it does
    /// not need to be held down continuously. A second tap does NOT stop it either, so
    /// attackstop (peace mode) is the only way to stop attacking.
    ///
    /// THREADING: same one-shot RenderFrame marshalling as Appraiser and GameStateDumper.
    /// </summary>
    class Attacker
    {
        private const int MAX_LOGGED_CANDIDATES = 20;

        private const string OUTCOME_Requested = "requested";
        private const string OUTCOME_Ambiguous = "ambiguous";
        private const string OUTCOME_NotFound = "notfound";

        // Settings keys, read from the filter's AssemblySettings so a live rig can retune
        // the synthetic input without a rebuild. See TESTING_CHANNEL.md.
        private const string SETTING_AttackKey = "AttackKey";
        private const string SETTING_AttackMethod = "AttackMethod";
        private const string SETTING_AttackCombatMode = "AttackCombatMode";
        private const string SETTING_AttackKeyHoldMs = "AttackKeyHoldMs";

        // The live-verified defaults. Changing these changes a proven path.
        private const string DEFAULT_AttackKey = "End";
        private const int DEFAULT_AttackKeyHoldMs = 200;

        private const string METHOD_Key = "key";
        private const string METHOD_UseItem = "useitem";
        private const string METHOD_Both = "both";

        private object _locker = new object();
        private Queue<string> _pendingTargets = new Queue<string>();
        private bool _stopPending;
        private bool _subscribed;

        // What we are currently holding down, so attackstop can release exactly that key
        // even if the setting changes between attack and attackstop. The release is done
        // on a later render frame rather than by sleeping: this runs on the GAME thread,
        // and blocking it for the hold duration would stall rendering.
        private bool _keyHeld;
        private NamedKey _heldKey;
        private DateTime _releaseDeadlineUtc = DateTime.MaxValue;

        /// <summary>
        /// Thread safe. Queues an attack on the next rendered frame.
        /// </summary>
        public void RequestAttack(string target)
        {
            try
            {
                if (target == null) { target = ""; }
                log.WriteInfo("Attacker: attack requested for '{0}'", target);
                lock (_locker)
                {
                    _pendingTargets.Enqueue(target);
                    Subscribe();
                }
            }
            catch (Exception exc)
            {
                log.WriteError("Attacker.RequestAttack exception: {0}", exc);
            }
        }

        /// <summary>
        /// Thread safe. Queues a stop on the next rendered frame.
        /// </summary>
        public void RequestStop()
        {
            try
            {
                log.WriteInfo("Attacker: attackstop requested");
                lock (_locker)
                {
                    _stopPending = true;
                    Subscribe();
                }
            }
            catch (Exception exc)
            {
                log.WriteError("Attacker.RequestStop exception: {0}", exc);
            }
        }

        // Caller must hold _locker.
        private void Subscribe()
        {
            if (!_subscribed)
            {
                _subscribed = true;
                CoreManager.Current.RenderFrame += new EventHandler<EventArgs>(Current_RenderFrame);
            }
        }

        void Current_RenderFrame(object sender, EventArgs e)
        {
            try
            {
                string target = null;
                bool haveTarget = false;
                bool doStop = false;
                bool doRelease = false;
                lock (_locker)
                {
                    // A stop always wins over queued attacks: "attackstop" must mean stop,
                    // not "stop after working through the backlog".
                    if (_stopPending)
                    {
                        _stopPending = false;
                        doStop = true;
                        _pendingTargets.Clear();
                    }
                    else if (_keyHeld && DateTime.UtcNow >= _releaseDeadlineUtc)
                    {
                        // The hold elapsed. Release before starting anything else, so two
                        // queued attacks cannot overlap their keypresses.
                        doRelease = true;
                    }
                    else if (!_keyHeld && _pendingTargets.Count > 0)
                    {
                        target = _pendingTargets.Dequeue();
                        haveTarget = true;
                    }
                    // Stay subscribed while anything is outstanding, INCLUDING a key we
                    // still owe a release for.
                    if (_pendingTargets.Count == 0 && !_stopPending && !_keyHeld
                        && !doStop && !doRelease && !haveTarget && _subscribed)
                    {
                        CoreManager.Current.RenderFrame -= new EventHandler<EventArgs>(Current_RenderFrame);
                        _subscribed = false;
                    }
                }
                if (doStop) { DoStop(); }
                else if (doRelease) { ReleaseAttackKey(); }
                else if (haveTarget) { DoAttack(target); }
            }
            catch (Exception exc)
            {
                log.WriteError("Attacker.Current_RenderFrame exception: {0}", exc);
            }
        }

        /// <summary>Runs on the game thread.</summary>
        private void DoAttack(string target)
        {
            try
            {
                string trimmed = (target == null ? "" : target.Trim());
                if (trimmed.Length == 0)
                {
                    log.WriteInfo("attack: no target given; use 'attack <name-substring>'");
                    WriteResult("", OUTCOME_NotFound, 0, null, 0);
                    return;
                }
                List<TargetCandidate> candidates = TargetResolver.Collect(trimmed);
                if (candidates == null)
                {
                    log.WriteError("attack '{0}': could not read the world object list", trimmed);
                    WriteResult(trimmed, OUTCOME_NotFound, 0, null, 0);
                    return;
                }
                if (candidates.Count == 1)
                {
                    TargetCandidate match = candidates[0];
                    log.WriteInfo("attack '{0}': matched {1}", trimmed, match.Describe());
                    EngageTarget(match);
                    WriteResult(trimmed, OUTCOME_Requested, match.Id, match.Name, 1);
                    return;
                }
                // Same rule as appraise: never guess. Attacking the wrong creature is
                // worse than not attacking, because it is not silently recoverable.
                if (candidates.Count == 0)
                {
                    log.WriteInfo("attack '{0}': no match; not attacking", trimmed);
                    WriteResult(trimmed, OUTCOME_NotFound, 0, null, 0);
                }
                else
                {
                    log.WriteInfo(
                        "attack '{0}': ambiguous, {1} matches; not attacking. Narrow the substring.",
                        trimmed,
                        candidates.Count);
                    WriteResult(trimmed, OUTCOME_Ambiguous, 0, null, candidates.Count);
                }
                TargetResolver.LogCandidates("attack", candidates, MAX_LOGGED_CANDIDATES);
            }
            catch (Exception exc)
            {
                log.WriteError("Attacker.DoAttack exception: {0}", exc);
            }
        }

        /// <summary>
        /// Select the target, enter combat mode, and synthesize the attack input.
        /// Each step is independently guarded so a later failure still leaves the earlier
        /// steps applied and logged.
        /// </summary>
        private void EngageTarget(TargetCandidate match)
        {
            try
            {
                CoreManager.Current.Actions.SelectItem(match.Id);
                CoreManager.Current.Actions.CurrentSelection = match.Id;
                log.WriteInfo("attack: selected {0}", match.Id);
            }
            catch (Exception exc)
            {
                log.WriteError("attack: SelectItem({0}) failed: {1}", match.Id, exc);
            }

            CombatState mode = GetConfiguredCombatMode();
            try
            {
                CoreManager.Current.Actions.SetCombatMode(mode);
                log.WriteInfo("attack: combat mode set to {0}", mode);
            }
            catch (Exception exc)
            {
                log.WriteError("attack: SetCombatMode({0}) failed: {1}", mode, exc);
            }

            string method = GetSetting(SETTING_AttackMethod, METHOD_Key).Trim().ToLower();
            if (method == METHOD_UseItem || method == METHOD_Both)
            {
                try
                {
                    CoreManager.Current.Actions.UseItem(match.Id, 0);
                    log.WriteInfo("attack: UseItem({0}, 0) issued", match.Id);
                }
                catch (Exception exc)
                {
                    log.WriteError("attack: UseItem({0}) failed: {1}", match.Id, exc);
                }
            }
            if (method == METHOD_Key || method == METHOD_Both)
            {
                TapAttackKey();
            }
            if (method != METHOD_Key && method != METHOD_UseItem && method != METHOD_Both)
            {
                log.WriteError(
                    "attack: unrecognized {0} '{1}'; expected '{2}', '{3}' or '{4}'. No input synthesized.",
                    SETTING_AttackMethod, method, METHOD_Key, METHOD_UseItem, METHOD_Both);
            }
        }

        /// <summary>
        /// Post the attack key down and schedule its release. One tap starts the client's
        /// repeating attack loop, so this is a tap, not a sustained hold. The release runs
        /// from a later render frame; sleeping here would block the game thread.
        /// </summary>
        private void TapAttackKey()
        {
            NamedKey key = GetAttackKey();
            try
            {
                ReleaseAttackKey();
                PostMessageTools.SendNamedKeyDown(key);
                lock (_locker)
                {
                    _keyHeld = true;
                    _heldKey = key;
                    _releaseDeadlineUtc = DateTime.UtcNow.AddMilliseconds(GetAttackKeyHoldMs());
                }
                log.WriteInfo("attack: posted key down {0}", key);
            }
            catch (Exception exc)
            {
                log.WriteError("attack: could not post attack key {0}: {1}", key, exc);
            }
        }

        private void ReleaseAttackKey()
        {
            bool held;
            NamedKey key;
            lock (_locker)
            {
                held = _keyHeld;
                key = _heldKey;
                _keyHeld = false;
                _releaseDeadlineUtc = DateTime.MaxValue;
            }
            if (!held || key == null) { return; }
            try
            {
                PostMessageTools.SendNamedKeyUp(key);
                log.WriteInfo("attack: posted key up {0}", key);
            }
            catch (Exception exc)
            {
                log.WriteError("attack: could not release attack key {0}: {1}", key, exc);
            }
        }

        /// <summary>Runs on the game thread.</summary>
        private void DoStop()
        {
            ReleaseAttackKey();
            try
            {
                CoreManager.Current.Actions.SetCombatMode(CombatState.Peace);
                log.WriteInfo("attackstop: combat mode set to Peace");
            }
            catch (Exception exc)
            {
                log.WriteError("attackstop: SetCombatMode(Peace) failed: {0}", exc);
            }
        }

        private CombatState GetConfiguredCombatMode()
        {
            string text = GetSetting(SETTING_AttackCombatMode, "Melee").Trim();
            if (string.Compare(text, "Missile", StringComparison.OrdinalIgnoreCase) == 0)
            {
                return CombatState.Missile;
            }
            if (string.Compare(text, "Magic", StringComparison.OrdinalIgnoreCase) == 0)
            {
                return CombatState.Magic;
            }
            if (string.Compare(text, "Melee", StringComparison.OrdinalIgnoreCase) != 0)
            {
                log.WriteError(
                    "attack: unrecognized {0} '{1}'; falling back to Melee",
                    SETTING_AttackCombatMode, text);
            }
            return CombatState.Melee;
        }

        /// <summary>
        /// The attack key by NAME, not by character. A character cannot express End or the
        /// other navigation keys at all (see NamedKeys), which is why the old char setting
        /// could never have worked.
        /// </summary>
        private NamedKey GetAttackKey()
        {
            string text = GetSetting(SETTING_AttackKey, DEFAULT_AttackKey);
            NamedKey key = NamedKeys.Find(text);
            if (key != null) { return key; }
            log.WriteError(
                "attack: unrecognized {0} '{1}'; expected one of [{2}]. Falling back to {3}.",
                SETTING_AttackKey, text, NamedKeys.GetKnownNames(), DEFAULT_AttackKey);
            return NamedKeys.Find(DEFAULT_AttackKey);
        }

        private int GetAttackKeyHoldMs()
        {
            string text = GetSetting(SETTING_AttackKeyHoldMs, DEFAULT_AttackKeyHoldMs.ToString());
            int value = DEFAULT_AttackKeyHoldMs;
            if (!int.TryParse(text, out value)) { return DEFAULT_AttackKeyHoldMs; }
            if (value < 1 || value > 5000) { return DEFAULT_AttackKeyHoldMs; }
            return value;
        }

        private static string GetSetting(string key, string defaultValue)
        {
            try
            {
                AssemblySettings settings = new AssemblySettings();
                string value = settings.GetValue(key, defaultValue);
                if (string.IsNullOrEmpty(value)) { return defaultValue; }
                return value;
            }
            catch (Exception)
            {
                return defaultValue;
            }
        }

        /// <summary>
        /// Same record shape as AppraiseResult, so a harness handles both the same way.
        /// outcome "requested" means the input was issued, NOT that the character is
        /// confirmed to be swinging: see the reliability caveats in TESTING_CHANNEL.md.
        /// </summary>
        private static void WriteResult(
            string target,
            string outcome,
            int resolvedId,
            string resolvedName,
            int candidateCount)
        {
            try
            {
                Dictionary<string, object> entry = new Dictionary<string, object>();
                entry["utc"] = DateTime.UtcNow.ToString("o");
                entry["source"] = "filter";
                entry["type"] = "AttackResult";
                entry["target"] = target;
                entry["outcome"] = outcome;
                if (outcome == OUTCOME_Requested)
                {
                    entry["resolvedId"] = resolvedId;
                    entry["resolvedName"] = resolvedName;
                }
                if (outcome == OUTCOME_Ambiguous)
                {
                    entry["candidateCount"] = candidateCount;
                }
                ChatLogWriter.WriteEntry(entry);
            }
            catch (Exception exc)
            {
                log.WriteError("Attacker.WriteResult exception: {0}", exc);
            }
        }
    }
}
