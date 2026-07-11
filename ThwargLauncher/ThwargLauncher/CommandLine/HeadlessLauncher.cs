using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ThwargLauncher.GlobalResources;

namespace ThwargLauncher.CommandLine
{
    /// <summary>
    /// Headless "launch" verb: launch a single AC client for one account/server/character
    /// from the command line, with no WPF window, then exit. Reuses the same server list,
    /// account file and injection path that the GUI uses, so ThwargFilter/Decal still injects
    /// and auto-selects the requested character.
    ///
    /// Usage:
    ///   ThwargLauncher.exe launch --server WaffleHouse --account MyAcct --character "My Toon"
    ///                             [--client "C:\...\acclient.exe"] [--rodat on|off]
    ///                             [--simple] [--keep-client] [--timeout 120]
    /// </summary>
    internal static class HeadlessLauncher
    {
        /// <summary>The verb (first non-switch arg) that routes into this launcher.</summary>
        public const string Verb = "launch";

        private class Options
        {
            public string Server;
            public string Account;
            public string Character = "None";
            public string ClientPath;
            public bool? Rodat;            // null => use server default
            public bool Simple;            // force minimal launch (no inject / no wait)
            public bool KeepClient = true; // default: never kill the client we launch
            public int? TimeoutSeconds;
            public bool ShowHelp;
        }

        public static int Run(string[] verbArgs)
        {
            ConsoleManager.EnsureConsole();
            try
            {
                var opts = ParseArgs(verbArgs);
                if (opts.ShowHelp)
                {
                    PrintUsage();
                    return 0;
                }

                var errors = new List<string>();
                if (string.IsNullOrWhiteSpace(opts.Server)) { errors.Add("--server is required"); }
                if (string.IsNullOrWhiteSpace(opts.Account)) { errors.Add("--account is required"); }
                if (errors.Count > 0)
                {
                    foreach (var e in errors) { Console.Error.WriteLine("ERROR: " + e); }
                    Console.Error.WriteLine();
                    PrintUsage();
                    return (int)ExitCode.BadArguments;
                }

                // ---- Resolve the server (name -> ip:port, emu, rodat) ----
                ServerManager.LoadServerLists();
                var server = ServerManager.ServerList.FirstOrDefault(
                    s => string.Equals(s.ServerName, opts.Server, StringComparison.OrdinalIgnoreCase));
                if (server == null)
                {
                    Console.Error.WriteLine("ERROR: Unknown server '" + opts.Server + "'.");
                    Console.Error.WriteLine("Known servers: " +
                        string.Join(", ", ServerManager.ServerList.Select(s => s.ServerName)));
                    return (int)ExitCode.UnknownServer;
                }

                // ---- Resolve the account (name -> password, custom launch path) ----
                var accounts = ReadAccounts();
                var account = accounts.FirstOrDefault(
                    a => string.Equals(a.Name, opts.Account, StringComparison.OrdinalIgnoreCase));
                if (account == null)
                {
                    Console.Error.WriteLine("ERROR: Unknown account '" + opts.Account + "'.");
                    Console.Error.WriteLine("Known accounts: " +
                        string.Join(", ", accounts.Select(a => a.Name)));
                    return (int)ExitCode.UnknownAccount;
                }

                // ---- Resolve the client exe (--client > account custom path > global setting) ----
                string exe = FirstNonEmpty(
                    opts.ClientPath,
                    account.CustomLaunchPath,
                    SafeGetAcLocation());
                if (string.IsNullOrWhiteSpace(exe))
                {
                    Console.Error.WriteLine("ERROR: No client exe. Pass --client <path>, set a LaunchPath on the " +
                        "account, or configure the AC client location in the launcher first.");
                    return (int)ExitCode.NoClientExe;
                }
                if (!File.Exists(exe))
                {
                    Console.Error.WriteLine("ERROR: Client exe not found: " + exe);
                    return (int)ExitCode.NoClientExe;
                }

                // ---- Settings that affect the launch path ----
                if (opts.KeepClient)
                {
                    // Read by Globals.NeverKillClients; kept in-process only (we do not Save()).
                    Properties.Settings.Default.NeverKillClients = true;
                }
                if (opts.TimeoutSeconds.HasValue)
                {
                    // GameLauncher reads this via ConfigSettings/ConfigurationManager.AppSettings.
                    // Override it in-process only (not persisted to App.config).
                    try
                    {
                        System.Configuration.ConfigurationManager.AppSettings.Set(
                            "LauncherGameTimeoutSeconds", opts.TimeoutSeconds.Value.ToString());
                    }
                    catch
                    {
                        Console.Error.WriteLine("WARN: could not apply --timeout override; using configured value.");
                    }
                }

                bool simple = opts.Simple;
                if (!simple && !DecalIsInstalled())
                {
                    Console.Error.WriteLine("WARN: Decal not detected; falling back to --simple (no character " +
                        "auto-login). Install Decal for full character login.");
                    simple = true;
                }

                var rodat = opts.Rodat.HasValue
                    ? (opts.Rodat.Value ? ServerModel.RodatEnum.On : ServerModel.RodatEnum.Off)
                    : server.RodatSetting;

                Console.WriteLine(string.Format(
                    "Launching '{0}' on '{1}' ({2}) as account '{3}'{4}{5}...",
                    opts.Character, server.ServerName, server.ServerIpAndPort, account.Name,
                    simple ? " [simple]" : " [inject]",
                    opts.KeepClient ? " [keep-client]" : ""));

                var launcher = new GameLauncher();
                launcher.ReportGameStatusEvent += (notice) =>
                {
                    if (notice != null && !string.IsNullOrEmpty(notice.StatusText))
                    {
                        Console.WriteLine("  " + notice.StatusText);
                    }
                };

                GameLaunchResult result = launcher.LaunchGameClient(
                    exelocation: exe,
                    serverName: server.ServerName,
                    accountName: account.Name,
                    password: account.Password,
                    ipAddress: server.ServerIpAndPort,
                    gameApiUrl: server.GameApiUrl,
                    loginServerUrl: server.LoginServerUrl,
                    discordurl: server.DiscordUrl,
                    emu: server.EMU,
                    desiredCharacter: opts.Character,
                    rodatSetting: rodat,
                    secureSetting: server.SecureSetting,
                    simpleLaunch: simple);

                if (result != null && result.Success)
                {
                    Console.WriteLine("OK PID=" + result.ProcessId);
                    return (int)ExitCode.Success;
                }

                Console.Error.WriteLine("ERROR: Launch did not complete successfully.");
                return (int)ExitCode.LaunchFailed;
            }
            catch (Exception exc)
            {
                Console.Error.WriteLine("ERROR: " + exc.Message);
                Logger.WriteError("Headless launch failed: " + exc);
                return (int)ExitCode.Exception;
            }
            finally
            {
                Console.Out.Flush();
                Console.Error.Flush();
            }
        }

        private enum ExitCode
        {
            Success = 0,
            Exception = 1,
            BadArguments = 2,
            UnknownServer = 3,
            UnknownAccount = 4,
            NoClientExe = 5,
            LaunchFailed = 6,
        }

        private static Options ParseArgs(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                switch (a.ToLowerInvariant())
                {
                    case "--server": case "-s": o.Server = Next(args, ref i); break;
                    case "--account": case "-a": o.Account = Next(args, ref i); break;
                    case "--character": case "-c": o.Character = Next(args, ref i); break;
                    case "--client": o.ClientPath = Next(args, ref i); break;
                    case "--rodat": o.Rodat = ParseOnOff(Next(args, ref i)); break;
                    case "--timeout": o.TimeoutSeconds = ParseInt(Next(args, ref i)); break;
                    case "--simple": o.Simple = true; break;
                    case "--keep-client": o.KeepClient = true; break;
                    case "--no-keep-client": o.KeepClient = false; break;
                    case "--no-window": break; // implied by the verb; accepted for clarity
                    case "--help": case "-h": case "-?": o.ShowHelp = true; break;
                    default:
                        Console.Error.WriteLine("WARN: ignoring unknown argument '" + a + "'");
                        break;
                }
            }
            return o;
        }

        private static string Next(string[] args, ref int i)
        {
            if (i + 1 >= args.Length)
            {
                throw new Exception("Missing value after '" + args[i] + "'");
            }
            return args[++i];
        }

        private static bool ParseOnOff(string v)
        {
            if (string.Equals(v, "on", StringComparison.OrdinalIgnoreCase)
                || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)) { return true; }
            if (string.Equals(v, "off", StringComparison.OrdinalIgnoreCase)
                || string.Equals(v, "false", StringComparison.OrdinalIgnoreCase)) { return false; }
            throw new Exception("Expected on|off, got '" + v + "'");
        }

        private static int ParseInt(string v)
        {
            int result;
            if (!int.TryParse(v, out result)) { throw new Exception("Expected a number, got '" + v + "'"); }
            return result;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var v in values)
            {
                if (!string.IsNullOrWhiteSpace(v)) { return v; }
            }
            return null;
        }

        private static List<UserAccount> ReadAccounts()
        {
            string oldUsersFilePath = Path.Combine(Configuration.AppFolder, "UserNames.txt");
            return new AccountParser().ReadOrMigrateAccounts(oldUsersFilePath);
        }

        private static string SafeGetAcLocation()
        {
            try { return Properties.Settings.Default.ACLocation; }
            catch { return null; }
        }

        private static bool DecalIsInstalled()
        {
            try { return DecalInjection.IsDecalInstalled(); }
            catch { return false; }
        }

        private static void PrintUsage()
        {
            Console.WriteLine();
            Console.WriteLine("ThwargLauncher headless launch");
            Console.WriteLine("  Launches a single AC client with no window, then exits.");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  ThwargLauncher.exe launch --server <name> --account <name> [--character <name>]");
            Console.WriteLine("                            [--client <path>] [--rodat on|off] [--simple]");
            Console.WriteLine("                            [--keep-client|--no-keep-client] [--timeout <sec>]");
            Console.WriteLine();
            Console.WriteLine("Required:");
            Console.WriteLine("  --server,  -s   Server name as it appears in the launcher's server list.");
            Console.WriteLine("  --account, -a   Account name as it appears in the launcher's account list.");
            Console.WriteLine();
            Console.WriteLine("Optional:");
            Console.WriteLine("  --character, -c   Character to auto-select (requires Decal). Default: none.");
            Console.WriteLine("  --client          Full path to acclient.exe (overrides account/global setting).");
            Console.WriteLine("  --rodat on|off    Override the server's rodat setting.");
            Console.WriteLine("  --simple          Just spawn the client (no injection, no wait, no auto-login).");
            Console.WriteLine("  --keep-client     Do not kill the client if login times out (default on).");
            Console.WriteLine("  --no-keep-client  Allow the client to be killed on a failed/timed-out login.");
            Console.WriteLine("  --timeout <sec>   Seconds to wait for login before giving up (inject mode).");
            Console.WriteLine();
            Console.WriteLine("Exit codes: 0 success, 2 bad args, 3 unknown server, 4 unknown account,");
            Console.WriteLine("            5 no/invalid client exe, 6 launch failed, 1 other error.");
            Console.WriteLine();
            Console.WriteLine("Example:");
            Console.WriteLine("  ThwargLauncher.exe launch --server WaffleHouse --account MyAcct --character \"My Toon\"");
        }
    }
}
