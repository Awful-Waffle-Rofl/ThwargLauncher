using System;
using System.Windows.Forms;

using Decal.Adapter;
using KeyUtil;
using Util;

namespace Filter.Shared
{
    public static class PostMessageTools
    {
        // http://msdn.microsoft.com/en-us/library/dd375731%28v=vs.85%29.aspx

        private const byte VK_RETURN = 0x0D;
        private const byte VK_SHIFT = 0x10;
        private const byte VK_CONTROL = 0x11;
        private const byte VK_PAUSE = 0x13;
        private const byte VK_SPACE = 0x20;

        public static void SendEnter()
        {
            PostMsgs.SendEnter(CoreManager.Current.Decal.Hwnd);
        }
        public static void SendMsg(string msg)
        {
            PostMsgs.SendMsg(CoreManager.Current.Decal.Hwnd, msg);
        }
        public static void SendCharString(string msg)
        {
            PostMsgs.SendCharString(CoreManager.Current.Decal.Hwnd, msg);
        }
        public static void ClickOK()
        {
            User32.RECT rect = new User32.RECT();

            User32.GetWindowRect(CoreManager.Current.Decal.Hwnd, ref rect);

            // The reason why we click at both of these positions is some clients will be running windowed, and some windowless. This will hit both locations
            SendMouseClick(rect.Width / 2, rect.Height / 2 + 18);
            SendMouseClick(rect.Width / 2, rect.Height / 2 + 25);
            SendMouseClick(rect.Width / 2, rect.Height / 2 + 31);
        }
        public static void ClickYes()
        {
            User32.RECT rect = new User32.RECT();

            User32.GetWindowRect(CoreManager.Current.Decal.Hwnd, ref rect);

            // 800x600 +32 works, +33 does not work on single/double/tripple line boxes
            // 1600x1200 +31 works, +32 does not work on single/double/tripple line boxes
            // The reason why we click at both of these positions is some clients will be running windowed, and some windowless. This will hit both locations
            SendMouseClick(rect.Width / 2 - 80, rect.Height / 2 + 18);
            SendMouseClick(rect.Width / 2 - 80, rect.Height / 2 + 25);
            SendMouseClick(rect.Width / 2 - 80, rect.Height / 2 + 31);
        }
        /// <summary>
        /// Mirror of ClickYes for the No button. Same three vertical offsets and the same
        /// reasoning; the x offset is +80 rather than -80, matching the ClickNo that the
        /// sibling KeyTestApp\PostMsgs.cs has always used.
        /// </summary>
        public static void ClickNo()
        {
            User32.RECT rect = new User32.RECT();

            User32.GetWindowRect(CoreManager.Current.Decal.Hwnd, ref rect);

            SendMouseClick(rect.Width / 2 + 80, rect.Height / 2 + 18);
            SendMouseClick(rect.Width / 2 + 80, rect.Height / 2 + 25);
            SendMouseClick(rect.Width / 2 + 80, rect.Height / 2 + 31);
        }
        /// <summary>Press a named key in the game window. Pair with SendNamedKeyUp.</summary>
        public static void SendNamedKeyDown(NamedKey key)
        {
            PostMsgs.SendNamedKeyDown(CoreManager.Current.Decal.Hwnd, key);
        }
        /// <summary>Release a named key previously pressed with SendNamedKeyDown.</summary>
        public static void SendNamedKeyUp(NamedKey key)
        {
            PostMsgs.SendNamedKeyUp(CoreManager.Current.Decal.Hwnd, key);
        }
        /// <summary>Press and hold a key in the game window. Pair with SendKeyUp.</summary>
        public static void SendKeyDown(char ch)
        {
            PostMsgs.SendKeyDown(CoreManager.Current.Decal.Hwnd, ch);
        }
        /// <summary>Release a key previously pressed with SendKeyDown.</summary>
        public static void SendKeyUp(char ch)
        {
            PostMsgs.SendKeyUp(CoreManager.Current.Decal.Hwnd, ch);
        }
        public static void SendMouseClick(int x, int y)
        {
            PostMsgs.SendMouseClick(CoreManager.Current.Decal.Hwnd, (short)x, (short)y);
        }
    }
}
