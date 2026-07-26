using System;
using System.Collections.Generic;

using Decal.Adapter;

using Filter.Shared;

namespace ThwargFilter
{
    /// <summary>
    /// Implements the "confirm yes|no" verb, and the observation of server confirmation
    /// dialogs that makes it usable from a test harness.
    ///
    /// WHY THIS EXISTS: an ACE server asks the player a yes/no question by sending
    /// GameEventConfirmationRequest (game event 0x0274 inside the 0xF7B0 game event
    /// container). The ONLY thing that answers it is GameActionConfirmationResponse,
    /// game action 0x0275, which only the client can send
    /// (ACE.Server\Network\GameAction\Actions\GameActionConfirmationResponse.cs, and
    /// ConfirmationManager.HandleResponse has no other non-timeout caller). Until this
    /// verb existed, an automated test could drive a server action that asks a question
    /// and then had no way at all to answer it, and no way to even SEE that a question
    /// was outstanding except by reading the server's own log.
    ///
    /// HOW THE ANSWER IS SENT, AND WHY IT IS A MOUSE CLICK.
    /// Decal cannot send a raw or arbitrary network message. Verified by reflection over
    /// the entire Decal surface at Decal 3.0 / Decal.Adapter 2.9.8.3:
    ///   - Decal.Adapter.Wrappers.HooksWrapper (CoreManager.Current.Actions) is a FIXED
    ///     list of client-function hooks - UseItem, RequestId, CastSpell, TradeAccept and
    ///     so on. There is no Send, no SendMessage, and nothing confirmation related.
    ///   - Decal.Interop.Net.INetworkFilter2 exposes only DispatchServer / DispatchClient /
    ///     Initialize / Terminate. Those are INBOUND observation callbacks; there is no
    ///     outbound injection point.
    ///   - Decal.Interop.Net.INetService exposes only Decal / Filter / FilterVB / Hooks.
    ///   - Decal.Interop.Net.IMessageFactory.CreateMessage builds a message object for
    ///     PARSING an observed buffer; it does not put anything on the wire.
    ///   - "C:\Program Files (x86)\Decal 3.0\messages.xml" defines exactly three outbound
    ///     messages (F657, F7B1, F7DE), and the F7B1 switch does NOT include action
    ///     0x0275, so Decal has no schema for the response either.
    /// So the response has to come from the client itself, by operating the client's own
    /// confirmation panel. This filter already does exactly that for the client's other
    /// yes/no boxes: FastQuit answers the quit box with PostMessageTools.ClickYes, and
    /// AutoRetryLogin dismisses login boxes with PostMessageTools.ClickOK. Those helpers
    /// post WM_LBUTTONDOWN/UP at a position derived from the client window rect, with the
    /// vertical offsets that were tuned against real clients (see the comments in
    /// Shared\PostMessageTools.cs). ClickNo is added here as the mirror of ClickYes, at
    /// the +80 x-offset that the sibling KeyTestApp\PostMsgs.cs already uses.
    ///
    /// WHAT IS NOT VERIFIED: that the SERVER-DRIVEN confirmation panel (event 0x0274) is
    /// drawn at the same screen position as the client-local quit/login boxes those
    /// offsets were tuned for. That is a hypothesis, not a measured fact, and it can only
    /// be settled with a live client. Two things exist specifically so it can be settled
    /// and worked around without a rebuild:
    ///   - "confirm yes at:X,Y" clicks one explicit client-relative point instead.
    ///   - "confirm yes force" clicks even when no request is outstanding, which separates
    ///     "detection is broken" from "the click misses the button".
    ///
    /// HOW A TEST KNOWS WHETHER THE ANSWER LANDED. ACE does NOT send ConfirmationDone
    /// after a normal response - GameEventConfirmationDone is only ever sent from
    /// ConfirmationManager.EnqueueAbort, i.e. on the 30 second timeout. So:
    ///   answered, then silence, then the effect happens  -> the click landed.
    ///   answered, then ConfirmationDone arrives about 30s later
    ///   (state goes to "aborted", usually with "You waited too long to answer the
    ///   question!")                                      -> the click did NOT land.
    ///
    /// THREADING: RequestConfirm may be called from any thread; launcher channel commands
    /// arrive on the heartbeat timer thread or a FileSystemWatcher thread. The click work
    /// is marshalled onto the game thread with the same one-shot RenderFrame pattern used
    /// by Appraiser and SpellBar. The observation entry point runs on the dispatch thread
    /// and never throws.
    /// </summary>
    class Confirmer
    {
        // Decal delivers the game event container; the event id selects the payload.
        // Opcode verified against ACE GameMessageOpcode.GameEvent and against Decal's
        // messages.xml, which parses F7B0 as character / sequence / event + a switch.
        private const int OPCODE_GameEvent = 0xF7B0;

        // ACE GameEventType.cs:72-73. Decal's messages.xml parses both:
        //   case 0x0274 -> type (DWORD), number (DWORD), text (String)
        //   case 0x0276 -> unknown (DWORD, the confirmation type), number (DWORD)
        // ACE writes confirmationType then context in both, so "number" is the context id.
        private const int EVENT_ConfirmationRequest = 0x0274;
        private const int EVENT_ConfirmationDone = 0x0276;

        // ACE ConfirmationManager.cs:20 - confirmationTimeout = 30 seconds. Tracked here
        // only so a request that was never answered and never aborted does not sit in the
        // heartbeat forever claiming to be outstanding.
        private const int CONFIRMATION_TIMEOUT_SECONDS = 30;

        // Heartbeat / gamestate values for ConfirmationState.
        public const string STATE_None = "none";
        public const string STATE_Outstanding = "outstanding";
        public const string STATE_Answered = "answered";
        public const string STATE_Aborted = "aborted";
        public const string STATE_Expired = "expired";

        public const string ANSWER_Yes = "yes";
        public const string ANSWER_No = "no";

        // Chat log records use this source so a harness can select confirmation traffic
        // without also matching the "network" and "chatbox" streams ChatObserver writes.
        private const string SOURCE_Confirmation = "confirmation";

        // Confirmation text is echoed into the line-oriented heartbeat file, which is
        // parsed one "Key:Value" line at a time, so it must stay on one line and stay
        // small. The full untruncated text is always in the chat log record.
        private const int MAX_HEARTBEAT_TEXT = 200;

        private static object _stateLocker = new object();
        private static string _state = STATE_None;
        private static long _type;
        private static long _context;
        private static string _text;
        private static string _answer;
        private static DateTime _requestedUtc = DateTime.MinValue;

        private object _locker = new object();
        private Queue<PendingAnswer> _pending = new Queue<PendingAnswer>();
        private bool _subscribed;

        private class PendingAnswer
        {
            public bool Yes;
            public bool Force;
            public bool HasPoint;
            public int X;
            public int Y;
        }

        // ---------- observation, on the dispatch thread ----------

        /// <summary>
        /// Called from FilterCore_ServerDispatch. Cheap: anything that is not the game
        /// event container returns immediately. Never throws.
        /// </summary>
        public void FilterCore_ServerDispatch(object sender, NetworkMessageEventArgs e)
        {
            try
            {
                if (e == null || e.Message == null) { return; }
                if (e.Message.Type != OPCODE_GameEvent) { return; }
                long eventId = 0;
                if (!TryGetLongField(e.Message, "event", out eventId)) { return; }
                if (eventId == EVENT_ConfirmationRequest)
                {
                    RecordRequest(e.Message);
                }
                else if (eventId == EVENT_ConfirmationDone)
                {
                    RecordDone(e.Message);
                }
            }
            catch (Exception exc)
            {
                log.WriteError("Confirmer.FilterCore_ServerDispatch exception: {0}", exc);
            }
        }

        private void RecordRequest(Message msg)
        {
            long type = 0;
            long context = 0;
            TryGetLongField(msg, "type", out type);
            TryGetLongField(msg, "number", out context);
            string text = GetStringField(msg, "text");

            lock (_stateLocker)
            {
                _state = STATE_Outstanding;
                _type = type;
                _context = context;
                _text = text;
                _answer = null;
                _requestedUtc = DateTime.UtcNow;
            }
            log.WriteInfo(
                "confirm: server asked a question, type {0} context {1}: {2}",
                type, context, text);

            Dictionary<string, object> entry = NewEntry("ConfirmationRequest");
            entry["confirmationType"] = type;
            entry["context"] = context;
            entry["text"] = text;
            entry["state"] = STATE_Outstanding;
            ChatLogWriter.WriteEntry(entry);
        }

        /// <summary>
        /// ConfirmationDone means the SERVER closed the dialog, which in ACE only happens
        /// on the 30 second abort path. Seeing this after answering is direct evidence
        /// that the answer never reached the server.
        /// </summary>
        private void RecordDone(Message msg)
        {
            long type = 0;
            long context = 0;
            // Decal names the first DWORD "unknown"; ACE writes confirmationType there
            // (GameEventConfirmationDone.cs:10-11).
            TryGetLongField(msg, "unknown", out type);
            TryGetLongField(msg, "number", out context);

            string priorAnswer = null;
            lock (_stateLocker)
            {
                priorAnswer = _answer;
                if (_state == STATE_Outstanding || _state == STATE_Answered)
                {
                    _state = STATE_Aborted;
                }
                _type = type;
                _context = context;
            }
            log.WriteError(
                "confirm: server ABORTED confirmation type {0} context {1} (previous answer '{2}'); "
                + "if this follows a confirm verb, the answer did not reach the server",
                type, context, (priorAnswer == null ? "" : priorAnswer));

            Dictionary<string, object> entry = NewEntry("ConfirmationDone");
            entry["confirmationType"] = type;
            entry["context"] = context;
            entry["priorAnswer"] = priorAnswer;
            entry["state"] = STATE_Aborted;
            ChatLogWriter.WriteEntry(entry);
        }

        // ---------- the verb, callable from any thread ----------

        /// <summary>
        /// Thread safe. Queues an answer to run on the next rendered frame.
        /// Argument forms: "yes" | "no" | "y" | "n", plus optional "force" and
        /// "at:X,Y" (client-window-relative pixels).
        /// </summary>
        public void RequestConfirm(string args)
        {
            try
            {
                string arg = (args == null ? "" : args.Trim());
                string[] parts = arg.Split(new char[0], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                {
                    log.WriteError("confirm: expected 'confirm yes' or 'confirm no'");
                    WriteAnswerResult(null, "badargs", 0, 0, null, 0, 0);
                    return;
                }
                PendingAnswer op = new PendingAnswer();
                string which = parts[0];
                if (string.Compare(which, "yes", StringComparison.OrdinalIgnoreCase) == 0
                    || string.Compare(which, "y", StringComparison.OrdinalIgnoreCase) == 0)
                {
                    op.Yes = true;
                }
                else if (string.Compare(which, "no", StringComparison.OrdinalIgnoreCase) == 0
                    || string.Compare(which, "n", StringComparison.OrdinalIgnoreCase) == 0)
                {
                    op.Yes = false;
                }
                else
                {
                    log.WriteError("confirm: unrecognized answer '{0}'; expected yes, no, y or n", which);
                    WriteAnswerResult(null, "badargs", 0, 0, null, 0, 0);
                    return;
                }
                for (int i = 1; i < parts.Length; i++)
                {
                    string token = parts[i];
                    if (string.Compare(token, "force", StringComparison.OrdinalIgnoreCase) == 0)
                    {
                        op.Force = true;
                        continue;
                    }
                    if (token.Length > 3
                        && string.Compare(token.Substring(0, 3), "at:", StringComparison.OrdinalIgnoreCase) == 0)
                    {
                        int x = 0;
                        int y = 0;
                        if (TryParsePoint(token.Substring(3), out x, out y))
                        {
                            op.HasPoint = true;
                            op.X = x;
                            op.Y = y;
                        }
                        else
                        {
                            log.WriteError("confirm: could not read '{0}'; expected at:X,Y", token);
                        }
                        continue;
                    }
                    log.WriteError("confirm: ignoring unrecognized token '{0}'", token);
                }

                log.WriteInfo(
                    "confirm: queued answer '{0}' (force={1}, explicitPoint={2})",
                    (op.Yes ? ANSWER_Yes : ANSWER_No), op.Force, op.HasPoint);

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
                log.WriteError("Confirmer.RequestConfirm exception: {0}", exc);
            }
        }

        void Current_RenderFrame(object sender, EventArgs e)
        {
            try
            {
                PendingAnswer op = null;
                lock (_locker)
                {
                    if (_pending.Count > 0) { op = _pending.Dequeue(); }
                    if (_pending.Count == 0 && _subscribed)
                    {
                        CoreManager.Current.RenderFrame -= new EventHandler<EventArgs>(Current_RenderFrame);
                        _subscribed = false;
                    }
                }
                if (op != null) { DoAnswer(op); }
            }
            catch (Exception exc)
            {
                log.WriteError("Confirmer.Current_RenderFrame exception: {0}", exc);
            }
        }

        /// <summary>Runs on the game thread.</summary>
        private void DoAnswer(PendingAnswer op)
        {
            string answer = (op.Yes ? ANSWER_Yes : ANSWER_No);
            ExpireIfStale();

            bool outstanding = false;
            long type = 0;
            long context = 0;
            string text = null;
            lock (_stateLocker)
            {
                outstanding = (_state == STATE_Outstanding);
                type = _type;
                context = _context;
                text = _text;
            }

            if (!outstanding && !op.Force)
            {
                // Do NOT click blind. A stray click in the 3D window selects or attacks
                // whatever is under it, which would silently corrupt the test that asked
                // for a confirmation. Use "confirm yes force" to click anyway.
                log.WriteInfo("confirm: no confirmation is outstanding; nothing clicked (add 'force' to click anyway)");
                WriteAnswerResult(answer, "nooutstanding", 0, 0, null, 0, 0);
                return;
            }

            int clickedX = 0;
            int clickedY = 0;
            bool clicked = false;
            try
            {
                if (op.HasPoint)
                {
                    clickedX = op.X;
                    clickedY = op.Y;
                    PostMessageTools.SendMouseClick(op.X, op.Y);
                }
                else if (op.Yes)
                {
                    PostMessageTools.ClickYes();
                }
                else
                {
                    PostMessageTools.ClickNo();
                }
                clicked = true;
            }
            catch (Exception exc)
            {
                log.WriteError("confirm: could not post the click: {0}", exc);
            }

            if (clicked && outstanding)
            {
                lock (_stateLocker)
                {
                    // Only demote a still-outstanding request: if the abort already
                    // arrived between the snapshot and here, leave it aborted.
                    if (_state == STATE_Outstanding) { _state = STATE_Answered; }
                    _answer = answer;
                }
            }
            else if (clicked)
            {
                lock (_stateLocker) { _answer = answer; }
            }

            log.WriteInfo(
                "confirm: answered '{0}' to confirmation type {1} context {2} (forced={3})",
                answer, type, context, op.Force);
            WriteAnswerResult(
                answer,
                (clicked ? "clicked" : "failed"),
                type,
                context,
                text,
                clickedX,
                clickedY);
        }

        // ---------- state exposed to the heartbeat and the gamestate dump ----------

        /// <summary>
        /// Snapshot for the heartbeat file. Never throws; returns STATE_None before any
        /// confirmation has been seen.
        /// </summary>
        public static void GetStatus(
            out string state,
            out int confirmationType,
            out int context,
            out string text,
            out string answer)
        {
            state = STATE_None;
            confirmationType = 0;
            context = 0;
            text = null;
            answer = null;
            try
            {
                ExpireIfStale();
                lock (_stateLocker)
                {
                    state = _state;
                    confirmationType = ClampToInt(_type);
                    context = ClampToInt(_context);
                    text = SingleLine(_text, MAX_HEARTBEAT_TEXT);
                    answer = _answer;
                }
            }
            catch (Exception exc)
            {
                log.WriteError("Confirmer.GetStatus exception: {0}", exc);
            }
        }

        /// <summary>
        /// Additive section for the "dumpstate" snapshot, so a harness that already polls
        /// gamestate_[pid].txt can see an outstanding question without also reading the
        /// heartbeat file. Full untruncated text here.
        /// </summary>
        public static void AddState(Dictionary<string, object> state, List<string> notes)
        {
            try
            {
                ExpireIfStale();
                Dictionary<string, object> confirmation = new Dictionary<string, object>();
                lock (_stateLocker)
                {
                    confirmation["state"] = _state;
                    confirmation["confirmationType"] = _type;
                    confirmation["context"] = _context;
                    confirmation["text"] = _text;
                    confirmation["answer"] = _answer;
                    confirmation["requestedUtc"] =
                        (_requestedUtc == DateTime.MinValue ? null : _requestedUtc.ToString("o"));
                }
                state["confirmation"] = confirmation;
            }
            catch (Exception exc)
            {
                if (notes != null) { notes.Add("confirmation section failed: " + exc.Message); }
            }
        }

        /// <summary>
        /// ACE aborts an unanswered confirmation after 30 seconds. Without this, a request
        /// that was never answered and whose abort we missed would report "outstanding"
        /// forever and a polling test would hang on it.
        /// </summary>
        private static void ExpireIfStale()
        {
            lock (_stateLocker)
            {
                if (_state != STATE_Outstanding) { return; }
                if (_requestedUtc == DateTime.MinValue) { return; }
                double age = (DateTime.UtcNow - _requestedUtc).TotalSeconds;
                if (age > CONFIRMATION_TIMEOUT_SECONDS) { _state = STATE_Expired; }
            }
        }

        // ---------- helpers ----------

        private static void WriteAnswerResult(
            string answer,
            string outcome,
            long type,
            long context,
            string text,
            int clickedX,
            int clickedY)
        {
            try
            {
                Dictionary<string, object> entry = NewEntry("ConfirmationAnswer");
                entry["answer"] = answer;
                entry["outcome"] = outcome;
                if (outcome == "clicked")
                {
                    // Emitted so a test can prove WHICH dialog it answered rather than
                    // assuming the only outstanding one was the one it meant.
                    entry["confirmationType"] = type;
                    entry["context"] = context;
                    entry["text"] = text;
                    if (clickedX != 0 || clickedY != 0)
                    {
                        entry["clickX"] = clickedX;
                        entry["clickY"] = clickedY;
                    }
                }
                ChatLogWriter.WriteEntry(entry);
            }
            catch (Exception exc)
            {
                log.WriteError("Confirmer.WriteAnswerResult exception: {0}", exc);
            }
        }

        private static Dictionary<string, object> NewEntry(string eventName)
        {
            Dictionary<string, object> entry = new Dictionary<string, object>();
            entry["utc"] = DateTime.UtcNow.ToString("o");
            entry["source"] = SOURCE_Confirmation;
            entry["type"] = eventName;
            return entry;
        }

        private static bool TryParsePoint(string text, out int x, out int y)
        {
            x = 0;
            y = 0;
            if (text == null) { return false; }
            string[] pieces = text.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (pieces.Length != 2) { return false; }
            if (!int.TryParse(pieces[0].Trim(), out x)) { return false; }
            if (!int.TryParse(pieces[1].Trim(), out y)) { return false; }
            return true;
        }

        /// <summary>
        /// Decal hands DWORD fields back as a signed Int32, so a context id above
        /// int.MaxValue would arrive negative. Read it as unsigned and widen, so the
        /// logged context always matches the uint ACE actually sent.
        /// </summary>
        private static bool TryGetLongField(Message msg, string fieldName, out long result)
        {
            result = 0;
            try
            {
                object value = msg[fieldName];
                if (value == null) { return false; }
                if (value is int)
                {
                    result = (long)unchecked((uint)(int)value);
                    return true;
                }
                result = Convert.ToInt64(value);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string GetStringField(Message msg, string fieldName)
        {
            try
            {
                object value = msg[fieldName];
                if (value == null) { return null; }
                string text = Convert.ToString(value);
                if (text == null) { return null; }
                return text.TrimEnd('\r', '\n');
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static int ClampToInt(long value)
        {
            if (value > int.MaxValue) { return int.MaxValue; }
            if (value < int.MinValue) { return int.MinValue; }
            return (int)value;
        }

        /// <summary>
        /// The heartbeat file is read back one "Key:Value" line at a time, so an embedded
        /// newline in the confirmation text would truncate the file for every later field.
        /// </summary>
        private static string SingleLine(string text, int maxLength)
        {
            if (text == null) { return null; }
            string flat = text.Replace("\r\n", " ").Replace("\r", " ").Replace("\n", " ");
            if (flat.Length > maxLength) { flat = flat.Substring(0, maxLength); }
            return flat;
        }
    }
}
