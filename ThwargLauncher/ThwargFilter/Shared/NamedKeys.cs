using System;

namespace Filter.Shared
{
    /// <summary>
    /// A keyboard key we can post to the game window, described the way Windows wants it in
    /// a WM_KEYDOWN / WM_KEYUP lParam.
    ///
    /// WHY THIS EXISTS: PostMsgs.CharCode() / ScanCode() only map a-z, '/' and space, and
    /// fall through to 0x20 for anything else. That makes a char-typed key setting
    /// unreachable-by-construction for the keys AC actually uses for combat (End, Delete,
    /// Page Down and friends), which is ledger item L5-1. Those keys also need the
    /// EXTENDED-KEY bit, which the char path never sets.
    ///
    /// lParam layout for WM_KEYDOWN / WM_KEYUP:
    ///   bits 0-15   repeat count            (1)
    ///   bits 16-23  scan code               (PS/2 set 1 make code, same as the DIK_ base code)
    ///   bit  24     extended key            (1 for the grey navigation keys)
    ///   bit  30     previous key state      (1 on key up)
    ///   bit  31     transition state        (1 on key up)
    /// so key up is simply key down with 0xC0000000 set.
    ///
    /// Worked example, End: scan 0x4F, extended, giving
    ///   down 0x014F0001   up 0xC14F0001
    /// which is exactly the pair that was live-verified against a target dummy.
    /// </summary>
    public class NamedKey
    {
        public readonly string Name;
        /// <summary>Virtual key code, the wParam of the message.</summary>
        public readonly byte VirtualKey;
        /// <summary>PS/2 set 1 make code. Same number as the DIK_ constant's base value.</summary>
        public readonly byte ScanCode;
        /// <summary>True for the grey navigation cluster; sets bit 24 of lParam.</summary>
        public readonly bool Extended;
        /// <summary>What this key does in the AC client, for logging and docs.</summary>
        public readonly string GameFunction;

        public NamedKey(string name, byte virtualKey, byte scanCode, bool extended, string gameFunction)
        {
            this.Name = name;
            this.VirtualKey = virtualKey;
            this.ScanCode = scanCode;
            this.Extended = extended;
            this.GameFunction = gameFunction;
        }

        private const uint LPARAM_RepeatCount = 0x00000001;
        private const uint LPARAM_ExtendedBit = 0x01000000;
        // Previous key state (bit 30) plus transition state (bit 31).
        private const uint LPARAM_KeyUpBits = 0xC0000000;

        public uint KeyDownLParam
        {
            get
            {
                uint lparam = LPARAM_RepeatCount | ((uint)ScanCode << 16);
                if (Extended) { lparam |= LPARAM_ExtendedBit; }
                return lparam;
            }
        }

        public uint KeyUpLParam
        {
            get { return KeyDownLParam | LPARAM_KeyUpBits; }
        }

        public override string ToString()
        {
            return string.Format(
                "{0} (vk=0x{1:X2} scan=0x{2:X2}{3} down=0x{4:X8} up=0x{5:X8})",
                Name, VirtualKey, ScanCode, (Extended ? " extended" : ""),
                KeyDownLParam, KeyUpLParam);
        }
    }

    /// <summary>
    /// The AC combat key vocabulary, as bound in the client keymap.
    ///
    /// VERIFICATION STATUS: the End entry is live-verified; posting it produces exactly the
    /// lParams batch 5 confirmed start the client's repeating attack loop. The others are
    /// derived from the same standard PS/2 set 1 table and share End's shape, but have NOT
    /// each been live-verified. Treat their scan codes as high confidence, not proven.
    ///
    /// The authoritative binding source is the player's own keymap file, see
    /// TESTING_CHANNEL.md. Bindings there are DIK (DirectInput) scan codes, not virtual key
    /// codes, so read that file for WHICH key does what and this table for HOW to post it.
    /// </summary>
    public static class NamedKeys
    {
        // Attack heights. In missile mode these are the aim heights; the keymap also shows
        // DIK_END bound to CombatCastCurrentSpell in MagicCombat mode.
        public static readonly NamedKey End = new NamedKey("End", 0x23, 0x4F, true, "attack low / missile aim low / cast current spell");
        public static readonly NamedKey Delete = new NamedKey("Delete", 0x2E, 0x53, true, "attack high / missile aim high");
        public static readonly NamedKey PageDown = new NamedKey("PageDown", 0x22, 0x51, true, "attack medium / missile aim medium");

        // Attack bar steps. Melee reads these as power and speed; missile reads them as
        // accuracy.
        public static readonly NamedKey Insert = new NamedKey("Insert", 0x2D, 0x52, true, "attack bar step down (power/speed, missile accuracy)");
        public static readonly NamedKey PageUp = new NamedKey("PageUp", 0x21, 0x49, true, "attack bar step up (power/speed, missile accuracy)");

        // Targeting and mode. Not extended: these are main-block keys.
        public static readonly NamedKey Apostrophe = new NamedKey("Apostrophe", 0xDE, 0x28, false, "select closest monster");
        public static readonly NamedKey Backtick = new NamedKey("Backtick", 0xC0, 0x29, false, "toggle combat mode");

        private static readonly NamedKey[] All = new NamedKey[]
        {
            End, Delete, PageDown, Insert, PageUp, Apostrophe, Backtick
        };

        /// <summary>
        /// Case insensitive lookup by name. Returns null if the name is not known, so the
        /// caller can report the mistake rather than silently posting the wrong key.
        /// A few common aliases are accepted.
        /// </summary>
        public static NamedKey Find(string name)
        {
            if (string.IsNullOrEmpty(name)) { return null; }
            string wanted = name.Trim();
            // Aliases for names people actually type.
            if (string.Compare(wanted, "PgDn", StringComparison.OrdinalIgnoreCase) == 0) { return PageDown; }
            if (string.Compare(wanted, "PgUp", StringComparison.OrdinalIgnoreCase) == 0) { return PageUp; }
            if (string.Compare(wanted, "Del", StringComparison.OrdinalIgnoreCase) == 0) { return Delete; }
            if (string.Compare(wanted, "Ins", StringComparison.OrdinalIgnoreCase) == 0) { return Insert; }
            if (string.Compare(wanted, "Grave", StringComparison.OrdinalIgnoreCase) == 0) { return Backtick; }
            if (string.Compare(wanted, "Quote", StringComparison.OrdinalIgnoreCase) == 0) { return Apostrophe; }
            for (int i = 0; i < All.Length; i++)
            {
                if (string.Compare(wanted, All[i].Name, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    return All[i];
                }
            }
            return null;
        }

        /// <summary>Comma separated list of known names, for error messages.</summary>
        public static string GetKnownNames()
        {
            string[] names = new string[All.Length];
            for (int i = 0; i < All.Length; i++) { names[i] = All[i].Name; }
            return string.Join(",", names);
        }
    }
}
