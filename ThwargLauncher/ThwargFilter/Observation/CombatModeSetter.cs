using System;
using System.Collections.Generic;

using Decal.Adapter;
using Decal.Adapter.Wrappers;

using Filter.Shared;

namespace ThwargFilter
{
    /// <summary>
    /// What a combat mode change actually achieved, handed back to the caller once the
    /// change has been verified or has exhausted every rung of the ladder.
    /// </summary>
    class CombatModeResult
    {
        public CombatState Requested;
        /// <summary>The mode actually observed on the final verify.</summary>
        public CombatState Final;
        /// <summary>True when Final matched Requested.</summary>
        public bool Verified;
        /// <summary>Extra SetCombatMode calls beyond the first (0 when it worked first time).</summary>
        public int Retries;
        /// <summary>True when the Backtick toggle rung was used.</summary>
        public bool UsedToggle;
        /// <summary>Why it ended, for the log and for the chatlog record.</summary>
        public string Detail = "";
        /// <summary>Mode observed at each verify, oldest first. Distinguishes "never landed" from "landed then reverted".</summary>
        public List<string> Observed = new List<string>();
    }

    delegate void CombatModeCallback(CombatModeResult result);

    /// <summary>
    /// Self-healing combat mode setting.
    ///
    /// WHY: Actions.SetCombatMode has no failure channel, and live runs found it silently
    /// no-opping in 2 of 4 module self-tests: the filter logged "combat mode set to Melee"
    /// while Actions.CombatMode stayed Peace for a full 20 second window. An externally
    /// posted Backtick recovered it within a second both times. Actions.CombatMode is a
    /// same-process client-truth read, so the set CAN be verified even though it cannot
    /// report failure.
    ///
    /// LADDER, each rung verified about 500ms later on a render frame:
    ///   1. SetCombatMode, verify
    ///   2. SetCombatMode again, verify   (up to 2 retries total)
    ///   3. SetCombatMode again, verify
    ///   4. post the Backtick key, which toggles combat mode natively, verify once
    /// The toggle rung is GUARDED: Backtick toggles rather than selects, so it is only
    /// posted when a toggle actually moves toward the goal. It is never posted blind.
    ///
    /// Every verify logs the observed mode, which is what distinguishes "the set never
    /// landed" from "it landed and then reverted" - the open question in ledger L6-76.
    ///
    /// THREADING: EnsureCombatMode may be called from any thread. All Decal access happens
    /// inside the RenderFrame handler on the game thread. The callback is invoked exactly
    /// once, on the game thread, and is exception guarded.
    /// </summary>
    class CombatModeSetter
    {
        private const int MAX_SET_ATTEMPTS = 3;
        private const int VERIFY_DELAY_MS = 500;
        // A ladder only advances on render frames. If the client stops rendering the
        // ladder can never finish, and without this every later request would take the
        // busy branch forever and no combat mode change would ever be healed again.
        // A stale ladder is abandoned so the next request can take over.
        private const int STALE_LADDER_SECONDS = 5;

        private enum Phase
        {
            Idle = 0,
            Set = 1,
            VerifySet = 2,
            Toggle = 3,
            VerifyToggle = 4
        }

        private object _locker = new object();
        private bool _subscribed;
        private Phase _phase = Phase.Idle;
        private CombatState _desired;
        private CombatModeCallback _callback;
        private CombatModeResult _result;
        private DateTime _verifyAtUtc = DateTime.MaxValue;
        private DateTime _ladderStartedUtc = DateTime.MinValue;
        private int _setAttempts;

        /// <summary>
        /// Thread safe. Drive the client into the desired combat mode, verifying each step,
        /// and invoke callback exactly once when it settles. The callback always fires,
        /// including on failure, so a caller can rely on it to continue.
        /// </summary>
        public void EnsureCombatMode(CombatState desired, CombatModeCallback callback)
        {
            CombatModeResult immediateFailure = null;
            try
            {
                lock (_locker)
                {
                    if (_phase != Phase.Idle && !IsLadderStale())
                    {
                        // A previous request is still settling. Fail this one fast rather
                        // than interleaving two ladders against the same client state.
                        immediateFailure = new CombatModeResult();
                        immediateFailure.Requested = desired;
                        immediateFailure.Final = desired;
                        immediateFailure.Verified = false;
                        immediateFailure.Detail = "busy: another combat mode change is still settling";
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
                        _callback = callback;
                        _result = new CombatModeResult();
                        _result.Requested = desired;
                        _result.Final = desired;
                        _setAttempts = 0;
                        _phase = Phase.Set;
                        _verifyAtUtc = DateTime.MinValue;
                        _ladderStartedUtc = DateTime.UtcNow;
                        try
                        {
                            Subscribe();
                        }
                        catch (Exception exc)
                        {
                            // Must reset here, inside the lock. Leaving _phase at Set would
                            // wedge the setter permanently: every later call would take the
                            // busy branch and no ladder would ever run again.
                            _phase = Phase.Idle;
                            _result = null;
                            _callback = null;
                            immediateFailure = new CombatModeResult();
                            immediateFailure.Requested = desired;
                            immediateFailure.Final = desired;
                            immediateFailure.Verified = false;
                            immediateFailure.Detail = "could not subscribe to RenderFrame: " + exc.Message;
                        }
                    }
                }
            }
            catch (Exception exc)
            {
                log.WriteError("CombatModeSetter.EnsureCombatMode exception: {0}", exc);
                immediateFailure = new CombatModeResult();
                immediateFailure.Requested = desired;
                immediateFailure.Final = desired;
                immediateFailure.Verified = false;
                immediateFailure.Detail = "exception scheduling combat mode change: " + exc.Message;
            }
            if (immediateFailure != null)
            {
                InvokeCallback(callback, immediateFailure);
            }
        }

        /// <summary>
        /// True when a ladder has been running longer than any real ladder could take.
        /// Caller must hold _locker.
        /// </summary>
        private bool IsLadderStale()
        {
            if (_ladderStartedUtc == DateTime.MinValue) { return true; }
            return (DateTime.UtcNow - _ladderStartedUtc).TotalSeconds > STALE_LADDER_SECONDS;
        }

        /// <summary>
        /// Drop a stuck ladder without invoking its callback, which by definition is never
        /// going to be satisfied. Caller must hold _locker.
        /// </summary>
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
                // The original caller is still waiting on this. Tell it the truth rather
                // than leaving it hanging forever.
                strandedResult.Verified = false;
                strandedResult.Detail = "abandoned: ladder stalled, client may have stopped rendering";
                InvokeCallback(stranded, strandedResult);
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
                        case Phase.Idle:
                            Unsubscribe();
                            break;

                        case Phase.Set:
                            DoSet();
                            break;

                        case Phase.VerifySet:
                            if (DateTime.UtcNow < _verifyAtUtc) { break; }
                            DoVerifySet();
                            break;

                        case Phase.Toggle:
                            DoToggle();
                            break;

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

        // All Do* methods run on the game thread with _locker held.

        private void DoSet()
        {
            _setAttempts++;
            try
            {
                CoreManager.Current.Actions.SetCombatMode(_desired);
                log.WriteInfo("combat mode: SetCombatMode({0}) attempt {1}", _desired, _setAttempts);
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
                "combat mode: verify after attempt {0}: observed {1}, wanted {2}",
                _setAttempts, observed, _desired);

            if (observed == _desired)
            {
                _result.Final = observed;
                _result.Verified = true;
                _result.Retries = _setAttempts - 1;
                _result.Detail = (_setAttempts == 1
                    ? "set verified first attempt"
                    : "set verified after retry");
                _phase = Phase.Idle;
                return;
            }

            if (_setAttempts < MAX_SET_ATTEMPTS)
            {
                log.WriteInfo("combat mode: mismatch, retrying SetCombatMode");
                _phase = Phase.Set;
                return;
            }

            log.WriteInfo("combat mode: {0} set attempts did not land, escalating to the toggle rung", _setAttempts);
            _phase = Phase.Toggle;
        }

        private void DoToggle()
        {
            _result.Retries = _setAttempts - 1;
            CombatState observed = ReadCombatMode();

            if (!ShouldPostToggle(observed, _desired))
            {
                // Backtick TOGGLES; it cannot select between combat modes. Posting it here
                // would drive the client into a mode nobody asked for, so refuse.
                _result.Final = observed;
                _result.Verified = false;
                _result.Detail = string.Format(
                    "toggle refused: observed {0}, wanted {1}; Backtick toggles and would not reach the goal",
                    observed, _desired);
                log.WriteError("combat mode: {0}", _result.Detail);
                _phase = Phase.Idle;
                return;
            }

            try
            {
                NamedKey key = NamedKeys.Find("Backtick");
                PostMessageTools.SendNamedKeyDown(key);
                PostMessageTools.SendNamedKeyUp(key);
                _result.UsedToggle = true;
                log.WriteInfo("combat mode: posted Backtick toggle (observed {0}, wanted {1})", observed, _desired);
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
            _result.Final = observed;
            _result.Verified = (observed == _desired);
            _result.Detail = (_result.Verified
                ? "recovered by Backtick toggle"
                : "toggle did not reach the requested mode");
            log.WriteInfo(
                "combat mode: verify after toggle: observed {0}, wanted {1} -> {2}",
                observed, _desired, (_result.Verified ? "recovered" : "still wrong"));
            _phase = Phase.Idle;
        }

        /// <summary>
        /// Backtick toggles between Peace and combat. It only helps when the toggle moves
        /// toward the goal:
        ///   Peace now, a combat mode wanted   -> toggle enters combat
        ///   a combat mode now, Peace wanted   -> toggle leaves combat
        /// It CANNOT switch between Melee, Missile and Magic, so when the client is in a
        /// different combat mode than the one wanted, toggling would only reach Peace.
        /// Exposed for testing.
        /// </summary>
        public static bool ShouldPostToggle(CombatState observed, CombatState desired)
        {
            if (observed == desired) { return false; }
            if (desired == CombatState.Peace) { return observed != CombatState.Peace; }
            return observed == CombatState.Peace;
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
                // Peace is the safe stand-in: it is never a combat mode we would have
                // asked for, so an unreadable mode is treated as "not there yet".
                return CombatState.Peace;
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
