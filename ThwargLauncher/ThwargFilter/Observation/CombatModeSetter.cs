using System;
using System.Collections.Generic;

using Decal.Adapter;
using Decal.Adapter.Wrappers;

using Filter.Shared;

namespace ThwargFilter
{
    /// <summary>
    /// What a combat mode change actually achieved, handed back once it has been verified
    /// or has exhausted the ladder.
    /// </summary>
    class CombatModeResult
    {
        /// <summary>The mode asked for, or Peace when the caller only wanted "any combat".</summary>
        public CombatState Requested;
        /// <summary>True when the caller accepted any combat mode rather than a specific one.</summary>
        public bool AnyCombatAccepted;
        /// <summary>The mode actually observed on the final verify.</summary>
        public CombatState Final;
        /// <summary>True when the goal was met.</summary>
        public bool Verified;
        /// <summary>Toggle attempts beyond the first (0 when it worked first time).</summary>
        public int Retries;
        /// <summary>True when the Backtick toggle was used.</summary>
        public bool UsedToggle;
        /// <summary>True when the optional SetCombatMode rung was used.</summary>
        public bool UsedSetCombatMode;
        /// <summary>True when the request was rejected as impossible for the wielded weapon.</summary>
        public bool ImpossibleRequest;
        /// <summary>What was wielded, so a mismatch is self-evident in the record.</summary>
        public string Weapon = "";
        public string Detail = "";
        /// <summary>Mode observed at each verify, oldest first.</summary>
        public List<string> Observed = new List<string>();
    }

    delegate void CombatModeCallback(CombatModeResult result);

    /// <summary>
    /// Drives the client into a combat mode, using the model the CLIENT actually implements.
    ///
    /// THE MODEL, corrected. Combat mode is NOT an independently settable axis:
    ///   * Backtick toggles Peace and Combat.
    ///   * WHICH combat mode you get is DERIVED from the wielded weapon.
    ///     wand/staff/orb -> Magic, bow -> Missile, melee weapon (or unarmed) -> Melee.
    /// A player cannot reach "melee mode while carrying a wand"; the client refuses with
    /// "You can't enter melee mode while carrying a wand". So the rig sequence is always
    /// WIELD THE RIGHT WEAPON FIRST, THEN ENTER COMBAT - never "set a mode then wield".
    ///
    /// This very likely explains ledger L6-76, where SetCombatMode appeared to no-op in
    /// 2 of 4 runs: those were mode/weapon mismatches, not a race. Asking for a mode the
    /// weapon cannot produce is a request the client can only refuse, and SetCombatMode has
    /// no failure channel to say so.
    ///
    /// THE LADDER:
    ///   0. Inspect: read Actions.CombatMode and the wielded weapon.
    ///      - already in a combat mode that satisfies the goal -> done, no input at all.
    ///      - in a DIFFERENT combat mode than a specific request -> FAIL FAST. Getting
    ///        there means changing weapon, which is the caller's job.
    ///      - request inconsistent with the wielded weapon -> FAIL FAST with what to wield.
    ///   1. Optional SetCombatMode, ONLY when the request is consistent with the weapon,
    ///      then verify. Skipped entirely when the weapon class cannot be determined.
    ///   2. Post Backtick (the native toggle), verify. Up to 3 toggle attempts.
    /// Every verify logs the observed mode AND the wielded weapon, so a future mismatch is
    /// self-evident in the log.
    ///
    /// THREADING: EnsureCombat/EnsureCombatMode may be called from any thread. All Decal
    /// access happens inside the RenderFrame handler on the game thread. The callback is
    /// invoked exactly once and is exception guarded.
    /// </summary>
    class CombatModeSetter
    {
        private const int MAX_TOGGLE_ATTEMPTS = 3;
        private const int VERIFY_DELAY_MS = 500;
        // A ladder only advances on render frames. If the client stops rendering the ladder
        // can never finish, and without this every later request would take the busy branch
        // forever and no combat mode change would ever happen again.
        private const int STALE_LADDER_SECONDS = 5;

        private enum Phase
        {
            Idle = 0,
            Inspect = 1,
            SetMode = 2,
            VerifySet = 3,
            Toggle = 4,
            VerifyToggle = 5
        }

        private object _locker = new object();
        private bool _subscribed;
        private Phase _phase = Phase.Idle;
        private CombatState _desired;
        private bool _anyCombat;
        private CombatModeCallback _callback;
        private CombatModeResult _result;
        private DateTime _verifyAtUtc = DateTime.MaxValue;
        private DateTime _ladderStartedUtc = DateTime.MinValue;
        private int _toggleAttempts;
        private bool _triedSetCombatMode;

        /// <summary>
        /// Ensure the client is in SOME combat mode. This is what most callers want: the
        /// weapon decides which mode, so demanding a specific one is usually overreach.
        /// </summary>
        public void EnsureCombat(CombatModeCallback callback)
        {
            Start(CombatState.Peace, true, callback);
        }

        /// <summary>
        /// Ensure a SPECIFIC combat mode. Fails fast when the wielded weapon cannot produce
        /// it, rather than retrying against a client that will only refuse.
        /// </summary>
        public void EnsureCombatMode(CombatState desired, CombatModeCallback callback)
        {
            Start(desired, false, callback);
        }

        private void Start(CombatState desired, bool anyCombat, CombatModeCallback callback)
        {
            CombatModeResult immediateFailure = null;
            try
            {
                lock (_locker)
                {
                    if (_phase != Phase.Idle && !IsLadderStale())
                    {
                        immediateFailure = MakeFailure(desired, anyCombat,
                            "busy: another combat mode change is still settling");
                    }
                    else
                    {
                        if (_phase != Phase.Idle)
                        {
                            log.WriteError(
                                "combat mode: abandoning a stale ladder stuck in phase {0}; the client may have stopped rendering",
                                _phase);
                            ForceResetUnlocked();
                        }
                        _desired = desired;
                        _anyCombat = anyCombat;
                        _callback = callback;
                        _result = new CombatModeResult();
                        _result.Requested = desired;
                        _result.AnyCombatAccepted = anyCombat;
                        _result.Final = CombatState.Peace;
                        _toggleAttempts = 0;
                        _triedSetCombatMode = false;
                        _phase = Phase.Inspect;
                        _verifyAtUtc = DateTime.MinValue;
                        _ladderStartedUtc = DateTime.UtcNow;
                        try
                        {
                            Subscribe();
                        }
                        catch (Exception exc)
                        {
                            // Reset inside the lock: leaving _phase set would wedge the
                            // setter and every later call would take the busy branch.
                            _phase = Phase.Idle;
                            _result = null;
                            _callback = null;
                            immediateFailure = MakeFailure(desired, anyCombat,
                                "could not subscribe to RenderFrame: " + exc.Message);
                        }
                    }
                }
            }
            catch (Exception exc)
            {
                log.WriteError("CombatModeSetter.Start exception: {0}", exc);
                immediateFailure = MakeFailure(desired, anyCombat,
                    "exception scheduling combat mode change: " + exc.Message);
            }
            if (immediateFailure != null) { InvokeCallback(callback, immediateFailure); }
        }

        private static CombatModeResult MakeFailure(CombatState desired, bool anyCombat, string detail)
        {
            CombatModeResult r = new CombatModeResult();
            r.Requested = desired;
            r.AnyCombatAccepted = anyCombat;
            r.Final = CombatState.Peace;
            r.Verified = false;
            r.Detail = detail;
            return r;
        }

        // Caller must hold _locker.
        private bool IsLadderStale()
        {
            if (_ladderStartedUtc == DateTime.MinValue) { return true; }
            return (DateTime.UtcNow - _ladderStartedUtc).TotalSeconds > STALE_LADDER_SECONDS;
        }

        // Caller must hold _locker.
        private void ForceResetUnlocked()
        {
            CombatModeCallback stranded = _callback;
            CombatModeResult strandedResult = _result;
            _phase = Phase.Idle;
            _callback = null;
            _result = null;
            try { Unsubscribe(); } catch (Exception) { }
            if (stranded != null && strandedResult != null)
            {
                strandedResult.Verified = false;
                strandedResult.Detail = "abandoned: ladder stalled, client may have stopped rendering";
                InvokeCallback(stranded, strandedResult);
            }
        }

        private void Subscribe()
        {
            if (!_subscribed)
            {
                _subscribed = true;
                CoreManager.Current.RenderFrame += new EventHandler<EventArgs>(Current_RenderFrame);
            }
        }

        private void Unsubscribe()
        {
            if (_subscribed)
            {
                CoreManager.Current.RenderFrame -= new EventHandler<EventArgs>(Current_RenderFrame);
                _subscribed = false;
            }
        }

        void Current_RenderFrame(object sender, EventArgs e)
        {
            CombatModeCallback callback = null;
            CombatModeResult finished = null;
            try
            {
                lock (_locker)
                {
                    switch (_phase)
                    {
                        case Phase.Idle: Unsubscribe(); break;
                        case Phase.Inspect: DoInspect(); break;
                        case Phase.SetMode: DoSetMode(); break;
                        case Phase.VerifySet:
                            if (DateTime.UtcNow < _verifyAtUtc) { break; }
                            DoVerifySet();
                            break;
                        case Phase.Toggle: DoToggle(); break;
                        case Phase.VerifyToggle:
                            if (DateTime.UtcNow < _verifyAtUtc) { break; }
                            DoVerifyToggle();
                            break;
                    }
                    if (_phase == Phase.Idle && _result != null)
                    {
                        finished = _result;
                        callback = _callback;
                        _result = null;
                        _callback = null;
                        Unsubscribe();
                    }
                }
            }
            catch (Exception exc)
            {
                log.WriteError("CombatModeSetter.Current_RenderFrame exception: {0}", exc);
                lock (_locker)
                {
                    finished = _result;
                    callback = _callback;
                    _result = null;
                    _callback = null;
                    _phase = Phase.Idle;
                    try { Unsubscribe(); } catch (Exception) { }
                }
                if (finished != null) { finished.Detail = "exception during combat mode ladder: " + exc.Message; }
            }
            if (finished != null) { InvokeCallback(callback, finished); }
        }

        // ---------- ladder, all on the game thread with _locker held ----------

        private void DoInspect()
        {
            CombatState observed = ReadCombatMode();
            WieldedWeaponInfo weapon = ReadWeapon();
            _result.Weapon = weapon.Describe();
            _result.Observed.Add(observed.ToString());
            log.WriteInfo(
                "combat mode: inspect - observed {0}, wielding {1}, goal {2}",
                observed, _result.Weapon, GoalText());

            if (observed != CombatState.Peace)
            {
                if (_anyCombat || observed == _desired)
                {
                    // Already there. No input at all: the cheapest and safest outcome.
                    Succeed(observed, "already in combat mode");
                    return;
                }
                // In a combat mode, but not the requested one. Backtick would only drop us
                // to Peace, and the mode is a function of the weapon, so this is the
                // caller's problem to fix by changing weapon.
                _result.ImpossibleRequest = true;
                Fail(observed, string.Format(
                    "already in {0} but {1} was requested; combat mode follows the wielded weapon ({2}), so change weapon rather than mode",
                    observed, _desired, _result.Weapon));
                return;
            }

            // In Peace. If a specific mode was asked for, refuse now when the weapon cannot
            // produce it, rather than toggling into a mode nobody wanted.
            if (!_anyCombat)
            {
                string reason;
                if (!WieldedWeapon.CanProduce(_desired, weapon, out reason))
                {
                    _result.ImpossibleRequest = true;
                    Fail(observed, reason);
                    return;
                }
            }

            // The optional SetCombatMode rung is only worth trying when we know the weapon
            // agrees with the request. When the weapon class is unknown, skip it and do
            // what a player does: press backtick.
            if (!_anyCombat && weapon.Read && weapon.ImpliedKnown && weapon.Implied == _desired)
            {
                _phase = Phase.SetMode;
                return;
            }
            _phase = Phase.Toggle;
        }

        private void DoSetMode()
        {
            _triedSetCombatMode = true;
            _result.UsedSetCombatMode = true;
            try
            {
                CoreManager.Current.Actions.SetCombatMode(_desired);
                log.WriteInfo("combat mode: SetCombatMode({0}) (weapon agrees)", _desired);
            }
            catch (Exception exc)
            {
                log.WriteError("combat mode: SetCombatMode({0}) threw: {1}", _desired, exc);
            }
            _verifyAtUtc = DateTime.UtcNow.AddMilliseconds(VERIFY_DELAY_MS);
            _phase = Phase.VerifySet;
        }

        private void DoVerifySet()
        {
            CombatState observed = ReadCombatMode();
            _result.Observed.Add(observed.ToString());
            log.WriteInfo(
                "combat mode: verify after SetCombatMode: observed {0}, goal {1}, wielding {2}",
                observed, GoalText(), _result.Weapon);
            if (IsGoalMet(observed))
            {
                Succeed(observed, "SetCombatMode verified");
                return;
            }
            log.WriteInfo("combat mode: SetCombatMode did not land, falling back to the Backtick toggle");
            _phase = Phase.Toggle;
        }

        private void DoToggle()
        {
            _toggleAttempts++;
            CombatState observed = ReadCombatMode();
            if (observed != CombatState.Peace)
            {
                // Something already put us in combat between frames. Toggling now would
                // drop us back to Peace, which is the opposite of the goal.
                if (IsGoalMet(observed))
                {
                    Succeed(observed, "entered combat before the toggle was needed");
                    return;
                }
                _result.ImpossibleRequest = true;
                Fail(observed, string.Format(
                    "in {0} but {1} was requested; combat mode follows the wielded weapon ({2})",
                    observed, _desired, _result.Weapon));
                return;
            }

            try
            {
                NamedKey key = NamedKeys.Find("Backtick");
                PostMessageTools.SendNamedKeyDown(key);
                PostMessageTools.SendNamedKeyUp(key);
                _result.UsedToggle = true;
                log.WriteInfo(
                    "combat mode: posted Backtick toggle, attempt {0} (from Peace, wielding {1})",
                    _toggleAttempts, _result.Weapon);
            }
            catch (Exception exc)
            {
                log.WriteError("combat mode: could not post Backtick toggle: {0}", exc);
            }
            _verifyAtUtc = DateTime.UtcNow.AddMilliseconds(VERIFY_DELAY_MS);
            _phase = Phase.VerifyToggle;
        }

        private void DoVerifyToggle()
        {
            CombatState observed = ReadCombatMode();
            _result.Observed.Add(observed.ToString());
            log.WriteInfo(
                "combat mode: verify after toggle {0}: observed {1}, goal {2}, wielding {3}",
                _toggleAttempts, observed, GoalText(), _result.Weapon);

            if (IsGoalMet(observed))
            {
                Succeed(observed, (_triedSetCombatMode
                    ? "recovered by Backtick toggle after SetCombatMode failed"
                    : "entered combat by Backtick toggle"));
                return;
            }

            if (observed != CombatState.Peace)
            {
                // We are in combat, just not the mode asked for. That is a weapon question,
                // and more toggling cannot fix it.
                _result.ImpossibleRequest = true;
                Fail(observed, string.Format(
                    "toggle entered {0} but {1} was requested; combat mode follows the wielded weapon ({2})",
                    observed, _desired, _result.Weapon));
                return;
            }

            if (_toggleAttempts < MAX_TOGGLE_ATTEMPTS)
            {
                _phase = Phase.Toggle;
                return;
            }
            Fail(observed, string.Format(
                "still in Peace after {0} Backtick attempts (wielding {1})",
                _toggleAttempts, _result.Weapon));
        }

        // ---------- helpers ----------

        private bool IsGoalMet(CombatState observed)
        {
            if (_anyCombat) { return observed != CombatState.Peace; }
            return observed == _desired;
        }

        private string GoalText()
        {
            return (_anyCombat ? "any combat mode" : _desired.ToString());
        }

        private void Succeed(CombatState observed, string detail)
        {
            _result.Final = observed;
            _result.Verified = true;
            _result.Retries = (_toggleAttempts > 0 ? _toggleAttempts - 1 : 0);
            _result.Detail = detail;
            _phase = Phase.Idle;
        }

        private void Fail(CombatState observed, string detail)
        {
            _result.Final = observed;
            _result.Verified = false;
            _result.Retries = (_toggleAttempts > 0 ? _toggleAttempts - 1 : 0);
            _result.Detail = detail;
            log.WriteError("combat mode: {0}", detail);
            _phase = Phase.Idle;
        }

        private CombatState ReadCombatMode()
        {
            try
            {
                return CoreManager.Current.Actions.CombatMode;
            }
            catch (Exception exc)
            {
                log.WriteError("combat mode: could not read Actions.CombatMode: {0}", exc);
                return CombatState.Peace;
            }
        }

        private WieldedWeaponInfo ReadWeapon()
        {
            try
            {
                int playerId = CoreManager.Current.CharacterFilter.Id;
                return WieldedWeapon.Read(playerId);
            }
            catch (Exception)
            {
                return new WieldedWeaponInfo();
            }
        }

        private static void InvokeCallback(CombatModeCallback callback, CombatModeResult result)
        {
            if (callback == null) { return; }
            try
            {
                callback(result);
            }
            catch (Exception exc)
            {
                log.WriteError("CombatModeSetter callback threw: {0}", exc);
            }
        }
    }
}
