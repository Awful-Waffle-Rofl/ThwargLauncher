using System;

namespace ThwargFilter
{
    public class HeartbeatGameStatus
    {
        // 1.5 added LoginStage, SecondsInStage, RequestedCharacter and StatusNote.
        // 1.6 added ConfirmationState, ConfirmationType, ConfirmationContext,
        //     ConfirmationText and ConfirmationAnswer.
        // The compat prefix stays "1" so older parsers keep reading the fields they know.
        public const string MASTER_FILE_VERSION = "1.6";
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
        // Server confirmation dialog telemetry (file version 1.6 and later).
        // ConfirmationState is one of none / outstanding / answered / aborted / expired;
        // see Confirmer for what each one means. A test polls for "outstanding" to know a
        // question is waiting, because there is no other remote-visible signal that one
        // was asked - it is otherwise only in the ACE server's own log.
        // ConfirmationContext is written as an int: ACE's context ids come from a
        // sequence that starts at zero, so this only loses precision in the impossible
        // case of a context above int.MaxValue. The chat log always carries the exact
        // value.
        public string ConfirmationState;
        public int ConfirmationType;
        public int ConfirmationContext;
        public string ConfirmationText;
        public string ConfirmationAnswer;
    }
}
