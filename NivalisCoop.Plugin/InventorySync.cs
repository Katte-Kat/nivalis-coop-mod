using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Nivalis.InventorySystem;
using NivalisCoop.Net;
using UnityEngine;

namespace NivalisCoop.Plugin
{
    /// <summary>
    /// Every player has their own inventory in a server world. The world save is the session leader's, so it holds the
    /// leader's things; everybody's own inventory is kept by the server per player and world (pdata "inv": a list of
    /// [item, count]). After the world is loaded the game's inventory is replaced by yours (a new player in a world
    /// starts with an empty one, the first player keeps what the save had), and every change is sent back.
    /// Money is not in here: that is the one shared account (MoneySync).
    /// </summary>
    internal static class InventorySync
    {
        const string Key = "inv";
        static JsonArray _stored;
        static bool _haveData, _applied;
        static string _lastSent;
        static float _applyAt, _nextCheck;
        static readonly Dictionary<string, ItemType> _types = new Dictionary<string, ItemType>();

        public static string Line { get; private set; } = "";

        public static void OnEvent(CoopEvent ev)
        {
            switch (ev)
            {
                case PlayerDataReceived pd when pd.World == ServerWorld.CurrentSlot:
                    _stored = pd.Data[Key] as JsonArray;
                    _haveData = true; _applied = false; _lastSent = null;
                    _applyAt = Time.realtimeSinceStartup + 6f; // the world must be loaded first
                    break;
                case Disconnected _:
                    _haveData = false; _applied = false; _stored = null; Line = "";
                    break;
            }
        }

        /// <summary>A new world is being loaded: wait for it before putting your things in.</summary>
        public static void OnWorldLoaded() { _applied = false; _applyAt = Time.realtimeSinceStartup + 12f; }

        public static void Tick(CoopClient client)
        {
            if (client.State != ClientState.Connected || ServerWorld.CurrentSlot == null || !_haveData || client.LeaderId == null) return;
            float now = Time.realtimeSinceStartup;
            if (now < _applyAt) return;
            var inv = UnityEngine.Object.FindObjectOfType<PlayerInventory>();
            if (inv == null || inv.Items == null) { _applyAt = now + 2f; return; }

            if (!_applied)
            {
                _applied = true;
                try
                {
                    if (_stored != null) { Put(inv.Items, _stored); Line = $"Inventory: yours ({_stored.Count} kinds of things)"; }
                    else if (!client.IsLeader) { inv.Items.Clear(); Line = "Inventory: yours (new here, empty)"; }
                    else Line = "Inventory: yours (from the world save)";
                    Plugin.Logger.LogInfo(Line);
                }
                catch (Exception ex) { Plugin.Logger.LogError("inventory could not be set: " + ex); }
                _nextCheck = now + 2f;
                return;
            }

            if (now < _nextCheck) return;
            _nextCheck = now + 3f;
            try
            {
                var list = Take(inv.Items);
                string json = list.ToJsonString();
                if (json == _lastSent) return;
                _lastSent = json;
                _stored = list;
                client.SetPlayerData(Key, list);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning("inventory: " + ex.Message); }
        }

        static JsonArray Take(ItemContainer items)
        {
            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var stack in items._items) // the IL2CPP interface enumerator cannot be walked from C#, the list can
            {
                if (stack?.Type == null) continue;
                string name = stack.Type.name;
                counts[name] = (counts.TryGetValue(name, out var c) ? c : 0) + stack.StackCount;
            }
            var list = new JsonArray();
            foreach (var kv in counts) list.Add(new JsonArray(kv.Key, kv.Value));
            return list;
        }

        static void Put(ItemContainer items, JsonArray list)
        {
            items.Clear();
            foreach (var e in list)
            {
                string name = e[0].GetValue<string>();
                int count = (int)e[1].GetValue<double>();
                var type = FindType(name);
                if (type == null) { Plugin.Logger.LogWarning($"inventory: item {name} is not in this game"); continue; }
                items.TryCreateInInventory(type, count, 0);
            }
        }

        static ItemType FindType(string name)
        {
            if (_types.TryGetValue(name, out var t) && t != null) return t;
            foreach (var it in Resources.FindObjectsOfTypeAll<ItemType>()) if (it != null && it.name == name) return _types[name] = it;
            return null;
        }
    }
}
