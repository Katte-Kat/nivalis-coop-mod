using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using HarmonyLib;
using Nivalis;
using NivalisCoop.Net;
using UnityEngine;

namespace NivalisCoop.Plugin
{
    /// <summary>
    /// Shares where placed objects stand. Every HoldableEntity has a stable <c>GhostId</c> that is the same in every
    /// copy of the same save, so it plays the role of Netcode's NetworkObjectId: the server keeps one key per object
    /// (<c>shared/&lt;world&gt;/obj/&lt;GhostId&gt;</c> = position and rotation), a placement is written by the player who
    /// did it, and everybody else moves their own copy of the object to that place.
    /// </summary>
    internal static class ObjectSync
    {
        const float MovedEnough = 0.04f;
        static string Prefix => "shared/" + ServerWorld.CurrentSlot + "/obj/";

        static bool _applying;
        static readonly Dictionary<string, float> _toReport = new Dictionary<string, float>(); // ghostId -> when
        static readonly Dictionary<string, (Vector3 Pos, Quaternion Rot)> _toApply = new Dictionary<string, (Vector3, Quaternion)>();
        static readonly Dictionary<string, (Vector3 Pos, Quaternion Rot)> _known = new Dictionary<string, (Vector3, Quaternion)>();
        static float _nextApply;
        static string _slot;
        public static int Applied, Reported;

        public static string Line => ServerWorld.CurrentSlot == null ? "" : $"Objects: {Reported} sent, {Applied} moved here, {_toApply.Count} waiting";

        // ---- from the game -------------------------------------------------------------------------------------
        public static void OnPlaced(HoldableEntity entity)
        {
            if (_applying || CoopHost.Client.State != ClientState.Connected || ServerWorld.CurrentSlot == null) return;
            string id = entity.GhostId;
            if (string.IsNullOrEmpty(id)) return;
            _toReport[id] = Time.realtimeSinceStartup + 1.2f; // report when the object has come to rest
        }

        // ---- from the server -----------------------------------------------------------------------------------
        public static void OnEvent(CoopEvent ev)
        {
            if (ev is Disconnected) { _toApply.Clear(); _known.Clear(); _toReport.Clear(); return; }
            if (ev is StateChanged sc && ServerWorld.CurrentSlot != null)
            {
                foreach (var kv in sc.Set) Take(kv.Key, kv.Value);
            }
        }

        static void Take(string key, JsonNode value)
        {
            if (!key.StartsWith(Prefix, StringComparison.Ordinal)) return;
            if (value is not JsonArray a || a.Count < 7) return;
            string id = key.Substring(Prefix.Length);
            var pos = new Vector3(F(a[0]), F(a[1]), F(a[2]));
            var rot = new Quaternion(F(a[3]), F(a[4]), F(a[5]), F(a[6]));
            if (_known.TryGetValue(id, out var mine) && (mine.Pos - pos).sqrMagnitude < MovedEnough * MovedEnough) return; // our own echo
            _toApply[id] = (pos, rot);
        }

        static float F(JsonNode n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? (float)d : 0f;

        public static void Tick(CoopClient client)
        {
            if (client.State != ClientState.Connected || ServerWorld.CurrentSlot == null) return;
            if (_slot != ServerWorld.CurrentSlot)
            {
                _slot = ServerWorld.CurrentSlot; _known.Clear(); _toApply.Clear(); _toReport.Clear();
                foreach (var kv in client.World) Take(kv.Key, kv.Value); // what was already placed when we came
            }
            float now = Time.realtimeSinceStartup;

            if (_toReport.Count > 0)
            {
                List<string> due = null;
                foreach (var kv in _toReport) if (now >= kv.Value) (due ??= new List<string>()).Add(kv.Key);
                if (due != null) foreach (var id in due) { _toReport.Remove(id); Report(client, id); }
            }

            if (now >= _nextApply && _toApply.Count > 0)
            {
                _nextApply = now + 0.5f;
                List<string> done = null;
                foreach (var kv in _toApply)
                {
                    HoldableEntity entity = null;
                    if (!HoldableEntity.TryGetLoadedEntity(kv.Key, out entity) || entity == null) continue; // not loaded here (yet)
                    if (entity.IsHeld || entity.IsBeingPlaced) continue; // somebody here is just handling it
                    var cur = entity.transform;
                    if ((cur.position - kv.Value.Pos).sqrMagnitude > MovedEnough * MovedEnough)
                    {
                        _applying = true;
                        try { entity.MovePlaced(new UnityEngine.Pose(kv.Value.Pos, kv.Value.Rot)); Applied++; }
                        catch (Exception ex) { Plugin.Logger.LogWarning($"could not move object {kv.Key}: {ex.Message}"); }
                        finally { _applying = false; }
                    }
                    _known[kv.Key] = kv.Value;
                    (done ??= new List<string>()).Add(kv.Key);
                }
                if (done != null) foreach (var id in done) _toApply.Remove(id);
            }
        }

        static void Report(CoopClient client, string ghostId)
        {
            HoldableEntity entity = null;
            if (!HoldableEntity.TryGetLoadedEntity(ghostId, out entity) || entity == null) return;
            var t = entity.transform;
            var p = t.position; var r = t.rotation;
            _known[ghostId] = (p, r);
            client.SetState(Prefix + ghostId, new JsonArray(p.x, p.y, p.z, r.x, r.y, r.z, r.w));
            Reported++;
        }
    }

    [HarmonyPatch(typeof(HoldableEntity), nameof(HoldableEntity.Place))]
    internal static class PlacePatch
    {
        static void Postfix(HoldableEntity __instance)
        {
            try { ObjectSync.OnPlaced(__instance); }
            catch (Exception ex) { Plugin.Logger.LogError("Place hook: " + ex); }
        }
    }

    [HarmonyPatch(typeof(HoldableEntity), nameof(HoldableEntity.MovePlaced))]
    internal static class MovePlacedPatch
    {
        static void Postfix(HoldableEntity __instance)
        {
            try { ObjectSync.OnPlaced(__instance); }
            catch (Exception ex) { Plugin.Logger.LogError("MovePlaced hook: " + ex); }
        }
    }
}
