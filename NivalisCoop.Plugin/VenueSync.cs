using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Nivalis.CraftingSystem;
using Nivalis.GhostSystem.Ai;
using Nivalis.GhostSystem.CustomerLoop;
using Nivalis.Locale;
using NivalisCoop.Net;
using UnityEngine;

namespace NivalisCoop.Plugin
{
    /// <summary>
    /// The settings of the players' venues are data the game changes in place, not functions: the menu with its prices,
    /// open or closed, and every staff member's shift (working hours), tasks and wage. Every game looks at its player
    /// venues every two seconds; when one is different from what it last sent or received, that game changed it (a
    /// player did), and it sends the venue's whole settings to everybody in the world. The others, the session leader
    /// among them, put them into their game, so the leader's save keeps them. Who changed last, wins.
    /// </summary>
    internal static class VenueSync
    {
        const string Kind = "coop.venue";
        static readonly Dictionary<string, string> _known = new Dictionary<string, string>(); // venue -> settings last sent/received
        static float _next;
        public static int Sent, Applied;

        public static string Line => ServerWorld.CurrentSlot == null ? "" : $"Venues: {Sent} changes sent, {Applied} taken over";

        public static void OnEvent(CoopEvent ev)
        {
            if (ev is Disconnected) { _known.Clear(); return; }
            if (ev is not GameEvent ge || ge.Kind != Kind || ge.Data is not JsonObject d) return;
            if (ServerWorld.CurrentSlot == null || !(d["world"] is JsonValue w && w.GetValue<string>() == ServerWorld.CurrentSlot)) return;
            string name = d["venue"]?.GetValue<string>();
            if (name == null || d["s"] is not JsonObject s) return;
            var venue = Find(name);
            if (venue == null) { Plugin.Logger.LogWarning($"venue {name} is not in this world"); return; }
            try
            {
                Apply(venue, s);
                _known[name] = Read(venue)?.ToJsonString() ?? s.ToJsonString();
                Applied++;
                SaveGuard.RequestUpload();
                Plugin.Logger.LogInfo($"venue {name}: settings of player {ge.From} taken over");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"venue {name}: settings could not be taken over: {ex.Message}"); }
        }

        public static void Tick(CoopClient client)
        {
            if (client.State != ClientState.Connected || ServerWorld.CurrentSlot == null || client.LeaderId == null) return;
            if (Time.realtimeSinceStartup < _next) return;
            _next = Time.realtimeSinceStartup + 2f;
            try
            {
                foreach (var venue in Resources.FindObjectsOfTypeAll<Venue>())
                {
                    if (venue == null || !venue.PlayerOwned || venue.RuntimeData == null) continue;
                    var s = Read(venue);
                    if (s == null) continue;
                    string now = s.ToJsonString();
                    if (!_known.TryGetValue(venue.name, out var was)) { _known[venue.name] = now; continue; } // first look: nothing changed yet
                    if (was == now) continue;
                    _known[venue.name] = now;
                    client.SendEvent(Kind, new JsonObject { ["world"] = ServerWorld.CurrentSlot, ["venue"] = venue.name, ["s"] = s });
                    Sent++;
                    Plugin.Logger.LogInfo($"venue {venue.name}: our change sent to the others");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning("venues: " + ex.Message); }
        }

        static Venue Find(string name)
        {
            foreach (var v in Resources.FindObjectsOfTypeAll<Venue>()) if (v != null && v.name == name) return v;
            return null;
        }

        static JsonObject Read(Venue venue)
        {
            var g = venue.RuntimeData;
            if (g == null) return null;
            var menu = new JsonArray();
            var m = g.Menu;
            if (m != null)
                for (int i = 0; i < m.Count; i++)
                {
                    var item = m[i];
                    if (item?.RecipeDefinition == null) continue;
                    menu.Add(new JsonArray(item.RecipeDefinition.name, item.Price));
                }
            var staff = new JsonArray();
            // Staff is an IL2CPP IReadOnlyList; behind it is a List the C# side can walk
            var list = g.Staff?.TryCast<Il2CppSystem.Collections.Generic.List<Person>>();
            if (list != null)
                for (int i = 0; i < list.Count; i++)
                {
                    var p = list[i];
                    var rd = p?.RuntimeData;
                    if (rd == null) continue;
                    var h = rd.WorkingHours;
                    staff.Add(new JsonArray(p.Guid, Math.Round(h.x, 2), Math.Round(h.y, 2), (int)rd.Tasks, rd.Wage));
                }
            return new JsonObject { ["open"] = g.IsOpen != null && g.IsOpen.Value, ["menu"] = menu, ["staff"] = staff };
        }

        static void Apply(Venue venue, JsonObject s)
        {
            var g = venue.RuntimeData;
            if (g == null) throw new Exception("venue not loaded");
            if (s["open"] is JsonValue o && g.IsOpen != null) g.IsOpen.Value = o.GetValue<bool>();

            if (s["menu"] is JsonArray menu && g.Menu != null)
            {
                var m = g.Menu;
                bool same = m.Count == menu.Count;
                for (int i = 0; same && i < m.Count; i++)
                    same = m[i]?.RecipeDefinition != null && m[i].RecipeDefinition.name == menu[i][0].GetValue<string>() && m[i].Price == (int)menu[i][1].GetValue<double>();
                if (!same)
                {
                    var keep = new Dictionary<string, MealMenuItem>();
                    for (int i = 0; i < m.Count; i++) if (m[i]?.RecipeDefinition != null) keep[m[i].RecipeDefinition.name] = m[i];
                    while (m.Count > 0) m.RemoveAt(m.Count - 1);
                    foreach (var e in menu)
                    {
                        string recipe = e[0].GetValue<string>();
                        int price = (int)e[1].GetValue<double>();
                        MealMenuItem item;
                        if (keep.TryGetValue(recipe, out var old)) item = old;
                        else
                        {
                            var def = FindRecipe(recipe);
                            if (def == null) { Plugin.Logger.LogWarning($"recipe {recipe} is not in this game"); continue; }
                            item = new MealMenuItem { recipe = def };
                        }
                        item.Price = price;
                        m.Add(item);
                    }
                    try { if (Nivalis.Singleton<VenueManager>.InstanceExist()) Nivalis.Singleton<VenueManager>.Instance.TriggerMenuUpdatedEvent(); } catch { }
                }
            }

            if (s["staff"] is JsonArray staff)
            {
                bool changed = false;
                foreach (var e in staff)
                {
                    var person = FindPerson(e[0].GetValue<string>());
                    var rd = person?.RuntimeData;
                    if (rd == null) continue;
                    var hours = new Vector2((float)e[1].GetValue<double>(), (float)e[2].GetValue<double>());
                    int tasks = (int)e[3].GetValue<double>(), wage = (int)e[4].GetValue<double>();
                    if (rd.WorkingHours != hours) { rd.WorkingHours = hours; changed = true; }
                    if ((int)rd.Tasks != tasks) { rd.Tasks = (VenueTasks)tasks; changed = true; }
                    if (rd.Wage != wage) { rd.Wage = wage; changed = true; }
                }
                if (changed) { g.ReportStaffWorkingHoursChanged(); g.ReportStaffChanged(); }
            }
        }

        static readonly Dictionary<string, MealRecipeDefinition> _recipes = new Dictionary<string, MealRecipeDefinition>();

        static MealRecipeDefinition FindRecipe(string name)
        {
            if (_recipes.TryGetValue(name, out var r) && r != null) return r;
            foreach (var def in Resources.FindObjectsOfTypeAll<MealRecipeDefinition>()) if (def != null && def.name == name) return _recipes[name] = def;
            return null;
        }

        static Person FindPerson(string guid)
        {
            if (!Nivalis.Singleton<PersonDataManager>.InstanceExist()) return null;
            return Nivalis.Singleton<PersonDataManager>.Instance.guidToPersons.TryGetValue(guid, out var p) ? p : null;
        }
    }
}
