using System;
using System.Collections.Generic;

using Decal.Adapter;

namespace ThwargFilter
{
    /// <summary>
    /// Records what the game says into chatlog_[pid].jsonl, from two independent sources:
    ///
    ///  source "network" - server to client messages seen on FilterCore's ServerDispatch.
    ///    Opcodes and field order were verified against the ACE server source that sends
    ///    them (ACE.Server\Network\GameMessages) and against Decal's own message schema
    ///    ("C:\Program Files (x86)\Decal 3.0\messages.xml"), which supplies the field names
    ///    used by the e.Message["field"] indexer.
    ///
    ///  source "chatbox" - lines drawn into the client chat window, seen on
    ///    CoreManager.Current.ChatBoxMessage. Decal plugin output (UtilityBelt, VirindiTank)
    ///    is rendered client side and is never a network message, so the network hook is
    ///    blind to it. Server chat generally appears on BOTH sources; the "source" field
    ///    lets the harness pick which one an assertion reads.
    ///
    /// Every handler here is exception guarded and never rethrows: a parse bug must not be
    /// able to break the filter.
    /// </summary>
    class ChatObserver
    {
        // Opcodes verified against C:\Users\danie\source\repos\ACE\Source\ACE.Server\
        // Network\GameMessages\GameMessageOpcode.cs
        private const int OPCODE_EmoteText = 0x01E0;         // GameMessageOpcode.cs:9
        private const int OPCODE_SoulEmote = 0x01E2;         // GameMessageOpcode.cs:10
        private const int OPCODE_HearSpeech = 0x02BB;        // GameMessageOpcode.cs:11
        private const int OPCODE_HearRangedSpeech = 0x02BC;  // GameMessageOpcode.cs:12
        private const int OPCODE_TurbineChat = 0xF7DE;       // GameMessageOpcode.cs:74
        private const int OPCODE_ServerMessage = 0xF7E0;     // GameMessageOpcode.cs:76

        // TurbineChat is a multiplexed blob; only this blob type carries chat text.
        private const int TURBINECHAT_BLOBTYPE_TEXT = 0x01;

        private const int MAX_LOGGED_ERRORS = 5;
        private static int _errorCount;

        /// <summary>
        /// Called from FilterCore_ServerDispatch, on the game's dispatch thread.
        /// Kept cheap: everything that is not a chat opcode returns immediately.
        /// </summary>
        public void FilterCore_ServerDispatch(object sender, NetworkMessageEventArgs e)
        {
            try
            {
                if (e == null || e.Message == null) { return; }
                int messageType = e.Message.Type;
                switch (messageType)
                {
                    case OPCODE_ServerMessage:
                        RecordServerMessage(e.Message);
                        break;
                    case OPCODE_HearSpeech:
                        RecordHearSpeech(e.Message, "HearSpeech", OPCODE_HearSpeech, false);
                        break;
                    case OPCODE_HearRangedSpeech:
                        RecordHearSpeech(e.Message, "HearRangedSpeech", OPCODE_HearRangedSpeech, true);
                        break;
                    case OPCODE_EmoteText:
                        RecordEmote(e.Message, "EmoteText", OPCODE_EmoteText);
                        break;
                    case OPCODE_SoulEmote:
                        RecordEmote(e.Message, "SoulEmote", OPCODE_SoulEmote);
                        break;
                    case OPCODE_TurbineChat:
                        RecordTurbineChat(e.Message);
                        break;
                    default:
                        break;
                }
            }
            catch (Exception exc)
            {
                ReportError("FilterCore_ServerDispatch", exc);
            }
        }

        /// <summary>
        /// Called from CoreManager.Current.ChatBoxMessage, on the game thread.
        /// Purely observational: this handler never modifies the event.
        /// </summary>
        public void Current_ChatBoxMessage(object sender, ChatTextInterceptEventArgs e)
        {
            try
            {
                if (e == null) { return; }
                string text = CleanText(e.Text);
                if (string.IsNullOrEmpty(text)) { return; }
                Dictionary<string, object> entry = NewEntry("chatbox", "ChatBoxMessage", null);
                entry["text"] = text;
                entry["color"] = e.Color;
                entry["target"] = e.Target;
                ChatLogWriter.WriteEntry(entry);
            }
            catch (Exception exc)
            {
                ReportError("Current_ChatBoxMessage", exc);
            }
        }

        /// <summary>
        /// ACE GameMessageSystemChat, sent as opcode ServerMessage (0xF7E0).
        /// ACE writes: string message, int chatMessageType
        /// (GameMessageSystemChat.cs:10-11). Decal names those fields "text" and "type"
        /// (messages.xml, message type="F7E0").
        /// </summary>
        private void RecordServerMessage(Message msg)
        {
            Dictionary<string, object> entry = NewEntry("network", "ServerMessage", OPCODE_ServerMessage);
            entry["text"] = GetStringField(msg, "text");
            AddIntField(entry, msg, "chatType", "type");
            ChatLogWriter.WriteEntry(entry);
        }

        /// <summary>
        /// ACE GameMessageHearSpeech (0x02BB) and GameMessageHearRangedSpeech (0x02BC).
        /// ACE writes: string messageText, string senderName, uint senderID,
        /// [float range for ranged], uint chatMessageType
        /// (GameMessageHearSpeech.cs:24-27, GameMessageHearRangedSpeech.cs:40-44).
        /// Decal names those fields text / senderName / sender / range / type.
        /// </summary>
        private void RecordHearSpeech(Message msg, string eventName, int opcode, bool ranged)
        {
            Dictionary<string, object> entry = NewEntry("network", eventName, opcode);
            entry["text"] = GetStringField(msg, "text");
            entry["senderName"] = GetStringField(msg, "senderName");
            AddIntField(entry, msg, "senderId", "sender");
            if (ranged)
            {
                AddDoubleField(entry, msg, "range", "range");
            }
            AddIntField(entry, msg, "chatType", "type");
            ChatLogWriter.WriteEntry(entry);
        }

        /// <summary>
        /// ACE GameMessageEmoteText (0x01E0) and GameMessageSoulEmote (0x01E2).
        /// ACE writes: uint senderId, string senderName, string emoteText
        /// (GameMessageEmoteText.cs:55-57, GameMessageSoulEmote.cs same shape).
        /// Decal names those fields sender / senderName / text.
        /// </summary>
        private void RecordEmote(Message msg, string eventName, int opcode)
        {
            Dictionary<string, object> entry = NewEntry("network", eventName, opcode);
            entry["text"] = GetStringField(msg, "text");
            entry["senderName"] = GetStringField(msg, "senderName");
            AddIntField(entry, msg, "senderId", "sender");
            ChatLogWriter.WriteEntry(entry);
        }

        /// <summary>
        /// ACE GameMessageTurbineChat (0xF7DE) carries allegiance and general chat inside a
        /// nested blob (GameMessageTurbineChat.cs:82-110). Decal's schema switches on the
        /// blob "type" field and only exposes channel / senderName / text / sender for
        /// blob type 0x01, so anything else is ignored here.
        /// </summary>
        private void RecordTurbineChat(Message msg)
        {
            int blobType = 0;
            if (!TryGetIntField(msg, "type", out blobType)) { return; }
            if (blobType != TURBINECHAT_BLOBTYPE_TEXT) { return; }
            Dictionary<string, object> entry = NewEntry("network", "TurbineChat", OPCODE_TurbineChat);
            entry["text"] = GetStringField(msg, "text");
            entry["senderName"] = GetStringField(msg, "senderName");
            AddIntField(entry, msg, "senderId", "sender");
            AddIntField(entry, msg, "channel", "channel");
            ChatLogWriter.WriteEntry(entry);
        }

        private static Dictionary<string, object> NewEntry(string source, string eventName, object opcode)
        {
            Dictionary<string, object> entry = new Dictionary<string, object>();
            entry["utc"] = DateTime.UtcNow.ToString("o");
            entry["source"] = source;
            entry["type"] = eventName;
            if (opcode != null)
            {
                entry["opcode"] = string.Format("0x{0:X4}", opcode);
            }
            return entry;
        }

        private static string CleanText(string text)
        {
            if (text == null) { return null; }
            return text.TrimEnd('\r', '\n');
        }

        private static string GetStringField(Message msg, string fieldName)
        {
            try
            {
                object value = msg[fieldName];
                if (value == null) { return null; }
                return CleanText(Convert.ToString(value));
            }
            catch (Exception)
            {
                // Field absent or the wrong shape for this message; report it as missing.
                return null;
            }
        }

        private static bool TryGetIntField(Message msg, string fieldName, out int result)
        {
            result = 0;
            try
            {
                object value = msg[fieldName];
                if (value == null) { return false; }
                result = Convert.ToInt32(value);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void AddIntField(Dictionary<string, object> entry, Message msg, string entryKey, string fieldName)
        {
            int value = 0;
            if (TryGetIntField(msg, fieldName, out value))
            {
                entry[entryKey] = value;
            }
        }

        private static void AddDoubleField(Dictionary<string, object> entry, Message msg, string entryKey, string fieldName)
        {
            try
            {
                object value = msg[fieldName];
                if (value == null) { return; }
                entry[entryKey] = Convert.ToDouble(value);
            }
            catch (Exception)
            {
                // Absent optional field; leave it out of the record.
            }
        }

        private static void ReportError(string context, Exception exc)
        {
            try
            {
                _errorCount++;
                if (_errorCount <= MAX_LOGGED_ERRORS)
                {
                    log.WriteError("ChatObserver.{0} exception ({1}): {2}", context, _errorCount, exc);
                    if (_errorCount == MAX_LOGGED_ERRORS)
                    {
                        log.WriteError("ChatObserver reached its logged error limit and will stay quiet");
                    }
                }
            }
            catch (Exception)
            {
                // Never rethrow out of an observation handler.
            }
        }
    }
}
