using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using BepInEx;
using NivalisCoop.Net;

namespace NivalisCoop.Plugin
{
    internal sealed class ServerEntry
    {
        public string Name { get; set; } = "Server";
        public string Address { get; set; } = "127.0.0.1:27800";
        public string Password { get; set; } = "";

        // what the last ping found out (not saved)
        [JsonIgnore] public ServerStatus Status { get; set; }
        [JsonIgnore] public string Error { get; set; }
        [JsonIgnore] public bool Pinging { get; set; }
    }

    /// <summary>
    /// The server list of the Multiplayer screen (BepInEx/config/NivalisCoop.servers.json), the way Minecraft keeps
    /// one. Passwords are stored in that file in plain text, like the rest of the BepInEx config.
    /// </summary>
    internal static class ServerBook
    {
        public static List<ServerEntry> Entries { get; private set; } = new List<ServerEntry>();

        static string PathOf => System.IO.Path.Combine(Paths.ConfigPath, "NivalisCoop.servers.json");

        public static void Load()
        {
            try
            {
                if (File.Exists(PathOf))
                {
                    Entries = JsonSerializer.Deserialize<List<ServerEntry>>(File.ReadAllText(PathOf)) ?? new List<ServerEntry>();
                    return;
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning("server list could not be read: " + ex.Message); }
            // first start: begin with the server from the config file so there is something to click
            Entries = new List<ServerEntry> { new ServerEntry { Name = "My server", Address = Plugin.Address.Value, Password = Plugin.Password.Value } };
            Save();
        }

        public static void Save()
        {
            try { File.WriteAllText(PathOf, JsonSerializer.Serialize(Entries, new JsonSerializerOptions { WriteIndented = true })); }
            catch (Exception ex) { Plugin.Logger.LogWarning("server list could not be saved: " + ex.Message); }
        }

        public static void Add(string name, string address, string password)
        {
            Entries.Add(new ServerEntry { Name = string.IsNullOrWhiteSpace(name) ? address : name.Trim(), Address = address.Trim(), Password = password ?? "" });
            Save();
        }

        public static void Remove(ServerEntry entry)
        {
            Entries.Remove(entry);
            Save();
        }

        /// <summary>Ask every server for its status (name, players, ping) without logging in.</summary>
        public static void PingAll()
        {
            foreach (var entry in Entries.ToArray())
            {
                if (entry.Pinging) continue;
                entry.Pinging = true;
                var e = entry;
                Task.Run(async () =>
                {
                    try
                    {
                        var (host, port) = CoopHost.ParseAddress(e.Address);
                        e.Status = await CoopClient.PingAsync(host, port);
                        e.Error = null;
                    }
                    catch (Exception ex) { e.Status = null; e.Error = ex is OperationCanceledException ? "no answer" : ex.Message; }
                    finally { e.Pinging = false; }
                });
            }
        }
    }
}
