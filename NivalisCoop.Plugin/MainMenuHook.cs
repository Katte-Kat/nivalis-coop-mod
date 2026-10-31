using System;
using Nivalis;
using Nivalis.Localization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NivalisCoop.Plugin
{
    /// <summary>
    /// Adds a "Multiplayer" entry to the game's own title menu by cloning the "Load" button: same look, same hover
    /// animation and sounds, but its own label and click. (The title screen is built from the objects the probe
    /// listed: Selection > LoadGameButton > [Image, Text(LocalizedStaticUILabel, TextMeshProUGUI)].)
    /// </summary>
    internal static class MainMenuHook
    {
        const string ButtonName = "MultiplayerButton";
        static float _next;
        static bool _failedOnce;

        public static void Tick()
        {
            if (Time.realtimeSinceStartup < _next) return;
            _next = Time.realtimeSinceStartup + 1f;
            try
            {
                var menu = UnityEngine.Object.FindObjectOfType<MainMenuUI>();
                if (menu == null || !menu.title || menu.loadButton == null) return;
                var parent = menu.loadButton.transform.parent;
                if (parent.Find(ButtonName) != null) return;
                AddButton(menu, parent);
            }
            catch (Exception ex)
            {
                if (_failedOnce) return;
                _failedOnce = true;
                Plugin.Logger.LogError("could not add the Multiplayer menu entry: " + ex);
            }
        }

        static void AddButton(MainMenuUI menu, Transform parent)
        {
            var source = menu.loadButton;
            var go = UnityEngine.Object.Instantiate(source.gameObject, parent);
            go.name = ButtonName;
            go.transform.SetSiblingIndex(source.transform.GetSiblingIndex() + 1);

            // the label is localized by a component that would put "Laden" back: remove it, then set our own text
            foreach (var loc in go.GetComponentsInChildren<LocalizedStaticUILabel>(true)) UnityEngine.Object.Destroy(loc);
            var label = go.GetComponentInChildren<TMP_Text>(true);
            if (label != null) label.text = "Multiplayer";

            // a fresh click event: a clone would keep whatever "Laden" does if that was set in the editor
            var button = go.GetComponent<Button>();
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(new Action(MultiplayerScreen.Open));
            button.interactable = true;
            Plugin.Logger.LogInfo("added the Multiplayer entry to the title menu");
        }
    }
}
