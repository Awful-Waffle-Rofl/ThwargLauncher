using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Windows;

namespace ThwargLauncher
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            AppDomain.CurrentDomain.UnhandledException += (sender, eargs)
                => HandleExcObject(eargs.ExceptionObject);

            // Headless command-line verbs run without the WPF UI and then exit.
            // e.Args excludes the exe path; the verb is the first non-switch token.
            if (e.Args.Length > 0 &&
                string.Equals(e.Args[0], CommandLine.HeadlessLauncher.Verb, StringComparison.OrdinalIgnoreCase))
            {
                var verbArgs = new string[e.Args.Length - 1];
                Array.Copy(e.Args, 1, verbArgs, 0, verbArgs.Length);
                int exitCode = CommandLine.HeadlessLauncher.Run(verbArgs);
                Shutdown(exitCode);
                return;
            }

            AppCoordinator appcoord = new AppCoordinator();
        }
        void HandleExcObject(object excObj)
        {
            var exc = excObj as Exception;
            if (exc == null)
            {
                exc = new NotSupportedException(
                    "Unhandled exception doesn't derive from System.Exception: "
                    + excObj.ToString());
            }
            HandleExc(exc);
        }
        void HandleExc(Exception exc)
        {
            Logger.WriteError("Fatal Exception: " + exc.ToString());
            MessageBox.Show("Fatal Program Error: See log file");
        }
    }
}
