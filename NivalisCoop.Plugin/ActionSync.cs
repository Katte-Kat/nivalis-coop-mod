using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Nivalis;
using Nivalis.GhostSystem;
using Nivalis.GhostSystem.Ai;
using Nivalis.GhostSystem.CustomerLoop;
using Nivalis.InventorySystem;
using NivalisCoop.Net;
using UnityEngine;

namespace NivalisCoop.Plugin
{
    /// <summary>
    /// One savegame for everybody: only the session leader's game saves the world, so whatever another player does to
    /// the world has to happen in the leader's game too. That is Netcode's "RPC to the authority": every game function
    /// in <see cref="Actions"/> that a player who is not the leader calls is sent to the leader (npc_act "rpc"), and the
    /// leader's game calls the same function with the same things. The player sees it at once in their own game; the
    /// leader's game makes it part of the world, and the leader's next save stores it on the server.
    ///
    /// Arguments travel as ids that are the same in every copy of the world: data objects (ScriptableObjects: venues,
    /// people, items, upgrades, places) by name, people by guid, ghosts by id, managers as "the one of that type", and
    /// the player who did it as "me" (the leader's own player stands in for them; the money is one shared account
    /// anyway). Calls that do not involve the players (the game's own AI owners buying a venue) are not sent.
    /// The money the leader's game spends while replaying is given back at once: the player who acted has already
    /// booked it on the server's shared account.
    /// </summary>
    internal static class ActionSync
    {
        // what changes the world when a player does it; the instance and argument types must be ones Encode knows
        static readonly (Type Type, string Method, bool Always)[] Actions =
        {
            (typeof(VenueManager), nameof(VenueManager.Buy), false),
            (typeof(VenueManager), nameof(VenueManager.Sell), false),
            (typeof(VenueManager), nameof(VenueManager.StartRenting), false),
            (typeof(VenueManager), nameof(VenueManager.StopRenting), false),
            (typeof(VenueManager), nameof(VenueManager.HireStaff), false),
            (typeof(VenueManager), nameof(VenueManager.FireStaff), false),
            (typeof(VenueManager), nameof(VenueManager.ChangeStaffTasks), false),
            (typeof(VenueAreaGhost), nameof(VenueAreaGhost.BuyIngredients), false),
            (typeof(VenueAreaGhost), nameof(VenueAreaGhost.Restock), false),
            (typeof(VenueAreaGhost), nameof(VenueAreaGhost.TryPurchaseFurniture), false),
            (typeof(Nivalis.Apartment.ApartmentManager), nameof(Nivalis.Apartment.ApartmentManager.Buy), false),
            (typeof(Nivalis.Apartment.ApartmentManager), nameof(Nivalis.Apartment.ApartmentManager.Sell), false),
            (typeof(GreenhouseManager), nameof(GreenhouseManager.Buy), false),
            (typeof(GreenhouseManager), nameof(GreenhouseManager.Sell), false),
            (typeof(GreenhouseAreaGhost), nameof(GreenhouseAreaGhost.BuyUpgrade), false),
            (typeof(TravelUnlockManager), nameof(TravelUnlockManager.UnlockLocation), true),
        };

        static readonly Dictionary<string, MethodInfo> _byKey = new Dictionary<string, MethodInfo>();
        static readonly Dictionary<MethodBase, (string Key, bool Always)> _byMethod = new Dictionary<MethodBase, (string, bool)>();
        [ThreadStatic] static int _replaying;
        public static int Sent, Replayed, Skipped;

        public static string Line => ServerWorld.CurrentSlot == null ? "" : $"World actions: {Sent} sent to the leader, {Replayed} done for others";

        static string Key(MethodInfo m) => m.DeclaringType.FullName + "." + m.Name + "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)) + ")";

        public static void Install(Harmony harmony)
        {
            // Harmony refuses __result on a function without a result, so there are two postfixes
            var postVoid = new HarmonyMethod(typeof(ActionSync).GetMethod(nameof(PostfixVoid), BindingFlags.Static | BindingFlags.NonPublic));
            var postValue = new HarmonyMethod(typeof(ActionSync).GetMethod(nameof(PostfixValue), BindingFlags.Static | BindingFlags.NonPublic));
            foreach (var (type, name, always) in Actions)
            {
                foreach (var m in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public).Where(x => x.Name == name && x.DeclaringType == type))
                {
                    try
                    {
                        harmony.Patch(m, postfix: m.ReturnType == typeof(void) ? postVoid : postValue);
                        string key = Key(m);
                        _byKey[key] = m;
                        _byMethod[m] = (key, always);
                    }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"world action {type.Name}.{name} not shared: {ex.Message}"); }
                }
            }
            Plugin.Logger.LogInfo($"world actions shared with the session leader: {_byKey.Count}");
        }

        // ---- a player who is not the leader did something ----------------------------------------------------------
        static void PostfixVoid(MethodBase __originalMethod, object __instance, object[] __args) => After(__originalMethod, __instance, __args, null);

        static void PostfixValue(MethodBase __originalMethod, object __instance, object[] __args, object __result) => After(__originalMethod, __instance, __args, __result);

        static void After(MethodBase __originalMethod, object __instance, object[] __args, object __result)
        {
            try
            {
                if (_replaying > 0) return;
                var client = CoopHost.Client;
                if (client.State != ClientState.Connected || ServerWorld.CurrentSlot == null || client.LeaderId == null || client.IsLeader) return;
                if (!_byMethod.TryGetValue(__originalMethod, out var info)) return;
                if (__result is bool ok && !ok) return; // the game said no (not enough money, ...)

                bool involvesPlayer = info.Always;
                var args = new JsonArray();
                var pars = __originalMethod.GetParameters();
                for (int i = 0; i < pars.Length; i++)
                {
                    var enc = Encode(__args[i], ref involvesPlayer);
                    if (enc == Unsupported) { Skipped++; return; }
                    args.Add(enc);
                }
                JsonNode inst = null;
                if (!__originalMethod.IsStatic)
                {
                    inst = Encode(__instance, ref involvesPlayer);
                    if (inst == Unsupported) { Skipped++; return; }
                }
                if (!involvesPlayer) return; // the game's own simulation, not a player
                client.SendNpcAction(null, "rpc", new JsonObject { ["m"] = info.Key, ["i"] = inst, ["a"] = args });
                Sent++;
                Plugin.Logger.LogInfo($"world action sent to the session leader: {info.Key}");
            }
            catch (Exception ex) { Plugin.Logger.LogError("world action: " + ex); }
        }

        // ---- the leader does it ------------------------------------------------------------------------------------
        public static void OnEvent(CoopEvent ev)
        {
            if (ev is not NpcAction act || act.Action != "rpc" || act.Data is not JsonObject d) return;
            var client = CoopHost.Client;
            if (!client.IsLeader) return;
            string key = d["m"] is JsonValue kv && kv.TryGetValue<string>(out var k) ? k : null;
            if (key == null || !_byKey.TryGetValue(key, out var method)) { Plugin.Logger.LogWarning($"unknown world action {key}"); return; }
            try
            {
                var pars = method.GetParameters();
                var raw = d["a"] as JsonArray ?? new JsonArray();
                if (raw.Count != pars.Length) throw new Exception("wrong number of arguments");
                var args = new object[pars.Length];
                for (int i = 0; i < pars.Length; i++) args[i] = Decode(raw[i], pars[i].ParameterType);
                object target = method.IsStatic ? null : Decode(d["i"], method.DeclaringType);
                if (!method.IsStatic && target == null) throw new Exception("its object is not in this world");

                var inv = UnityEngine.Object.FindObjectOfType<PlayerInventory>();
                int before = inv != null ? inv.Money : 0;
                _replaying++;
                try { method.Invoke(target, args); }
                finally { _replaying--; }
                if (inv != null && inv.Money != before) inv.ChangeMoneyWithoutReceipt(before - inv.Money); // already booked by the player who did it
                Replayed++;
                SaveGuard.RequestUpload();
                string who = client.Players.TryGetValue(act.From, out var p) ? p.Name : "player " + act.From;
                Plugin.Logger.LogInfo($"world action of {who} done here: {key}");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"world action {key} could not be done here: {(ex is TargetInvocationException t && t.InnerException != null ? t.InnerException.Message : ex.Message)}"); }
        }

        // ---- ids ---------------------------------------------------------------------------------------------------
        static readonly JsonNode Unsupported = JsonValue.Create("\u0000unsupported");

        static JsonNode Encode(object o, ref bool involvesPlayer)
        {
            switch (o)
            {
                case null: return null;
                case bool b: return b;
                case int i: return i;
                case long l: return l;
                case float f: return f;
                case double db: return db;
                case string s: return s;
                case Enum e: return Convert.ToInt64(e);
            }
            if (o is not Il2CppObjectBase il) return Unsupported;
            if (il.TryCast<PlayerManager.Player>() != null) { involvesPlayer = true; return new JsonObject { ["me"] = "player" }; }
            if (il.TryCast<PlayerInventory>() != null) { involvesPlayer = true; return new JsonObject { ["me"] = "inventory" }; }
            var rpd = il.TryCast<RuntimePersonData>();
            if (rpd != null) return rpd.ForPerson != null ? new JsonObject { ["rpd"] = rpd.ForPerson.Guid } : Unsupported;
            var person = il.TryCast<Person>();
            if (person != null) return new JsonObject { ["person"] = person.Guid };
            var venueGhost = il.TryCast<VenueAreaGhost>();
            if (venueGhost != null && venueGhost.PlayerOwned) involvesPlayer = true;
            var ghost = il.TryCast<Ghost>();
            if (ghost != null) return new JsonObject { ["ghost"] = ghost.Id };
            var venue = il.TryCast<BaseProperty>();
            if (venue != null && venue.PlayerOwned) involvesPlayer = true;
            var so = il.TryCast<ScriptableObject>();
            if (so != null) return new JsonObject { ["so"] = so.name, ["type"] = so.GetIl2CppType().FullName };
            var mb = il.TryCast<MonoBehaviour>();
            if (mb != null) return new JsonObject { ["single"] = mb.GetIl2CppType().FullName };
            return Unsupported;
        }

        static object Decode(JsonNode n, Type want)
        {
            if (n == null) return null;
            if (n is JsonValue v)
            {
                if (want == typeof(bool)) return JsonNum.B(v);
                if (want == typeof(int)) return JsonNum.I(v);
                if (want == typeof(long)) return (long)JsonNum.D(v);
                if (want == typeof(float)) return JsonNum.F(v);
                if (want == typeof(double)) return JsonNum.D(v);
                if (want == typeof(string)) return JsonNum.S(v);
                if (want.IsEnum) return Enum.ToObject(want, (long)JsonNum.D(v));
                throw new Exception("cannot read a " + want.Name);
            }
            var o = (JsonObject)n;
            Il2CppObjectBase found = null;
            if (o["me"] is JsonValue me)
            {
                if (JsonNum.S(me) == "inventory") found = UnityEngine.Object.FindObjectOfType<PlayerInventory>();
                else if (Singleton<PlayerManager>.InstanceExist()) found = Singleton<PlayerManager>.Instance.LocalPlayer;
            }
            else if (o["person"] is JsonValue pg) found = FindPerson(JsonNum.S(pg));
            else if (o["rpd"] is JsonValue rg) found = FindPerson(JsonNum.S(rg))?.RuntimeData;
            else if (o["ghost"] is JsonValue gid) found = GhostManager.HasInstance ? GhostManager.Instance.GetGhostById(JsonNum.S(gid)) : null;
            else if (o["so"] is JsonValue sn) found = FindAsset(JsonNum.S(sn), o["type"] is JsonValue tn ? JsonNum.S(tn) : null);
            else if (o["single"] is JsonValue st) found = FindSingle(JsonNum.S(st));
            if (found == null) throw new Exception($"not in this world: {n.ToJsonString()}");
            // hand it over as the type the function wants (an interface or a base class of the object)
            return want.IsInstanceOfType(found) ? found : Activator.CreateInstance(want, found.Pointer);
        }

        static Person FindPerson(string guid)
        {
            if (!Singleton<PersonDataManager>.InstanceExist()) return null;
            return Singleton<PersonDataManager>.Instance.guidToPersons.TryGetValue(guid, out var p) ? p : null;
        }

        static readonly Dictionary<string, ScriptableObject> _assets = new Dictionary<string, ScriptableObject>();

        static ScriptableObject FindAsset(string name, string typeName)
        {
            string key = typeName + "/" + name;
            if (_assets.TryGetValue(key, out var hit) && hit != null) return hit;
            foreach (var so in Resources.FindObjectsOfTypeAll<ScriptableObject>())
            {
                if (so == null || so.name != name) continue;
                if (typeName != null && so.GetIl2CppType().FullName != typeName) continue;
                _assets[key] = so;
                return so;
            }
            return null;
        }

        static MonoBehaviour FindSingle(string typeName)
        {
            foreach (var mb in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
                if (mb != null && mb.gameObject.scene.IsValid() && mb.GetIl2CppType().FullName == typeName) return mb;
            return null;
        }

        /// <summary>For other parts of the mod: is the game replaying another player's action right now?</summary>
        public static bool Replaying => _replaying > 0;
    }
}
