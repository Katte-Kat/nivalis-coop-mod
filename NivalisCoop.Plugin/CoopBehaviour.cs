using System;
using NivalisCoop.Net;
using UnityEngine;
using UnityEngine.SceneManagement;
using Pose = NivalisCoop.Net.Pose; // UnityEngine.Pose exists too

namespace NivalisCoop.Plugin
{
    /// <summary>The one Unity component of the mod: polls the network once per frame and draws the window and the name tags.</summary>
    public class CoopBehaviour : MonoBehaviour
    {
        public CoopBehaviour(IntPtr ptr) : base(ptr) { }

        // window input lives in statics, see CoopHost
        static string _address, _name, _password, _chatInput = "";
        static bool _cursorSaved;
        static CursorLockMode _lock;
        static bool _cursorVisible;
        static GUIStyle _tag;
        static bool _loggedTag;

        static bool _loggedFirstUpdate, _loggedFirstPose, _autoConnectDone;

        void Update()
        {
            if (!_loggedFirstUpdate)
            {
                _loggedFirstUpdate = true;
                Plugin.Logger.LogInfo("Update loop of the injected component is running");
            }
            if (Plugin.AutoConnect.Value && !_autoConnectDone && Time.realtimeSinceStartup > 5f)
            {
                _autoConnectDone = true;
                CoopHost.ConnectAsync(Plugin.Address.Value, Plugin.PlayerName.Value, Plugin.Password.Value);
            }
            if (Hotkeys.Pressed(Plugin.ToggleKey.Value)) ToggleWindow();

            var client = CoopHost.Client;
            client.Poll(CoopHost.OnEvent);
            ServerWorld.RunQueued();
            ChatBox.Tick();
            DialogueSharing.Tick();
            WorldTime.Tick();
            MainMenuHook.Tick();
            ServerWorld.Tick();
            MoneySync.Tick(client);
            ObjectSync.Tick(client);
            RemoteAvatars.Tick(client);
            NpcSync.Tick(client);
            SaveGuard.Tick();
            AvatarChooser.Tick(client);
            VenueSync.Tick(client);
            InventorySync.Tick(client);
            if (client.State == ClientState.Connected && LocalPose.TryGet(out var pose))
            {
                if (!_loggedFirstPose)
                {
                    _loggedFirstPose = true;
                    Plugin.Logger.LogInfo($"sending poses, first one: ({pose.X:F1}, {pose.Y:F1}, {pose.Z:F1}) in scene '{pose.Scene}'");
                }
                client.SendMove(pose); // throttled inside the client
            }
        }

        void LateUpdate()
        {
            try { NpcSync.LateTick(); }
            catch (Exception ex) { if (!_loggedLate) { _loggedLate = true; Plugin.Logger.LogError("people (late update): " + ex); } }
        }

        static bool _loggedLate;

        void ToggleWindow()
        {
            CoopHost.WindowOpen = !CoopHost.WindowOpen;
            if (CoopHost.WindowOpen)
            {
                _address ??= Plugin.Address.Value;
                _name ??= Plugin.PlayerName.Value;
                _password ??= Plugin.Password.Value;
                _lock = Cursor.lockState; _cursorVisible = Cursor.visible; _cursorSaved = true;
                CoopHost.PingAsync(_address);
            }
            else if (_cursorSaved)
            {
                TextInput.Unfocus();
                Plugin.Address.Value = _address; Plugin.PlayerName.Value = _name; Plugin.Password.Value = _password;
                Cursor.lockState = _lock; Cursor.visible = _cursorVisible;
            }
        }

        static bool _loggedOnGui;

        void OnGUI()
        {
            if (!_loggedOnGui)
            {
                _loggedOnGui = true;
                Plugin.Logger.LogInfo("OnGUI is being called");
            }
            MultiplayerScreen.OnGUI();
            _tag ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            DrawNameTags();
            DialogueSharing.OnGUI();
            ChatBox.OnGUI();
            AvatarChooser.OnGUI();
            if (CoopHost.WindowOpen) DrawWindow();
        }

        // other players ---------------------------------------------------
        // The pose is the camera of the other player, so the tag goes just above their head; the body is RemoteAvatars.
        static void DrawNameTags()
        {
            var cam = Camera.main;
            if (cam == null || CoopHost.Client.State != ClientState.Connected) return;
            string scene = LocalPose.SceneKey();
            double now = Time.realtimeSinceStartupAsDouble;
            foreach (var player in CoopHost.Client.Players.Values)
            {
                if (!CoopHost.Interpolator.TryGet(player.Id, now, out var pose) || !LocalPose.SameScene(pose.Scene, scene)) continue;
                var screen = cam.WorldToScreenPoint(new Vector3(pose.X, pose.Y + 0.3f, pose.Z));
                if (screen.z <= 0) continue; // behind the camera
                if (!_loggedTag)
                {
                    _loggedTag = true;
                    Plugin.Logger.LogInfo($"name tag for {player.Name} drawn at screen ({screen.x:F0}, {screen.y:F0}), distance {screen.z:F1}");
                }
                GUI.Label(new Rect(screen.x - 90, Screen.height - screen.y - 12, 180, 24), player.Name, _tag);
            }
        }

        // window ----------------------------------------------------------
        static void DrawServerWorld(CoopClient client)
        {
            GUILayout.Space(8);
            var world = client.SaveOf(ServerWorld.ActiveSlot);
            GUILayout.Label(world == null
                ? "Server world: none yet"
                : $"Server world: version {world.Version} by {world.Uploader}, {world.Size / 1e6:F1} MB");
            GUI.enabled = !ServerWorld.Busy;
            if (world != null)
            {
                string label = ServerWorld.AskingToConfirm ? "Really? This replaces your running game - click again" : "Load the server world";
                if (GUILayout.Button(label) && ServerWorld.ConfirmLoad()) ServerWorld.LoadFromServer();
            }
            if (GUILayout.Button(world == null ? "Make my current game the server world" : "Save my game to the server"))
                ServerWorld.SaveToServer();
            GUI.enabled = true;
            if (ServerWorld.Line.Length > 0) GUILayout.Label(ServerWorld.Line);
        }

        static bool _loggedDrawError;

        static void DrawWindow()
        {
            try { DrawWindowCore(); }
            catch (Exception ex)
            {
                if (_loggedDrawError) return;
                _loggedDrawError = true;
                Plugin.Logger.LogError("the window could not be drawn: " + ex);
            }
        }

        static void DrawWindowCore()
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            var client = CoopHost.Client;
            var box = new Rect(20, 20, 400, 600);
            GUI.Box(box, "Nivalis Coop " + CoopClient.ModVersion);
            GUILayout.BeginArea(new Rect(box.x + 10, box.y + 28, box.width - 20, box.height - 38));

            if (client.State == ClientState.Disconnected)
            {
                GUILayout.Label("Server (host:port)");
                _address = TextInput.Field("address", _address, 100);
                GUILayout.Label("Your name");
                _name = TextInput.Field("name", _name, 24);
                GUILayout.Label("Password (if the server has one)");
                _password = TextInput.Field("password", _password, 64, secret: true);
                GUILayout.BeginHorizontal();
                GUI.enabled = !CoopHost.Busy;
                if (GUILayout.Button("Connect")) CoopHost.ConnectAsync(_address, _name, _password);
                if (GUILayout.Button("Ping")) CoopHost.PingAsync(_address);
                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }
            else
            {
                GUILayout.Label($"Players ({client.Players.Count + 1}):");
                GUILayout.Label("  " + client.Name + " (you)");
                foreach (var p in client.Players.Values) GUILayout.Label("  " + p.Name);
                if (GUILayout.Button("Disconnect")) client.Disconnect();
            }

            GUILayout.Space(8);
            GUILayout.Label(CoopHost.Status);

            if (client.State == ClientState.Connected)
            {
                GUILayout.Space(8);
                Plugin.SyncMoney.Value = GUILayout.Toggle(Plugin.SyncMoney.Value, "Shared money (all players use one account)");
                if (MoneySync.Line.Length > 0) GUILayout.Label(MoneySync.Line);
                if (WorldTime.Line.Length > 0) GUILayout.Label(WorldTime.Line);
                if (ObjectSync.Line.Length > 0) GUILayout.Label(ObjectSync.Line);
                if (NpcSync.Line.Length > 0) GUILayout.Label(NpcSync.Line);
                if (ActionSync.Line.Length > 0) GUILayout.Label(ActionSync.Line);
                if (VenueSync.Line.Length > 0) GUILayout.Label(VenueSync.Line);
                if (InventorySync.Line.Length > 0) GUILayout.Label(InventorySync.Line);
                if (GUILayout.Button("Your look: " + (client.Avatar ?? "not chosen") + " (change)")) { CoopHost.WindowOpen = false; AvatarChooser.Open(); }
                DrawServerWorld(client);
                GUILayout.Space(4);
                GUILayout.Label("Chat (click the line, type, Enter sends):");
                _chatInput = TextInput.Field("chat", _chatInput, 200);
                if ((TextInput.TakeSubmitted("chat") || GUILayout.Button("Send")) && _chatInput.Trim().Length > 0)
                {
                    client.SendChat(_chatInput.Trim());
                    _chatInput = "";
                }
            }
            GUILayout.EndArea();
        }
    }

    internal static class LocalPose
    {
        /// <summary>The scene prefixed with the world: players in different worlds never see each other.</summary>
        public static string SceneKey()
        {
            string scene = SceneManager.GetActiveScene().name;
            // the game's own scene number of the people around us goes along, so the session leader knows which part
            // of the city to send to us
            return ServerWorld.CurrentSlot == null ? scene : ServerWorld.CurrentSlot + "|" + scene + "#" + NpcSync.LocalGhostScene;
        }

        /// <summary>Same world and Unity scene (the people-scene number after '#' does not count).</summary>
        public static bool SameScene(string a, string b)
        {
            int ia = a.LastIndexOf('#'), ib = b.LastIndexOf('#');
            return string.CompareOrdinal(ia >= 0 ? a.Substring(0, ia) : a, ib >= 0 ? b.Substring(0, ib) : b) == 0;
        }

        public static int GhostSceneOf(string sceneKey)
        {
            int hash = sceneKey.LastIndexOf('#');
            return hash >= 0 && int.TryParse(sceneKey.Substring(hash + 1), out var n) ? n : -1;
        }

        /// <summary>
        /// Where this player is. Phase 1 uses the main camera so it works without knowing the game's player class;
        /// the walking animation and the exact character position come with the game-specific hooks.
        /// </summary>
        public static bool TryGet(out Pose pose)
        {
            pose = default;
            var cam = Camera.main;
            if (cam == null) return false;
            var t = cam.transform;
            var p = t.position;
            pose = new Pose(p.x, p.y, p.z, t.eulerAngles.y, "idle", SceneKey());
            return true;
        }
    }

    internal static class Hotkeys
    {
        static bool _legacyBroken;

        /// <summary>F1-F12 through the legacy Input class, or through the Input System if the game disabled the legacy one.</summary>
        public static bool Pressed(KeyCode key)
        {
            if (!_legacyBroken)
            {
                try { return Input.GetKeyDown(key); }
                catch (Exception) { _legacyBroken = true; } // "Input System only" projects throw here
            }
#if HAS_INPUTSYSTEM
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard == null || key < KeyCode.F1 || key > KeyCode.F12) return false;
            var mapped = (UnityEngine.InputSystem.Key)((int)UnityEngine.InputSystem.Key.F1 + (int)key - (int)KeyCode.F1);
            return keyboard[mapped].wasPressedThisFrame;
#else
            return false;
#endif
        }
    }
}
