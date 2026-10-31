using System;
using UnityEngine;

namespace NivalisCoop.Plugin
{
    /// <summary>
    /// A text field made of a button plus the raw key events. IMGUI's own GUILayout.TextField is unusable in this game:
    /// it calls TextEditor.SaveBackup(), which Unity stripped from the build ("Method unstripping failed"), so every
    /// call threw and the rest of the window was never drawn. Buttons, labels and Event.current are not affected.
    /// Caveat: the game still sees the keys, so type while the pause menu is open, or use the config file.
    /// </summary>
    internal static class TextInput
    {
        static string _focus;
        static string _submitted;
        static bool _broken;

        public static bool Broken => _broken;

        public static void Unfocus() => _focus = null;

        public static bool HasFocus => _focus != null;

        public static void Focus(string id) => _focus = id;

        /// <summary>Feed the key events of this OnGUI call into <paramref name="text"/> (for a field you draw yourself).</summary>
        public static string Apply(string id, string text, int maxLength) => _broken ? text : ApplyKeys(id, text, maxLength);

        /// <summary>True once if Enter was pressed in the field with this id.</summary>
        public static bool TakeSubmitted(string id)
        {
            if (_submitted != id) return false;
            _submitted = null;
            return true;
        }

        public static string Field(string id, string text, int maxLength, bool secret = false)
        {
            text = text ?? "";
            bool focused = _focus == id;
            string shown = secret ? new string('*', text.Length) : text;
            if (shown.Length == 0 && !focused) shown = "(click to edit)";
            string caret = focused ? (((int)(Time.realtimeSinceStartup * 2f) % 2 == 0) ? "|" : " ") : "";
            if (GUILayout.Button(shown + caret))
                _focus = focused ? null : id;
            return focused && !_broken ? ApplyKeys(id, text, maxLength) : text;
        }

        /// <summary>Same field at an absolute position (the Multiplayer screen draws without layout groups).</summary>
        public static string FieldAt(Rect rect, string id, string text, int maxLength, GUIStyle style, string placeholder = "", bool secret = false)
        {
            text = text ?? "";
            bool focused = _focus == id;
            string shown = secret ? new string('*', text.Length) : text;
            if (shown.Length == 0 && !focused) shown = placeholder.Length > 0 ? placeholder : "(click to edit)";
            string caret = focused ? (((int)(Time.realtimeSinceStartup * 2f) % 2 == 0) ? "|" : " ") : "";
            if (GUI.Button(rect, shown + caret, style))
                _focus = focused ? null : id;
            return focused && !_broken ? ApplyKeys(id, text, maxLength) : text;
        }

        static string ApplyKeys(string id, string text, int maxLength)
        {
            try
            {
                var e = Event.current;
                if (e == null || e.type != EventType.KeyDown) return text;
                KeyCode key = e.keyCode;
                if (key == KeyCode.Return || key == KeyCode.KeypadEnter) { _submitted = id; e.Use(); }
                else if (key == KeyCode.Escape) { _focus = null; e.Use(); }
                else if (key == KeyCode.Backspace) { if (text.Length > 0) text = text.Substring(0, text.Length - 1); e.Use(); }
                else if (key == KeyCode.V && (e.control || e.command)) { text = Append(text, Clipboard(), maxLength); e.Use(); }
                else if (e.character != '\0' && !char.IsControl(e.character)) { text = Append(text, e.character.ToString(), maxLength); e.Use(); }
            }
            catch (Exception ex)
            {
                _broken = true; // do not throw every frame: fall back to "edit the config file"
                Plugin.Logger.LogWarning("keyboard input in the window is unavailable: " + ex.Message);
            }
            return text;
        }

        static string Append(string text, string extra, int maxLength)
        {
            if (string.IsNullOrEmpty(extra)) return text;
            string joined = text + extra.Replace("\r", "").Replace("\n", "");
            return joined.Length <= maxLength ? joined : joined.Substring(0, maxLength);
        }

        static string Clipboard()
        {
            try { return GUIUtility.systemCopyBuffer; }
            catch (Exception) { return ""; } // pasting is a convenience, typing must keep working without it
        }
    }
}
