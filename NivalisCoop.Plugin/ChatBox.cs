using System;
using Nivalis;
using NivalisCoop.Net;
using UnityEngine;
using UnityEngine.InputSystem;

namespace NivalisCoop.Plugin
{
    /// <summary>
    /// The in-game chat, Minecraft style: Enter opens a line at the bottom, Enter sends, Esc closes. Messages of the
    /// last seconds stay visible at the bottom left, the whole history shows while the line is open. While you type
    /// the game's own controls are switched off (its menu input map), so the character does not walk away.
    /// Only active while you are connected and inside a server world.
    /// </summary>
    internal static class ChatBox
    {
        const string FieldId = "chat";
        const float VisibleSeconds = 10f;

        static bool _open;
        static float _releaseAt;
        static string _buffer = "";
        static bool _inputBlocked;
        static GUIStyle _line, _input;
        static int _styleHeight;

        public static bool IsOpen => _open;

        public static void OnGUI()
        {
            bool active = CoopHost.Client.State == ClientState.Connected && ServerWorld.CurrentSlot != null
                          && !MultiplayerScreen.IsOpen && !CoopHost.WindowOpen;
            if (!active)
            {
                if (_open) Close();
                return;
            }
            MakeStyles();
            var e = Event.current;

            if (!_open)
            {
                if (e != null && e.type == EventType.KeyDown && !TextInput.HasFocus)
                {
                    bool enter = e.keyCode == Plugin.ChatKey.Value || (e.keyCode == KeyCode.KeypadEnter && Plugin.ChatKey.Value == KeyCode.Return);
                    bool alt = Plugin.ChatKeyAlt.Value != KeyCode.None && e.keyCode == Plugin.ChatKeyAlt.Value;
                    // Enter is also the game's own "confirm" key: in menus and conversations it must stay the game's.
                    // The second key (T) works everywhere.
                    if ((enter && InGameplay()) || alt)
                    {
                        Open();
                        e.Use();
                    }
                }
            }
            else
            {
                _buffer = TextInput.Apply(FieldId, _buffer, 256);
                if (TextInput.TakeSubmitted(FieldId))
                {
                    string text = _buffer.Trim();
                    if (text.Length > 0) CoopHost.Client.SendChat(text);
                    Close();
                }
                else if (!TextInput.HasFocus) Close(); // Esc
            }
            Draw();
        }

        /// <summary>True while the character is free to walk around (the game's gameplay controls are the current map).</summary>
        static bool InGameplay()
        {
            try
            {
                if (_manager == null) _manager = UnityEngine.Object.FindObjectOfType<Nivalis.PlayerInputManager>();
                var map = _manager != null ? _manager.currentMap : null;
                return map != null && map.name == "GameplayControls" && !DialogueSharing.InConversation && DialogueSharing.SecondsSinceActivity > 0.7f;
            }
            catch (Exception) { return false; }
        }

        static void Open()
        {
            _open = true;
            _buffer = "";
            TextInput.Focus(FieldId);
            _releaseAt = 0;
            BlockGameInput(true);
        }

        public static void Close()
        {
            _open = false;
            _buffer = "";
            if (TextInput.HasFocus) TextInput.Unfocus();
            // keep the game blocked for a moment: the Enter or Esc press that closed the chat must not reach the game
            // (Esc would open its pause menu); Tick() gives the controls back afterwards
            _releaseAt = Time.realtimeSinceStartup + 0.35f;
        }

        // The game's controls are Input System actions. Switching its action map (SwitchMenuActionMap) is not enough:
        // the character kept walking. So the chat takes every enabled action away while it is open (remembering which
        // ones, to give them back exactly), and keeps doing so every frame in case the game switches something on again.
        static readonly Il2CppSystem.Collections.Generic.List<InputAction> _scratch = new Il2CppSystem.Collections.Generic.List<InputAction>();
        static readonly System.Collections.Generic.List<InputAction> _saved = new System.Collections.Generic.List<InputAction>();
        static Nivalis.PlayerInputManager _manager;
        static int _reEnabledLogged;
        static UnityEngine.InputSystem.InputActionMap _mapBefore;

        static string Names(Il2CppSystem.Collections.Generic.List<InputAction> list, int max = 40)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < list.Count && i < max; i++)
            {
                var a = list[i];
                sb.Append(i > 0 ? ", " : "").Append(a.actionMap != null ? a.actionMap.name : "?").Append('/').Append(a.name);
            }
            if (list.Count > max) sb.Append(", ... (").Append(list.Count).Append(" in total)");
            return sb.ToString();
        }

        static void BlockGameInput(bool block)
        {
            if (block == _inputBlocked) return;
            try
            {
                if (_manager == null) _manager = UnityEngine.Object.FindObjectOfType<Nivalis.PlayerInputManager>();
                if (block)
                {
                    _saved.Clear();
                    _scratch.Clear();
                    InputSystem.ListEnabledActions(_scratch);
                    for (int i = 0; i < _scratch.Count; i++) _saved.Add(_scratch[i]);
                    _mapBefore = _manager != null ? _manager.currentMap : null;
                    Plugin.Logger.LogInfo($"chat: blocking game input (current map '{(_mapBefore != null ? _mapBefore.name : "?")}'); {_scratch.Count} actions enabled before: {Names(_scratch)}");
                    if (_manager != null) { try { _manager.SwitchMenuActionMap(true); } catch (Exception ex) { Plugin.Logger.LogWarning("SwitchMenuActionMap(true): " + ex.Message); } }
                    DisableEverything("after the map switch");
                }
                else
                {
                    // Put back the map that was current when the chat opened: opened in a conversation that is the menu map,
                    // and SwitchMenuActionMap(false) would have switched to the gameplay controls and left the conversation
                    // without clicks (the answers did nothing).
                    if (_manager != null)
                    {
                        try
                        {
                            if (_mapBefore != null) _manager.SwitchActionMap(_mapBefore);
                            else _manager.SwitchMenuActionMap(false);
                        }
                        catch (Exception ex) { Plugin.Logger.LogWarning("restoring the input map: " + ex.Message); }
                    }
                    int back = 0;
                    foreach (var a in _saved)
                        try { a.Enable(); back++; } catch (Exception) { /* the action's owner is gone (scene change) */ }
                    _scratch.Clear();
                    InputSystem.ListEnabledActions(_scratch);
                    Plugin.Logger.LogInfo($"chat: input released; map '{(_manager != null && _manager.currentMap != null ? _manager.currentMap.name : "?")}', gave back {back} of {_saved.Count} actions, {_scratch.Count} enabled now");
                    _saved.Clear();
                }
                _inputBlocked = block;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning("could not switch the game's input for the chat: " + ex.Message);
                // never hold the game's controls forever because giving them back failed: let go and try to enable what we took
                if (!block)
                {
                    foreach (var a in _saved) { try { a.Enable(); } catch (Exception) { } }
                    _saved.Clear();
                    _inputBlocked = false;
                }
            }
        }

        static void DisableEverything(string when)
        {
            _scratch.Clear();
            InputSystem.ListEnabledActions(_scratch);
            if (_scratch.Count == 0) return;
            if (_reEnabledLogged < 6)
            {
                _reEnabledLogged++;
                Plugin.Logger.LogInfo($"chat: {_scratch.Count} actions still enabled {when}: {Names(_scratch)}");
            }
            for (int i = 0; i < _scratch.Count; i++)
                try { _scratch[i].Disable(); } catch (Exception) { }
        }

        /// <summary>Every frame: while the chat is open nothing of the game may listen to the keyboard.</summary>
        public static void Tick()
        {
            if (!_inputBlocked) return;
            try
            {
                if (_open) { DisableEverything("(the game switched them on again)"); return; }
                if (Time.realtimeSinceStartup >= _releaseAt) BlockGameInput(false);
                else DisableEverything("(waiting to hand the controls back)");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning("chat input guard: " + ex.Message); }
        }

        static void MakeStyles()
        {
            if (_styleHeight == Screen.height && _line != null) return;
            _styleHeight = Screen.height;
            float s = Screen.height / 1080f;
            _line = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(22 * s), alignment = TextAnchor.MiddleLeft, wordWrap = false };
            _input = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(24 * s), alignment = TextAnchor.MiddleLeft };
        }

        static void Draw()
        {
            float s = Screen.height / 1080f;
            float lineH = 30 * s, x = 16 * s, w = Math.Min(Screen.width * 0.6f, 1100 * s);
            float inputTop = Screen.height - 56 * s;
            float y = (_open ? inputTop - 8 * s : Screen.height - 24 * s) - lineH;
            int shown = 0;
            lock (CoopHost.Chat)
            {
                for (int i = CoopHost.Chat.Count - 1; i >= 0 && shown < (_open ? 14 : 8); i--)
                {
                    var (text, at) = CoopHost.Chat[i];
                    if (!_open && Time.realtimeSinceStartup - at > VisibleSeconds) break;
                    GUI.Box(new Rect(x - 6 * s, y, w, lineH), "");
                    GUI.Label(new Rect(x, y, w - 12 * s, lineH), text, _line);
                    y -= lineH + 2 * s;
                    shown++;
                }
            }
            if (!_open) return;
            var bar = new Rect(x - 6 * s, inputTop, w, 44 * s);
            GUI.Box(bar, "");
            string caret = ((int)(Time.realtimeSinceStartup * 2f) % 2 == 0) ? "|" : " ";
            GUI.Label(new Rect(x, inputTop, w - 12 * s, 44 * s), "> " + _buffer + caret, _input);
            GUI.Label(new Rect(x + w - 330 * s, inputTop, 320 * s, 44 * s), "Enter: send   Esc: close", _line);
        }
    }
}
