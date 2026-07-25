using System;
using System.Collections.Generic;

using Decal.Adapter;
using Decal.Adapter.Wrappers;

namespace ThwargFilter
{
    /// <summary>
    /// Implements the "appraise" verb: ask the server to identify (appraise) a target, so
    /// that ACE admin commands which act on the LAST-APPRAISED object have a known target.
    ///
    /// WHY THIS EXISTS: selecting an object is not the same thing as appraising it.
    /// Decal's Actions.SelectItem / CurrentSelection only change the client's selection;
    /// only Actions.RequestId sends the identify request that makes the server record a new
    /// appraisal target. Before this verb there was no chat-command path to appraise a
    /// creature or a player, so admin commands like /remove-vitae and the creature branch
    /// of /heal had no reliable way to be aimed.
    ///
    /// THREADING: RequestAppraise may be called from any thread. Launcher channel commands
    /// arrive on the heartbeat timer thread or on a FileSystemWatcher thread, and
    /// CoreManager.Current.Actions / WorldFilter / CharacterFilter must only be touched on
    /// the game thread. So RequestAppraise only queues the target and subscribes to
    /// RenderFrame; the work happens inside the RenderFrame handler, which processes one
    /// request per frame and unsubscribes itself once the queue is empty. This is the same
    /// one-shot pattern as GameStateDumper.
    ///
    /// Requests are queued rather than collapsed because each RequestId moves the server's
    /// appraisal target: a harness that appraises A, reads, then appraises B must get both
    /// in that order.
    /// </summary>
    class Appraiser
    {
        /// <summary>Bare "appraise", or "appraise self", targets the logged in character.</summary>
        public const string TARGET_Self = "self";

        // Outcomes emitted into chatlog_<pid>.jsonl so a harness can await the result
        // without parsing the filter log.
        private const string OUTCOME_Requested = "requested";
        private const string OUTCOME_Ambiguous = "ambiguous";
        private const string OUTCOME_NotFound = "notfound";
        // Keep candidate listings bounded: this goes to the log on every ambiguous match.
        private const int MAX_LOGGED_CANDIDATES = 20;

        private object _locker = new object();
        private Queue<string> _pendingTargets = new Queue<string>();
        private bool _subscribed;

        /// <summary>
        /// Thread safe. Queues an appraisal to run on the next rendered frame.
        /// Pass null or "self" for the logged in character, otherwise a case insensitive
        /// substring of the target's name.
        /// </summary>
        public void RequestAppraise(string target)
        {
            try
            {
                if (target == null) { target = ""; }
                // Logged before the marshalling attempt so the request is recorded even if
                // subscribing fails, which also makes command routing visible in the log.
                log.WriteInfo("Appraiser: appraise requested for '{0}'", target);
                lock (_locker)
                {
                    _pendingTargets.Enqueue(target);
                    if (!_subscribed)
                    {
                        _subscribed = true;
                        CoreManager.Current.RenderFrame += new EventHandler<EventArgs>(Current_RenderFrame);
                    }
                }
            }
            catch (Exception exc)
            {
                log.WriteError("Appraiser.RequestAppraise exception: {0}", exc);
            }
        }

        void Current_RenderFrame(object sender, EventArgs e)
        {
            try
            {
                string target = null;
                bool haveTarget = false;
                lock (_locker)
                {
                    if (_pendingTargets.Count > 0)
                    {
                        target = _pendingTargets.Dequeue();
                        haveTarget = true;
                    }
                    // Unsubscribe as soon as nothing is left, so we are not billed a
                    // delegate call on every frame for the rest of the session.
                    if (_pendingTargets.Count == 0 && _subscribed)
                    {
                        CoreManager.Current.RenderFrame -= new EventHandler<EventArgs>(Current_RenderFrame);
                        _subscribed = false;
                    }
                }
                if (haveTarget)
                {
                    DoAppraise(target);
                }
            }
            catch (Exception exc)
            {
                log.WriteError("Appraiser.Current_RenderFrame exception: {0}", exc);
            }
        }

        /// <summary>Runs on the game thread.</summary>
        private void DoAppraise(string target)
        {
            try
            {
                string trimmed = (target == null ? "" : target.Trim());
                if (trimmed.Length == 0
                    || string.Compare(trimmed, TARGET_Self, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    AppraiseSelf();
                }
                else
                {
                    AppraiseByName(trimmed);
                }
            }
            catch (Exception exc)
            {
                log.WriteError("Appraiser.DoAppraise exception: {0}", exc);
            }
        }

        private void AppraiseSelf()
        {
            int characterId = 0;
            string characterName = null;
            try
            {
                characterId = CoreManager.Current.CharacterFilter.Id;
                characterName = CoreManager.Current.CharacterFilter.Name;
            }
            catch (Exception exc)
            {
                log.WriteError("appraise self: cannot read character id: {0}", exc);
                WriteResult(TARGET_Self, OUTCOME_NotFound, 0, null, 0);
                return;
            }
            if (characterId == 0)
            {
                log.WriteInfo("appraise self: no character id yet; not logged in");
                WriteResult(TARGET_Self, OUTCOME_NotFound, 0, null, 0);
                return;
            }
            if (SendRequestId(characterId))
            {
                log.WriteInfo("appraise self: requested id for character {0}", characterId);
                WriteResult(TARGET_Self, OUTCOME_Requested, characterId, characterName, 1);
            }
            else
            {
                WriteResult(TARGET_Self, OUTCOME_NotFound, 0, null, 0);
            }
        }

        private void AppraiseByName(string target)
        {
            List<TargetCandidate> candidates = TargetResolver.Collect(target);
            if (candidates == null)
            {
                log.WriteError("appraise '{0}': could not read the world object list", target);
                WriteResult(target, OUTCOME_NotFound, 0, null, 0);
                return;
            }
            if (candidates.Count == 1)
            {
                TargetCandidate match = candidates[0];
                if (SendRequestId(match.Id))
                {
                    log.WriteInfo("appraise '{0}': matched {1}", target, match.Describe());
                    WriteResult(target, OUTCOME_Requested, match.Id, match.Name, 1);
                }
                else
                {
                    WriteResult(target, OUTCOME_NotFound, 0, null, 1);
                }
                return;
            }
            // Zero or ambiguous: report and do NOT guess. Picking one arbitrarily would
            // silently aim an admin command at the wrong object.
            if (candidates.Count == 0)
            {
                log.WriteInfo("appraise '{0}': no match; nothing appraised", target);
                WriteResult(target, OUTCOME_NotFound, 0, null, 0);
            }
            else
            {
                log.WriteInfo(
                    "appraise '{0}': ambiguous, {1} matches; nothing appraised. Narrow the substring.",
                    target,
                    candidates.Count);
                WriteResult(target, OUTCOME_Ambiguous, 0, null, candidates.Count);
            }
            TargetResolver.LogCandidates("appraise", candidates, MAX_LOGGED_CANDIDATES);
        }


        /// <summary>
        /// Emit one machine-readable record into chatlog_[pid].jsonl so a harness can await
        /// the outcome of an appraise without tailing the filter log. The human readable
        /// log lines are kept as well; this is in addition to them, not instead.
        ///
        /// Deliberately NOT routed through LoginStageTracker.StatusNote: that field is
        /// documented as login stall diagnosis and self clears on stage change, so
        /// overloading it would corrupt the login decision table.
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
                entry["type"] = "AppraiseResult";
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
                // Reporting must never break the verb it is reporting on.
                log.WriteError("Appraiser.WriteResult exception: {0}", exc);
            }
        }

        private bool SendRequestId(int objectId)
        {
            try
            {
                CoreManager.Current.Actions.RequestId(objectId);
                return true;
            }
            catch (Exception exc)
            {
                log.WriteError("appraise: RequestId({0}) failed: {1}", objectId, exc);
                return false;
            }
        }
    }
}
