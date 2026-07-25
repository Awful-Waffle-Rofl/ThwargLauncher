using System;
using System.Runtime.InteropServices;

namespace ThwargLauncher.CommandLine
{
    /// <summary>
    /// ThwargLauncher is a WinExe (no console) so Console.WriteLine normally goes nowhere.
    /// When we are invoked from a terminal for a headless command we attach to the parent
    /// console (or allocate one) so that stdout/stderr are visible and scriptable.
    /// </summary>
    internal static class ConsoleManager
    {
        private const int ATTACH_PARENT_PROCESS = -1;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AllocConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeConsole();

        private static bool _attached;

        /// <summary>
        /// Attach to the console of the process that launched us (a terminal), if any.
        /// Falls back to allocating a fresh console so output is never silently lost.
        /// </summary>
        public static void EnsureConsole()
        {
            if (_attached) { return; }
            if (AttachConsole(ATTACH_PARENT_PROCESS) || AllocConsole())
            {
                _attached = true;
                // Re-point the managed streams at the freshly (re)attached console.
                try
                {
                    var stdout = new System.IO.StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                    Console.SetOut(stdout);
                    var stderr = new System.IO.StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
                    Console.SetError(stderr);
                }
                catch
                {
                    // If re-pointing fails we still tried our best; swallow so a launch can proceed.
                }
            }
        }

        public static void Release()
        {
            if (_attached)
            {
                FreeConsole();
                _attached = false;
            }
        }
    }
}
