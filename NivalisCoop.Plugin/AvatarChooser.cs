using System;
using System.Collections.Generic;
using NivalisCoop.Net;
using UnityEngine;

namespace NivalisCoop.Plugin
{
    /// <summary>
    /// Choosing how the others see you: on the first login on a server (the server has no avatar for you yet) a window
    /// lists every people model of the game; "Look" puts the model in front of you, "Take it" stores the choice on the
    /// server, which hands it to everybody at each of your logins. The F8 window opens it again to change your mind.
    /// It waits until a world is loaded: only then has the game loaded its people models.
    /// </summary>
    internal static class AvatarChooser
    {
        public static bool IsOpen { get; private set; }
        static bool _askedThisLogin;
        static float _openAfter;
        static Vector2 _scroll;
        static string _filter = "";
        static string _looking;
        static GameObject _preview;
        static GUIStyle _title;

        public static void Open()
        {
            AvatarModels.Scan(true);
            if (!IsOpen) { _lock = Cursor.lockState; _cursorVisible = Cursor.visible; }
            IsOpen = true;
            ChatBox.Hold(true);
            Plugin.Logger.LogInfo($"avatar choice opened, {AvatarModels.Count} models");
        }

        static void Close()
        {
            IsOpen = false;
            ChatBox.Hold(false);
            TextInput.Unfocus();
            if (_preview != null) UnityEngine.Object.Destroy(_preview);
            _preview = null; _looking = null;
            Cursor.lockState = _lock; Cursor.visible = _cursorVisible;
        }

        static CursorLockMode _lock;
        static bool _cursorVisible;

        public static void OnEvent(CoopEvent ev)
        {
            if (ev is Disconnected) { _askedThisLogin = false; if (IsOpen) Close(); }
        }

        public static void Tick(CoopClient client)
        {
            if (client.State != ClientState.Connected || ServerWorld.CurrentSlot == null) { _openAfter = 0; return; }
            if (_openAfter == 0) _openAfter = Time.realtimeSinceStartup + 8f; // let the world finish loading
            if (!_askedThisLogin && client.Avatar == null && Time.realtimeSinceStartup >= _openAfter && AvatarModels.Names.Count > 0)
            {
                _askedThisLogin = true;
                Open();
            }
            if (IsOpen)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                if (_preview != null)
                {
                    var cam = Camera.main;
                    if (cam != null)
                    {
                        var fwd = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up).normalized;
                        _preview.transform.position = cam.transform.position + fwd * 2.2f - Vector3.up * 1.6f;
                        _preview.transform.rotation = Quaternion.LookRotation(-fwd, Vector3.up) * Quaternion.Euler(0, Mathf.Sin(Time.time) * 25f, 0);
                    }
                }
            }
        }

        public static void OnGUI()
        {
            if (!IsOpen) return;
            try { Draw(); }
            catch (Exception ex) { Plugin.Logger.LogError("avatar choice: " + ex); Close(); }
        }

        static void Draw()
        {
            _title ??= new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold };
            var client = CoopHost.Client;
            float w = 420, h = Mathf.Min(620, Screen.height - 60);
            var box = new Rect(Screen.width - w - 30, 30, w, h);
            GUI.Box(box, "");
            GUILayout.BeginArea(new Rect(box.x + 12, box.y + 10, w - 24, h - 20));
            GUILayout.Label("Choose your look", _title);
            GUILayout.Label(client.Avatar == null
                ? "This is how the other players see you on this server. You can change it later in the F8 window."
                : "Now: " + client.Avatar);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Search:", GUILayout.Width(60));
            _filter = TextInput.Field("avatar_filter", _filter, 40);
            GUILayout.EndHorizontal();

            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(h - 190));
            int shown = 0;
            foreach (var name in AvatarModels.Names)
            {
                if (_filter.Length > 0 && name.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                shown++;
                GUILayout.BeginHorizontal();
                GUILayout.Label((name == _looking ? "> " : "") + name);
                if (GUILayout.Button("Look", GUILayout.Width(60))) Look(name);
                GUILayout.EndHorizontal();
            }
            if (shown == 0) GUILayout.Label("No model matches.");
            GUILayout.EndScrollView();

            GUILayout.Label($"{AvatarModels.Count} models (more appear while you walk through the city)");
            GUILayout.BeginHorizontal();
            GUI.enabled = _looking != null;
            if (GUILayout.Button(_looking == null ? "Look at one first" : "Take " + _looking))
            {
                client.SetAvatar(_looking);
                CoopHost.Say("You are shown as " + _looking);
                Close();
            }
            GUI.enabled = true;
            if (GUILayout.Button(client.Avatar == null ? "Later" : "Close", GUILayout.Width(80))) Close();
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        static void Look(string name)
        {
            if (_preview != null) UnityEngine.Object.Destroy(_preview);
            _preview = AvatarModels.Build(name, "NivalisCoop_AvatarPreview");
            _looking = name;
            if (_preview == null) CoopHost.Say("This model cannot be shown right now");
        }
    }
}
