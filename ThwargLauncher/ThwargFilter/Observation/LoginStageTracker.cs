using System;

namespace ThwargFilter
{
    /// <summary>
    /// Tracks how far through connect and login this client has got, so an external test
    /// harness can tell a connection stall apart from a character select problem instead
    /// of inferring it from a timeout.
    ///
    /// The values are published on every heartbeat (see LaunchControl and
    /// HeartbeatGameStatus). The login flow managers set the stage from their existing
    /// message handlers; nothing here drives the login itself.
    ///
    /// Every method is exception guarded and static, and is called from the game dispatch
    /// thread, the login timers and the heartbeat timer, so all access is locked.
    /// Telemetry must never be able to break the filter.
    /// </summary>
    class LoginStageTracker
    {
        /// <summary>Process is up and the filter is loaded, but no character list yet.</summary>
        public const string STAGE_Starting = "Starting";
        /// <summary>The server sent a character list; we are at character select.</summary>
        public const string STAGE_CharSelect = "CharSelect";
        /// <summary>A character has materialized in the world.</summary>
        public const string STAGE_InWorld = "InWorld";

        // Keep notes short and single line: the heartbeat file is one Key:Value per line,
        // so an embedded newline would corrupt the record for every parser downstream.
        private const int MAX_NOTE_LENGTH = 300;

        private static object _locker = new object();
        private static string _stage = STAGE_Starting;
        private static DateTime _stageChangedUtc = DateTime.UtcNow;
        private static string _requestedCharacter = "";
        private static string _statusNote = "";

        /// <summary>
        /// Idempotent: setting the stage it is already in does not restart the clock, so
        /// SecondsInStage keeps rising while a client is wedged. Moving to a genuinely new
        /// stage clears any status note, which by then describes the previous stage.
        /// </summary>
        public static void SetStage(string stage)
        {
            try
            {
                lock (_locker)
                {
                    if (_stage == stage) { return; }
                    _stage = stage;
                    _stageChangedUtc = DateTime.UtcNow;
                    _statusNote = "";
                }
                log.WriteInfo("LoginStage -> {0}", stage);
            }
            catch (Exception exc)
            {
                SafeLogError(exc);
            }
        }

        public static string GetStage()
        {
            lock (_locker) { return _stage; }
        }

        public static int GetSecondsInStage()
        {
            try
            {
                lock (_locker)
                {
                    return (int)(DateTime.UtcNow - _stageChangedUtc).TotalSeconds;
                }
            }
            catch (Exception exc)
            {
                SafeLogError(exc);
                return 0;
            }
        }

        /// <summary>
        /// The character name the launcher asked for, so a mismatch against what the
        /// server actually offered is visible without cross referencing the launch file.
        /// </summary>
        public static void SetRequestedCharacter(string characterName)
        {
            try
            {
                lock (_locker) { _requestedCharacter = Sanitize(characterName); }
            }
            catch (Exception exc)
            {
                SafeLogError(exc);
            }
        }

        public static string GetRequestedCharacter()
        {
            lock (_locker) { return _requestedCharacter; }
        }

        /// <summary>
        /// Free text describing why this client is stuck where it is.
        /// </summary>
        public static void SetStatusNote(string note)
        {
            try
            {
                lock (_locker) { _statusNote = Sanitize(note); }
            }
            catch (Exception exc)
            {
                SafeLogError(exc);
            }
        }

        public static string GetStatusNote()
        {
            lock (_locker) { return _statusNote; }
        }

        private static string Sanitize(string text)
        {
            if (text == null) { return ""; }
            text = text.Replace("\r", " ");
            text = text.Replace("\n", " ");
            if (text.Length > MAX_NOTE_LENGTH)
            {
                text = text.Substring(0, MAX_NOTE_LENGTH);
            }
            return text;
        }

        private static void SafeLogError(Exception exc)
        {
            try
            {
                log.WriteError("LoginStageTracker exception: {0}", exc);
            }
            catch (Exception)
            {
                // Telemetry must never throw into the filter.
            }
        }
    }
}
