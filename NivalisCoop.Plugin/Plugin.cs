using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace NivalisCoop.Plugin
{
    [BepInPlugin(Guid, "Nivalis Coop", NivalisCoop.Net.CoopClient.ModVersion)]
    public class Plugin : BasePlugin
    {
        public const string Guid = "dev.katte.nivaliscoop";

        internal static ManualLogSource Logger;
        internal static ConfigEntry<string> Address;
        internal static ConfigEntry<string> PlayerName;
        internal static ConfigEntry<string> Password;
        internal static ConfigEntry<KeyCode> ToggleKey;
        internal static ConfigEntry<KeyCode> ChatKey;
        internal static ConfigEntry<KeyCode> ChatKeyAlt;
        internal static ConfigEntry<bool> AutoConnect;
        internal static ConfigEntry<bool> SyncMoney;

        public override void Load()
        {
            Logger = Log;
            Address = Config.Bind("Server", "Address", "127.0.0.1:27800", "host:port of the Nivalis Coop server");
            Password = Config.Bind("Server", "Password", "", "Server password, if the server has one");
            PlayerName = Config.Bind("Player", "Name", "Player", "Name shown to the others (2-24 letters, digits, space . - _)");
            ToggleKey = Config.Bind("Keys", "ToggleWindow", KeyCode.F8, "Opens and closes the multiplayer window (F1-F12)");
            ChatKey = Config.Bind("Keys", "Chat", KeyCode.Return, "Opens the server chat while you are in a server world (Enter by default; Enter sends, Esc closes)");
            ChatKeyAlt = Config.Bind("Keys", "ChatAnywhere", KeyCode.T, "Second key that opens the chat everywhere, also in menus and conversations where Enter belongs to the game (None = off)");
            SyncMoney = Config.Bind("Sync", "Money", true,
                "While you are connected to a server, your money is the server's shared account (all players use one purse). Inventories stay separate.");
            AutoConnect = Config.Bind("Dev", "AutoConnect", false, "Connect a few seconds after the game started, with the settings above (for testing)");

            try
            {
                var harmony = new HarmonyLib.Harmony(Guid);
                harmony.PatchAll(typeof(Plugin).Assembly);
                ActionSync.Install(harmony);
                SaveGuard.CleanLeftovers();
                Logger.LogInfo("conversation hooks installed");
            }
            catch (System.Exception ex) { Logger.LogError("conversation hooks could not be installed: " + ex); }

            // Unity only knows classes that were injected into its runtime first.
            ClassInjector.RegisterTypeInIl2Cpp<CoopBehaviour>();
            var host = new GameObject("NivalisCoop") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.AddComponent<CoopBehaviour>();

            Logger.LogInfo($"Nivalis Coop {NivalisCoop.Net.CoopClient.ModVersion} loaded, press {ToggleKey.Value} for the multiplayer window");
        }
    }
}
