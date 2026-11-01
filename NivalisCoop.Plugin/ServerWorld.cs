using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Nivalis;
using Nivalis.InventorySystem;
using NivalisCoop.Net;
using UnityEngine;

namespace NivalisCoop.Plugin
{
    /// <summary>
    /// The server's worlds. A world is a savegame the server holds (a "slot", with versions). Playing one means
    /// downloading its newest .sav and loading it through the game's SerializationManager; a new world is a normal
    /// new game whose first save is uploaded as version 1. Everything that touches the game runs on Unity's thread
    /// (RunQueued), the transfers run in the background.
    /// </summary>
    internal static class ServerWorld
    {
        /// <summary>The world you are playing in, null outside of a server world (title screen, single player).</summary>
        public static string CurrentSlot
        {
            get => _currentSlot;
            private set
            {
                if (_currentSlot == value) return;
                _currentSlot = value;
                try { CoopHost.Client.JoinWorld(value); } catch (Exception ex) { Plugin.Logger.LogWarning("join_world: " + ex.Message); }
            }
        }

        static string _currentSlot;

        /// <summary>The slot the F8 buttons use when you are not in a world yet.</summary>
        public static string ActiveSlot => CurrentSlot ?? "main";

        static readonly ConcurrentQueue<Action> _main = new ConcurrentQueue<Action>();
        static readonly Dictionary<string, int> _base = new Dictionary<string, int>(); // slot -> version this game came from
        static volatile bool _busy;
        static volatile string _line = "";
        static float _confirmLoadUntil;
        static string _pendingNewWorld;
        static float _nextPendingTry;

        public static bool Busy => _busy;
        public static string Line => _line;
        public static bool HasPendingNewWorld => _pendingNewWorld != null;

        // the game's name for the file (no extension) and where it keeps it
        static string SaveName(string slot) => "COOP_" + slot;
        static string SavePath(string slot) => Path.Combine(Application.persistentDataPath, SaveName(slot) + ".sav");

        /// <summary>World names become slot names on the server: a-z, 0-9 and _, at most 24 characters.</summary>
        public static string Slugify(string name)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in (name ?? "").Trim().ToLowerInvariant())
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.Append(c);
                else if (c == ' ' || c == '_' || c == '-') sb.Append('_');
                if (sb.Length >= 24) break;
            }
            return sb.ToString().Trim('_');
        }

        public static void RunQueued()
        {
            while (_main.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { Plugin.Logger.LogError("server world: " + ex); _busy = false; _line = "Failed: " + ex.Message; }
            }
        }

        /// <summary>Two clicks within five seconds: loading replaces the game that is running right now.</summary>
        public static bool ConfirmLoad()
        {
            if (Time.realtimeSinceStartup < _confirmLoadUntil) { _confirmLoadUntil = 0; return true; }
            _confirmLoadUntil = Time.realtimeSinceStartup + 5f;
            return false;
        }

        public static bool AskingToConfirm => Time.realtimeSinceStartup < _confirmLoadUntil;

        /// <summary>Called every frame: uploads the first save of a freshly created world as soon as the game allows it.</summary>
        static float _deleteLoadedAt;
        static string _deleteLoaded;

        public static void Tick()
        {
            if (_deleteLoaded != null && Time.realtimeSinceStartup >= _deleteLoadedAt && !_busy)
            {
                SaveGuard.DeleteLocal(_deleteLoaded);
                _deleteLoaded = null;
            }
            if (_pendingNewWorld == null || _busy || Time.realtimeSinceStartup < _nextPendingTry) return;
            _nextPendingTry = Time.realtimeSinceStartup + 5f;
            var client = CoopHost.Client;
            if (client.State != ClientState.Connected) return;
            if (client.SaveOf(_pendingNewWorld) != null) { _pendingNewWorld = null; return; }
            // the new game must really be running (the intro and the character creation come first)
            if (!Singleton<SerializationManager>.InstanceExist() || UnityEngine.Object.FindObjectOfType<PlayerInventory>() == null) return;
            SaveToServer(_pendingNewWorld);
        }

        /// <summary>Game thread: write a save of the running game and store it on the server as the next version.</summary>
        public static void SaveToServer() => SaveToServer(ActiveSlot);

        static void SaveToServer(string slot)
        {
            var client = CoopHost.Client;
            if (_busy || client.State != ClientState.Connected) return;
            if (!Singleton<SerializationManager>.InstanceExist()) { _line = "No game is loaded"; return; }
            _busy = true;
            _line = "Saving the game...";
            if (CurrentSlot == slot && client.LeaderId != null && !client.IsLeader)
            {
                _busy = false;
                _line = "Only the session leader's game saves this world (it does that by itself)";
                return;
            }
            bool saved;
            SaveGuard.OwnSave = true;
            try { saved = Singleton<SerializationManager>.Instance.Save(SaveName(slot), false); }
            finally { SaveGuard.OwnSave = false; }
            if (!saved) { _busy = false; _line = "The game did not save (not possible right now?)"; return; }

            string path = SavePath(slot);
            _base.TryGetValue(slot, out int baseVersion);
            Plugin.Logger.LogInfo($"server world '{slot}': the game wrote {path}, uploading as a successor of version {baseVersion}");
            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(600); // the game writes a temp file and then replaces the real one
                    var info = await client.UploadSaveAsync(slot, path, baseVersion, "saved in game by " + client.Name,
                        p => _line = $"Uploading {p:P0}");
                    _base[slot] = info.Version;
                    if (CurrentSlot == null) CurrentSlot = slot;
                    if (_pendingNewWorld == slot) _pendingNewWorld = null;
                    _line = $"World '{slot}' stored on the server as version {info.Version} ({info.Size / 1e6:F1} MB)";
                    CoopHost.Say(_line);
                    _main.Enqueue(() => SaveGuard.DeleteLocal(SaveName(slot))); // the server has it now
                }
                catch (Exception ex) { _line = "Upload failed: " + ex.Message; }
                finally { _busy = false; }
                Plugin.Logger.LogInfo("server world: " + _line);
            });
        }

        /// <summary>Game thread: replace the running game by the server's newest save of the active world.</summary>
        public static void LoadFromServer() => PlayWorld(ActiveSlot);

        /// <summary>Game thread: download the world's newest save and load it.</summary>
        public static void PlayWorld(string slot)
        {
            var client = CoopHost.Client;
            if (_busy || client.State != ClientState.Connected) return;
            if (client.SaveOf(slot) == null) { _line = $"The server has no world '{slot}'"; return; }
            _busy = true;
            _line = $"Downloading world '{slot}'...";
            string dest = SavePath(slot);
            Task.Run(async () =>
            {
                try
                {
                    var got = await client.DownloadSaveAsync(slot, dest, null, p => _line = $"Downloading {p:P0}");
                    Plugin.Logger.LogInfo($"server world '{slot}': version {got.Version} by {got.Uploader} stored as {dest}");
                    _main.Enqueue(() =>
                    {
                        _base[slot] = got.Version;
                        _line = $"Loading world '{slot}' (version {got.Version})...";
                        CoopHost.WindowOpen = false;
                        MultiplayerScreen.Close();
                        CurrentSlot = slot;
                        WorldTime.OnWorldLoaded();
                        Singleton<SerializationManager>.Instance.Load(SaveName(slot));
                        _deleteLoadedAt = Time.realtimeSinceStartup + 90f; // the game reads it while the scene loads
                        _deleteLoaded = SaveName(slot);
                        _busy = false;
                        Plugin.Logger.LogInfo($"server world '{slot}': Load({SaveName(slot)}) called");
                    });
                }
                catch (Exception ex) { _line = "Download failed: " + ex.Message; _busy = false; Plugin.Logger.LogInfo("server world: " + _line); }
            });
        }

        /// <summary>Game thread: start the game's own "new game" and store its first save as version 1 of a new world.</summary>
        public static void CreateWorld(string slot)
        {
            var client = CoopHost.Client;
            if (_busy || client.State != ClientState.Connected) return;
            if (client.SaveOf(slot) != null) { _line = $"A world called '{slot}' already exists"; return; }
            var menu = UnityEngine.Object.FindObjectOfType<MainMenuUI>();
            if (menu == null) { _line = "A new world can be created from the title screen"; return; }
            CurrentSlot = slot;
            WorldTime.OnWorldLoaded();
            _base[slot] = 0;
            _pendingNewWorld = slot;
            _nextPendingTry = Time.realtimeSinceStartup + 20f; // the game needs a moment to leave the title screen
            _line = $"Creating world '{slot}': play through the start, it is stored on the server by itself";
            Plugin.Logger.LogInfo($"server world '{slot}': starting a new game");
            MultiplayerScreen.Close();
            menu.NewGameRequest();
        }

        /// <summary>You left the world (disconnect): nothing is shared any more.</summary>
        public static void LeaveWorld()
        {
            CurrentSlot = null;
            _pendingNewWorld = null;
        }
    }
}
