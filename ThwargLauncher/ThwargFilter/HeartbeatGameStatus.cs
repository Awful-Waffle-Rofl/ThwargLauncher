using System;

namespace ThwargFilter
{
    public class HeartbeatGameStatus
    {
        // 1.5 added LoginStage, SecondsInStage, RequestedCharacter and StatusNote.
        // The compat prefix stays "1" so older parsers keep reading the fields they know.
        public const string MASTER_FILE_VERSION = "1.5";
        public const string MASTER_FILE_VERSION_COMPAT = "1";

        public string FileVersion;
        public string ServerName;
        public string AccountName;
        public string CharacterName;
        public int UptimeSeconds;
        public int ProcessId;
        public string TeamList; // separated by commas and no spaces
        public string ThwargFilterVersion;
        public string ThwargFilterFilePath;
        public bool IsOnline;
        public int LastServerDispatchSecondsAgo;
        public string ActualServerName;
        public string ActualAccountName;
        public string ActualCharacterName;
        // Login stage telemetry (file version 1.5 and later).
        public string LoginStage;
        public int SecondsInStage;
        public string RequestedCharacter;
        public string StatusNote;
    }
}
