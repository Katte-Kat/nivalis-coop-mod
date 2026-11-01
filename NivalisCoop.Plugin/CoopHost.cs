using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BepInEx;
using NivalisCoop.Net;
using UnityEngine;

namespace NivalisCoop.Plugin
{
    /// <summary>
    /// Everything the mod knows about its connection. Static on purpose: the injected MonoBehaviour only
    /// forwards Update/OnGUI, because instance fields of injected classes are not reliable under IL2CPP.
    /// </summary>
    internal static class CoopHost
    {
        public static readonly CoopClient Client = new CoopClient();
        public static readonly PoseInterpolator Interpolator = new PoseInterpolator();
        public static readonly List<(string Text, float At)> Chat = new List<(string, float)>();

        static volatile string _status = "Not connected";

        /// <summary>One line for the window, also written to the BepInEx log.</summary>
        public static string Status
        {
            get => _status;
            set { _status = value; Plugin.Logger?.LogInfo(value); }
        }
        public static volatile ServerStatus LastPing;
        public static bool WindowOpen;

        static volatile bool _busy;
        static bool _loggedMove;

        public static bool Busy => _busy;

        public static (string Host, int Port) ParseAddress(string address)
        {
            address = (address ?? "").Trim();
            int colon = address.LastIndexOf(':');
            if (colon > 0 && int.TryParse(address.Substring(colon + 1), out int port))
                return (address.Substring(0, colon), port);
            return (address.Length > 0 ? address : "127.0.0.1", 27800);
        }

        /// <summary>Server-list ping, shown in the window before you join.</summary>
        public static async void PingAsync(string address)
        {
            if (_busy) return;
            _busy = true;
            try
            {
                var (host, port) = ParseAddress(address);
                LastPing = await CoopClient.PingAsync(host, port);
                Status = $"{LastPing.Name}: {LastPing.Online}/{LastPing.Max} online, {LastPing.LatencyMs} ms" +
                         (LastPing.PasswordRequired ? ", password" : "");
            }
            catch (Exception ex) { LastPing = null; Status = "No answer: " + ex.Message; }
            finally { _busy = false; }
        }

        public static async void ConnectAsync(string address, string name, string password, Action<bool> done = null)
        {
            if (_busy || Client.State != ClientState.Disconnected) return;
            _busy = true;
            Status = "Connecting...";
            bool joined = false;
            try
            {
                var (host, port) = ParseAddress(address);
                string key = host + ":" + port;
                var result = await Client.ConnectAsync(host, port, name, Tokens.Get(key),
                    string.IsNullOrEmpty(password) ? null : password, "Nivalis Nights");
                if (!result.Ok) { Status = "Refused: " + result.Reason; return; }
                joined = true;
                if (ServerWorld.CurrentSlot != null) Client.JoinWorld(ServerWorld.CurrentSlot);
                if (result.NewToken != null) Tokens.Set(key, result.NewToken);
                Status = $"Connected to {result.ServerName} as {result.Name}";
                Say($"Connected. {result.Motd}");
            }
            catch (Exception ex) { Status = "Failed: " + ex.Message; }
            finally
            {
                _busy = false;
                try { done?.Invoke(joined); } catch (Exception ex) { Plugin.Logger.LogError(ex); }
            }
        }

        public static void Say(string text)
        {
            Plugin.Logger?.LogInfo("[chat] " + text);
            lock (Chat)
            {
                Chat.Add((text, Time.realtimeSinceStartup));
                if (Chat.Count > 50) Chat.RemoveAt(0);
            }
        }

        /// <summary>Runs on the game thread (CoopClient.Poll). The only place where network events meet Unity.</summary>
        public static void OnEvent(CoopEvent ev)
        {
            double now = Time.realtimeSinceStartupAsDouble;
            MoneySync.OnEvent(ev);
            DialogueSharing.OnEvent(ev);
            WorldTime.OnEvent(ev);
            ObjectSync.OnEvent(ev);
            NpcSync.OnEvent(ev);
            AvatarChooser.OnEvent(ev);
            ActionSync.OnEvent(ev);
            VenueSync.OnEvent(ev);
            InventorySync.OnEvent(ev);
            WorldObjects.OnEvent(ev);
            switch (ev)
            {
                case PlayerJoined j:
                    Interpolator.Add(j.Id, now, j.Pose);
                    Say($"{j.Name} joined");
                    break;
                case PlayerLeft l:
                    Interpolator.Remove(l.Id);
                    Say($"{l.Name} left");
                    break;
                case PlayersMoved m:
                    foreach (var kv in m.Poses) Interpolator.Add(kv.Key, now, kv.Value);
                    if (!_loggedMove)
                    {
                        _loggedMove = true;
                        foreach (var kv in m.Poses)
                            Plugin.Logger.LogInfo($"first position update: player {kv.Key} at ({kv.Value.X:F1}, {kv.Value.Y:F1}, {kv.Value.Z:F1}) in '{kv.Value.Scene}'");
                    }
                    break;
                case SaveUpdated u:
                    Say($"{u.Save.Uploader} saved the server world (version {u.Save.Version})");
                    break;
                case ChatReceived c:
                    Say($"{c.Name}: {c.Text}");
                    break;
                case StateRejected r:
                    Plugin.Logger.LogDebug($"state write {r.Key} refused: {r.Reason}");
                    break;
                case Disconnected d:
                    Interpolator.Clear();
                    Status = "Disconnected: " + d.Reason;
                    Say(Status);
                    break;
            }
        }
    }

    /// <summary>One server token per address, so a name stays yours on that server (BepInEx/config/NivalisCoop.tokens.json).</summary>
    internal static class Tokens
    {
        static string PathOf => System.IO.Path.Combine(Paths.ConfigPath, "NivalisCoop.tokens.json");

        static Dictionary<string, string> Load()
        {
            try { return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(PathOf)) ?? new Dictionary<string, string>(); }
            catch { return new Dictionary<string, string>(); }
        }

        public static string Get(string address) => Load().TryGetValue(address, out var token) ? token : null;

        public static void Set(string address, string token)
        {
            var all = Load();
            all[address] = token;
            File.WriteAllText(PathOf, JsonSerializer.Serialize(all));
        }
    }
}
