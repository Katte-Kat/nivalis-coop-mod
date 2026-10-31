using System;
using System.Linq;
using Nivalis;
using NivalisCoop.Net;
using UnityEngine;

namespace NivalisCoop.Plugin
{
    /// <summary>
    /// The "Multiplayer" screen behind the new main menu entry, laid out like Minecraft's: a server list (name, players,
    /// ping, Join), and after joining the worlds the server holds (Play) plus "New world". Drawn with plain IMGUI
    /// rectangles: layout groups, text fields and options are not reliable in this build (see TextInput).
    /// </summary>
    internal static class MultiplayerScreen
    {
        enum View { Servers, Worlds }

        static View _view = View.Servers;
        static string _name, _addName = "", _addAddress = "", _addPassword = "", _newWorld = "";
        static string _message = "";
        static float _nextPing;
        static bool _drawFailed;
        static GUIStyle _title, _text, _small, _button, _field;
        static int _styleHeight;

        public static bool IsOpen { get; private set; }

        public static void Open()
        {
            IsOpen = true;
            _name = _name ?? Plugin.PlayerName.Value;
            _view = CoopHost.Client.State == ClientState.Connected ? View.Worlds : View.Servers;
            _message = "";
            ServerBook.Load();
            ServerBook.PingAll();
            _nextPing = Time.realtimeSinceStartup + 10f;
            BlockMenu(true);
        }

        public static void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            TextInput.Unfocus();
            BlockMenu(false);
        }

        // the uGUI menu below must not get the clicks that belong to this screen
        static void BlockMenu(bool block)
        {
            try
            {
                var menu = UnityEngine.Object.FindObjectOfType<MainMenuUI>();
                if (menu == null || menu.menuCanvasGroup == null) return;
                menu.menuCanvasGroup.blocksRaycasts = !block;
                menu.menuCanvasGroup.interactable = !block;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning("could not (un)block the main menu: " + ex.Message); }
        }

        public static void OnGUI()
        {
            if (!IsOpen) return;
            try { Draw(); }
            catch (Exception ex)
            {
                if (_drawFailed) return;
                _drawFailed = true;
                Plugin.Logger.LogError("the multiplayer screen could not be drawn: " + ex);
            }
        }

        static void MakeStyles()
        {
            if (_styleHeight == Screen.height && _title != null) return;
            _styleHeight = Screen.height;
            float s = Screen.height / 1080f;
            _title = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(40 * s), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            _text = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(26 * s), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
            _small = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(19 * s), alignment = TextAnchor.MiddleLeft };
            _button = new GUIStyle(GUI.skin.button) { fontSize = Mathf.RoundToInt(22 * s) };
            _field = new GUIStyle(GUI.skin.button) { fontSize = Mathf.RoundToInt(20 * s), alignment = TextAnchor.MiddleLeft };
        }

        static void Draw()
        {
            MakeStyles();
            var e = Event.current;
            if (e != null && e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape && !TextInput.HasFocus) { Back(); e.Use(); return; }
            if (Time.realtimeSinceStartup > _nextPing && _view == View.Servers) { _nextPing = Time.realtimeSinceStartup + 10f; ServerBook.PingAll(); }

            float s = Screen.height / 1080f;
            var panel = new Rect((Screen.width - 1000 * s) / 2f, 90 * s, 1000 * s, 900 * s);
            GUI.Box(new Rect(0, 0, Screen.width, Screen.height), "");
            GUI.Box(panel, "");
            float x = panel.x + 30 * s, w = panel.width - 60 * s, y = panel.y + 20 * s;

            GUI.Label(new Rect(x, y, w, 60 * s), "Multiplayer", _title);
            y += 70 * s;
            if (_view == View.Servers) DrawServers(x, y, w, panel, s); else DrawWorlds(x, y, w, panel, s);

            string status = _message.Length > 0 ? _message : (ServerWorld.Line.Length > 0 ? ServerWorld.Line : CoopHost.Status);
            GUI.Label(new Rect(x, panel.yMax - 50 * s, w - 180 * s, 40 * s), status, _small);
            if (GUI.Button(new Rect(panel.xMax - 190 * s, panel.yMax - 55 * s, 160 * s, 42 * s), _view == View.Worlds ? "Disconnect" : "Back", _button)) Back();
        }

        static void Back()
        {
            if (_view == View.Worlds)
            {
                CoopHost.Client.Disconnect();
                ServerWorld.LeaveWorld();
                _view = View.Servers;
                ServerBook.PingAll();
            }
            else Close();
        }

        // ---------------------------------------------------------------- server list
        static void DrawServers(float x, float y, float w, Rect panel, float s)
        {
            GUI.Label(new Rect(x, y, 200 * s, 40 * s), "Your name:", _small);
            _name = TextInput.FieldAt(new Rect(x + 130 * s, y, 360 * s, 38 * s), "name", _name, 24, _field);
            y += 56 * s;

            float row = 84 * s;
            var entries = ServerBook.Entries;
            for (int i = 0; i < entries.Count && i < 6; i++)
            {
                var entry = entries[i];
                var r = new Rect(x, y + i * (row + 8 * s), w, row);
                GUI.Box(r, "");
                GUI.Label(new Rect(r.x + 14 * s, r.y + 4 * s, w - 300 * s, 36 * s), entry.Name + "   (" + entry.Address + ")", _text);
                GUI.Label(new Rect(r.x + 14 * s, r.y + 42 * s, w - 300 * s, 34 * s), StatusLine(entry), _small);
                if (GUI.Button(new Rect(r.xMax - 270 * s, r.y + 20 * s, 150 * s, 44 * s), "Join", _button)) Join(entry);
                if (GUI.Button(new Rect(r.xMax - 108 * s, r.y + 20 * s, 94 * s, 44 * s), "Remove", _button)) { ServerBook.Remove(entry); break; }
            }

            float ay = panel.yMax - 230 * s;
            GUI.Label(new Rect(x, ay, w, 36 * s), "Add a server", _text);
            ay += 44 * s;
            _addName = TextInput.FieldAt(new Rect(x, ay, 250 * s, 40 * s), "addname", _addName, 32, _field, "Name");
            _addAddress = TextInput.FieldAt(new Rect(x + 260 * s, ay, 300 * s, 40 * s), "addaddr", _addAddress, 100, _field, "host:port");
            _addPassword = TextInput.FieldAt(new Rect(x + 570 * s, ay, 190 * s, 40 * s), "addpw", _addPassword, 64, _field, "password", secret: true);
            if (GUI.Button(new Rect(x + 770 * s, ay, 170 * s, 40 * s), "Add", _button) && _addAddress.Trim().Length > 0)
            {
                ServerBook.Add(_addName, _addAddress, _addPassword);
                _addName = _addAddress = _addPassword = "";
                ServerBook.PingAll();
            }
        }

        static string StatusLine(ServerEntry e)
        {
            if (e.Status != null)
            {
                var st = e.Status;
                return $"{st.Name}: {st.Motd}  |  {st.Online}/{st.Max} players  |  {st.LatencyMs} ms" + (st.PasswordRequired ? "  |  password" : "");
            }
            if (e.Pinging) return "Pinging...";
            return e.Error != null ? "No answer: " + e.Error : "Not pinged yet";
        }

        static void Join(ServerEntry entry)
        {
            if (CoopHost.Busy) return;
            if (_name.Trim().Length < 2) { _message = "Enter your name first (at least 2 characters)"; return; }
            Plugin.PlayerName.Value = _name.Trim();
            _message = "Connecting to " + entry.Name + "...";
            CoopHost.ConnectAsync(entry.Address, _name.Trim(), entry.Password, joined =>
            {
                if (joined) { _message = ""; _view = View.Worlds; }
                else _message = CoopHost.Status;
            });
        }

        // ---------------------------------------------------------------- worlds of the server
        static void DrawWorlds(float x, float y, float w, Rect panel, float s)
        {
            var client = CoopHost.Client;
            if (client.State != ClientState.Connected) { _view = View.Servers; return; }
            GUI.Label(new Rect(x, y, w, 40 * s), $"Connected as {client.Name}  |  {client.Players.Count + 1} player(s) on this server", _small);
            y += 46 * s;
            GUI.Label(new Rect(x, y, w, 36 * s), "Worlds on this server", _text);
            y += 46 * s;

            float row = 78 * s;
            var worlds = client.Saves;
            if (worlds.Count == 0) GUI.Label(new Rect(x, y, w, 36 * s), "No world yet. Create the first one below.", _small);
            for (int i = 0; i < worlds.Count && i < 6; i++)
            {
                var wd = worlds[i];
                var r = new Rect(x, y + i * (row + 8 * s), w, row);
                GUI.Box(r, "");
                GUI.Label(new Rect(r.x + 14 * s, r.y + 4 * s, w - 220 * s, 34 * s), wd.Slot, _text);
                string when = DateTimeOffset.FromUnixTimeSeconds((long)wd.Ts).LocalDateTime.ToString("g");
                GUI.Label(new Rect(r.x + 14 * s, r.y + 40 * s, w - 220 * s, 32 * s), $"version {wd.Version} by {wd.Uploader}  |  {wd.Size / 1e6:F1} MB  |  {when}", _small);
                GUI.enabled = !ServerWorld.Busy;
                if (GUI.Button(new Rect(r.xMax - 190 * s, r.y + 16 * s, 170 * s, 46 * s), "Play", _button)) ServerWorld.PlayWorld(wd.Slot);
                GUI.enabled = true;
            }

            float ay = panel.yMax - 200 * s;
            GUI.Label(new Rect(x, ay, w, 36 * s), "New world", _text);
            ay += 44 * s;
            _newWorld = TextInput.FieldAt(new Rect(x, ay, 600 * s, 40 * s), "newworld", _newWorld, 24, _field, "name of the world");
            string slot = ServerWorld.Slugify(_newWorld);
            GUI.enabled = !ServerWorld.Busy && slot.Length > 0;
            if (GUI.Button(new Rect(x + 620 * s, ay, 320 * s, 40 * s), "Create (starts a new game)", _button))
            {
                _message = "";
                ServerWorld.CreateWorld(slot);
                _newWorld = "";
            }
            GUI.enabled = true;
            if (slot.Length > 0 && slot != _newWorld.Trim())
                GUI.Label(new Rect(x, ay + 44 * s, w, 30 * s), $"will be called '{slot}' on the server", _small);
        }
    }
}
