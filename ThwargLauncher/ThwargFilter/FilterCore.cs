using System;
using System.IO;
using System.Runtime.InteropServices;

using Filter.Shared;
using Filter.Shared.Settings;

using Decal.Adapter;

namespace ThwargFilter
{
    [FriendlyName("ThwargFilter")]
    public class FilterCore : FilterBase
    {
        readonly AutoRetryLogin autoRetryLogin = new AutoRetryLogin();
        readonly LoginCharacterTools loginCharacterTools = new LoginCharacterTools();
        readonly FastQuit fastQuit = new FastQuit();
        readonly LoginCompleteMessageQueueManager loginCompleteMessageQueueManager = new LoginCompleteMessageQueueManager();
        readonly AfterLoginCompleteMessageQueueManager afterLoginCompleteMessageQueueManager = new AfterLoginCompleteMessageQueueManager();
        readonly ChatObserver chatObserver = new ChatObserver();
        readonly GameStateDumper gameStateDumper = new GameStateDumper();
        readonly Appraiser appraiser = new Appraiser();
        readonly Attacker attacker = new Attacker();
        readonly Unwielder unwielder = new Unwielder();
        readonly Wielder wielder = new Wielder();
        readonly KeyDumper keyDumper = new KeyDumper();
        readonly SpellBar spellBar = new SpellBar();
        readonly Confirmer confirmer = new Confirmer();

        DefaultFirstCharacterManager defaultFirstCharacterManager;
        private LauncherChooseCharacterManager chooseCharacterManager;
        private ThwargFilterCommandExecutor ThwargFilterCommandExecutor;
        private ThwargFilterCommandParser ThwargFilterCommandParser;
        private LoginNextCharacterManager loginNextCharacterManager;
        private ThwargInventory thwargInventory;

        private DateTime _lastServerDispatchUtc = DateTime.MinValue;
        private static FilterCore theFilterCore = null;
        // Only unsubscribe chat box capture in Shutdown if subscribing actually succeeded.
        private bool _chatBoxMessageSubscribed;


        private string PluginName { get { return FileLocations.FilterName; } }

        public void ExternalStartup() { Startup(); } // for game emulator
        protected override void Startup()
        {
            Debug.Init(FileLocations.PluginPersonalFolder.FullName + @"\Exceptions.txt", PluginName);
            SettingsFile.Init(FileLocations.GetFilterSettingsFilepath(), PluginName);
            LogStartup();
            theFilterCore = this;

            defaultFirstCharacterManager = new DefaultFirstCharacterManager(loginCharacterTools);
            chooseCharacterManager = new LauncherChooseCharacterManager(loginCharacterTools);
            ThwargFilterCommandExecutor = new ThwargFilterCommandExecutor();
            ThwargFilterCommandParser = new ThwargFilterCommandParser(ThwargFilterCommandExecutor);
            Heartbeat.SetCommandParser(ThwargFilterCommandParser);
            loginNextCharacterManager = new LoginNextCharacterManager(loginCharacterTools);
            thwargInventory = new ThwargInventory();
            ThwargFilterCommandParser.Inventory = thwargInventory;
            ThwargFilterCommandParser.GameState = gameStateDumper;
            ThwargFilterCommandParser.Appraise = appraiser;
            ThwargFilterCommandParser.Attack = attacker;
            ThwargFilterCommandParser.Unwield = unwielder;
            ThwargFilterCommandParser.Wield = wielder;
            ThwargFilterCommandParser.Keys = keyDumper;
            ThwargFilterCommandParser.SpellBarManager = spellBar;
            ThwargFilterCommandParser.Confirm = confirmer;

            ClientDispatch += new EventHandler<NetworkMessageEventArgs>(FilterCore_ClientDispatch);
            ServerDispatch += new EventHandler<NetworkMessageEventArgs>(FilterCore_ServerDispatch);
            WindowMessage += new EventHandler<WindowMessageEventArgs>(FilterCore_WindowMessage);

            CommandLineText += new EventHandler<ChatParserInterceptEventArgs>(FilterCore_CommandLineText);

            // Chat window capture for the test observation channel. Decal plugin output
            // (UtilityBelt, VirindiTank) is drawn client side and never appears as a
            // server message, so ServerDispatch alone cannot see it.
            // Guarded on its own: this is an optional observation feature, and it must
            // never be able to abort Startup and take the core filter (heartbeat,
            // channel, auto-login) down with it.
            try
            {
                CoreManager.Current.ChatBoxMessage += new EventHandler<ChatTextInterceptEventArgs>(Current_ChatBoxMessage);
                _chatBoxMessageSubscribed = true;
            }
            catch (Exception ex)
            {
                log.WriteError("Failed to subscribe chat box capture, continuing without it: {0}", ex);
            }

            // Start the heartbeat now rather than waiting for the login flow to start it.
            // A client that stalls at "Connecting" never reaches the login managers, so
            // without this it produces no game_<pid>.txt at all and a test harness is
            // blind exactly when it most needs to see something. LaunchHeartbeat is
            // idempotent, so the existing lazy calls in the login flow stay as they are.
            // Guarded for the same reason as the subscribe above: telemetry must never
            // be able to abort Startup and take the core filter down with it.
            try
            {
                LoginStageTracker.SetStage(LoginStageTracker.STAGE_Starting);
                Heartbeat.LaunchHeartbeat();
            }
            catch (Exception ex)
            {
                log.WriteError("Failed to start heartbeat at startup, continuing: {0}", ex);
            }
        }

        public static DateTime GetLastServerDispatchUtc()
        {
            return theFilterCore._lastServerDispatchUtc;
        }

        private void LogStartup()
        {
            System.Reflection.Assembly assembly = System.Reflection.Assembly.GetExecutingAssembly();

            log.WriteInfo(
                "ThwargFilter.Startup, AssemblyVer: {0}, AssemblyFileVer: {1}",
                assembly.GetName().Version,
                System.Diagnostics.FileVersionInfo.GetVersionInfo(assembly.Location)
                                );
        }

        public void ExternalShutdown() { Shutdown(); } // for game emulator
        protected override void Shutdown()
        {
            ClientDispatch -= new EventHandler<NetworkMessageEventArgs>(FilterCore_ClientDispatch);
            ServerDispatch -= new EventHandler<NetworkMessageEventArgs>(FilterCore_ServerDispatch);
            WindowMessage -= new EventHandler<WindowMessageEventArgs>(FilterCore_WindowMessage);

            CommandLineText -= new EventHandler<ChatParserInterceptEventArgs>(FilterCore_CommandLineText);

            // Mirror of the guarded subscribe in Startup: only unsubscribe if we managed
            // to subscribe, and never let a failure here abort Shutdown.
            if (_chatBoxMessageSubscribed)
            {
                try
                {
                    CoreManager.Current.ChatBoxMessage -= new EventHandler<ChatTextInterceptEventArgs>(Current_ChatBoxMessage);
                    _chatBoxMessageSubscribed = false;
                }
                catch (Exception ex)
                {
                    log.WriteError("Failed to unsubscribe chat box capture: {0}", ex);
                }
            }

            log.WriteInfo("FilterCore-Shutdown");
        }

        public void CallFilterCoreClientDispatch(object sender, NetworkMessageEventArgs e) // for game emulator
        {
            FilterCore_ClientDispatch(sender, e);
        }
        void FilterCore_ClientDispatch(object sender, NetworkMessageEventArgs e)
        {
            try
            {
                autoRetryLogin.FilterCore_ClientDispatch(sender, e);
                loginCompleteMessageQueueManager.FilterCore_ClientDispatch(sender, e);
                afterLoginCompleteMessageQueueManager.FilterCore_ClientDispatch(sender, e);
            }
            catch (Exception ex) { Debug.LogException(ex); }
        }

        void FilterCore_ServerDispatch(object sender, NetworkMessageEventArgs e)
        {
            try
            {
                _lastServerDispatchUtc = DateTime.UtcNow;
                autoRetryLogin.FilterCore_ServerDispatch(sender, e);
                loginCharacterTools.FilterCore_ServerDispatch(sender, e);

                defaultFirstCharacterManager.FilterCore_ServerDispatch(sender, e);
                chooseCharacterManager.FilterCore_ServerDispatch(sender, e);
                loginNextCharacterManager.FilterCore_ServerDispatch(sender, e);

                // Observation only, and last, so it cannot affect the login path above.
                // Both of these swallow their own exceptions and never rethrow.
                chatObserver.FilterCore_ServerDispatch(sender, e);
                confirmer.FilterCore_ServerDispatch(sender, e);
            }
            catch (Exception ex) { Debug.LogException(ex); }
        }

        void Current_ChatBoxMessage(object sender, ChatTextInterceptEventArgs e)
        {
            try
            {
                chatObserver.Current_ChatBoxMessage(sender, e);
            }
            catch (Exception ex) { Debug.LogException(ex); }
        }

        void FilterCore_WindowMessage(object sender, WindowMessageEventArgs e)
        {
            try
            {
                fastQuit.FilterCore_WindowMessage(sender, e);
            }
            catch (Exception ex) { Debug.LogException(ex); }
        }

        void FilterCore_CommandLineText(object sender, ChatParserInterceptEventArgs e)
        {
            try
            {
                loginCompleteMessageQueueManager.FilterCore_CommandLineText(sender, e);
                afterLoginCompleteMessageQueueManager.FilterCore_CommandLineText(sender, e);

                defaultFirstCharacterManager.FilterCore_CommandLineText(sender, e);
                chooseCharacterManager.FilterCore_CommandLineText(sender, e);
                loginNextCharacterManager.FilterCore_CommandLineText(sender, e);
                ThwargFilterCommandParser.FilterCore_CommandLineText(sender, e);
            }
            catch (Exception ex) { Debug.LogException(ex); }
        }
    }
}
