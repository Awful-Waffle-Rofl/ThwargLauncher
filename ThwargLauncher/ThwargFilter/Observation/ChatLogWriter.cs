using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using Newtonsoft.Json;

namespace ThwargFilter
{
    /// <summary>
    /// Appends one JSON object per line ("JSON Lines") to chatlog_[pid].jsonl in the
    /// launcher Running folder, so an external test harness can tail what the game said.
    /// This is the observation half of the launcher test channel.
    ///
    /// This class must never throw. It is called from the game's own dispatch and
    /// render threads, and a logging failure must not disturb the game.
    /// </summary>
    class ChatLogWriter
    {
        // Rotate at roughly 5 MB; the previous rotation is overwritten.
        private const long MAX_FILE_BYTES = 5 * 1024 * 1024;
        // Only complain to the log this many times, then stay quiet but keep trying.
        private const int MAX_LOGGED_ERRORS = 5;

        private static object _locker = new object();
        private static int _sequence;
        private static int _errorCount;
        // UTF8 without a byte order mark - a BOM in the middle of an appended
        // JSON Lines file breaks naive line-by-line parsers in the harness.
        private static Encoding _encoding = new UTF8Encoding(false);

        public static void WriteEntry(Dictionary<string, object> entry)
        {
            if (entry == null) { return; }
            try
            {
                lock (_locker)
                {
                    _sequence++;
                    entry["seq"] = _sequence;
                    string line = JsonConvert.SerializeObject(entry);
                    string filepath = FileLocations.GetChatLogFilepath();
                    RotateIfNeeded(filepath);
                    using (StreamWriter file = new StreamWriter(filepath, true, _encoding))
                    {
                        file.WriteLine(line);
                    }
                }
            }
            catch (Exception exc)
            {
                ReportError(exc);
            }
        }

        private static void RotateIfNeeded(string filepath)
        {
            FileInfo info = new FileInfo(filepath);
            if (!info.Exists) { return; }
            if (info.Length < MAX_FILE_BYTES) { return; }
            string rotatedFilepath = FileLocations.GetChatLogRotatedFilepath();
            if (File.Exists(rotatedFilepath))
            {
                File.Delete(rotatedFilepath);
            }
            File.Move(filepath, rotatedFilepath);
            log.WriteInfo("ChatLogWriter rotated chat log to '{0}'", rotatedFilepath);
        }

        private static void ReportError(Exception exc)
        {
            try
            {
                _errorCount++;
                if (_errorCount <= MAX_LOGGED_ERRORS)
                {
                    log.WriteError("ChatLogWriter exception ({0}): {1}", _errorCount, exc);
                    if (_errorCount == MAX_LOGGED_ERRORS)
                    {
                        log.WriteError("ChatLogWriter reached its logged error limit and will stay quiet");
                    }
                }
            }
            catch (Exception)
            {
                // Nothing sensible left to do; swallow so the game is undisturbed.
            }
        }
    }
}
