using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Nivalis;
using Nivalis.GhostSystem;
using Nivalis.GhostSystem.Ai;
using NivalisCoop.Net;
using UnityEngine;

namespace NivalisCoop.Plugin
{
    /// <summary>
    /// The people of a server world, simulated once and seen by everybody (Netcode's distributed authority).
    ///
    /// The game keeps every person as a "ghost" (AgentGhost, with an Id that is stored in the save, so every copy of
    /// the same world has the same ids) and shows a Character for the ghosts near the camera. The simulation of all
    /// ghosts is AgentGhostSimulator; the game pauses it itself during conversations (IsPaused).
    ///
    /// * The session leader's game simulates as in single player and sends the ghosts (id, position, heading, walk)
    ///   five times a second: only what changed, everything every five seconds.
    /// * Every other game keeps AgentGhostSimulator paused, moves its ghosts and the Characters of them to the
    ///   leader's positions, hides its own people the leader does not have, and shows a simple stand-in for people
    ///   the leader has and this game does not (customers that came after the world was loaded).
    /// * When the leader leaves, the server makes the next player the leader: that game un-pauses its simulator and
    ///   carries on from where the people stand.
    /// </summary>
    internal static class NpcSync
    {
        const float SendEvery = 0.2f, FullEvery = 5f;
        const float MoveEnough = 0.03f, TurnEnough = 3f;

        sealed class Target { public Vector3 Pos; public float Ry; public int Walk; public bool Seen; }
        sealed class Sent { public Vector3 Pos; public float Ry; public int Walk; }

        static readonly Dictionary<string, Target> _targets = new Dictionary<string, Target>();
        static readonly Dictionary<string, Sent> _sent = new Dictionary<string, Sent>();
        static readonly Dictionary<string, GameObject> _standIns = new Dictionary<string, GameObject>();
        static readonly HashSet<Character> _hidden = new HashSet<Character>();
        static readonly Dictionary<string, Character> _byGhost = new Dictionary<string, Character>();

        static AgentGhostSimulator _sim;
        static float _nextSend, _nextFull, _nextFind, _nextGhostWrite;
        static int _tick;
        static bool _following, _haveSnapshot, _loggedSendError, _loggedFirstSend, _loggedTickError;
        static Material _standInMaterial;

        public static string Line { get; private set; } = "";

        // ---- events -----------------------------------------------------------------------------------------------
        public static void OnEvent(CoopEvent ev)
        {
            switch (ev)
            {
                case LeaderChanged lc:
                    Plugin.Logger.LogInfo($"session leader of world {lc.World}: {lc.Name ?? "nobody"}{(lc.Handover ? " (took over)" : "")}{(CoopHost.Client.IsLeader ? " - that is us" : "")}");
                    if (CoopHost.Client.IsLeader) { _nextFull = 0; _sent.Clear(); }
                    break;
                case NpcSnapshot snap:
                    Take(snap);
                    break;
                case NpcRefused r:
                    Plugin.Logger.LogWarning("npc snapshot refused: " + r.Reason);
                    break;
                case Disconnected _:
                    Stop("disconnected");
                    break;
            }
        }

        static void Take(NpcSnapshot snap)
        {
            if (snap.Full) foreach (var t in _targets.Values) t.Seen = false;
            foreach (var node in snap.Npcs)
            {
                if (node is not JsonObject o || o["id"] is not JsonValue idv || !idv.TryGetValue<string>(out var id)) continue;
                if (o["gone"] is JsonValue g && g.TryGetValue<bool>(out var gone) && gone) { _targets.Remove(id); continue; }
                if (o["p"] is not JsonArray p || p.Count < 3) continue;
                if (!_targets.TryGetValue(id, out var t)) _targets[id] = t = new Target();
                t.Pos = new Vector3(F(p[0]), F(p[1]), F(p[2]));
                t.Ry = F(o["r"]);
                t.Walk = o["w"] is JsonValue w && w.TryGetValue<int>(out var wi) ? wi : 0;
                t.Seen = true;
            }
            if (snap.Full)
            {
                var stale = new List<string>();
                foreach (var kv in _targets) if (!kv.Value.Seen) stale.Add(kv.Key);
                foreach (var id in stale) _targets.Remove(id);
            }
            _haveSnapshot = true;
        }

        static float F(JsonNode n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? (float)d : 0f;

        // ---- every frame --------------------------------------------------------------------------------------------
        public static void Tick(CoopClient client)
        {
            try { TickCore(client); }
            catch (Exception ex)
            {
                if (!_loggedTickError) { _loggedTickError = true; Plugin.Logger.LogError("people: " + ex); }
            }
        }

        static void TickCore(CoopClient client)
        {
            if (client.State != ClientState.Connected || ServerWorld.CurrentSlot == null || client.LeaderId == null) { Stop("not in a server world"); return; }
            if (!GhostManager.HasInstance) { Line = "People: waiting for a loaded world"; return; }
            float now = Time.realtimeSinceStartup;
            if (now >= _nextFind) { _nextFind = now + 2f; FindSimulator(); IndexCharacters(); }

            if (client.IsLeader)
            {
                if (_following) Lead("we are the session leader now");
                if (now >= _nextSend)
                {
                    _nextSend = now + SendEvery;
                    try { Send(client, now); }
                    catch (Exception ex)
                    {
                        if (!_loggedSendError) { _loggedSendError = true; Plugin.Logger.LogError("people: could not send the snapshot: " + ex); }
                    }
                }
            }
            else
            {
                if (!_following) Follow();
                if (_sim != null && !_sim.IsPaused) _sim.IsPaused = true; // the game un-pauses it after every conversation
                if (now >= _nextGhostWrite) { _nextGhostWrite = now + 0.2f; WriteGhosts(); }
                Line = $"People: shown as the session leader has them ({_targets.Count}, {_standIns.Count} stand-ins)";
            }
        }

        /// <summary>After the animation: the Characters stand where the leader has them (root motion would move them).</summary>
        public static void LateTick()
        {
            if (!_following || !_haveSnapshot) return;
            float k = 1f - Mathf.Exp(-12f * Time.deltaTime);
            foreach (var kv in _byGhost)
            {
                var ch = kv.Value;
                if (ch == null) continue;
                if (!_targets.TryGetValue(kv.Key, out var t))
                {
                    if (_hidden.Add(ch)) SetShown(ch, false); // only in our copy of the world
                    continue;
                }
                if (_hidden.Remove(ch)) SetShown(ch, true);
                var tr = ch.transform;
                tr.position = (tr.position - t.Pos).sqrMagnitude > 9f ? t.Pos : Vector3.Lerp(tr.position, t.Pos, k);
                tr.rotation = Quaternion.Slerp(tr.rotation, Quaternion.Euler(0, t.Ry, 0), k);
                if ((int)ch.CurrentWalk != t.Walk)
                {
                    try { ch.ChangeWalkWeight((Character.AnimatorWalkType)t.Walk, 0.2f); } catch { }
                }
            }
            UpdateStandIns(k);
        }

        // ---- leader -------------------------------------------------------------------------------------------------
        static void Send(CoopClient client, float now)
        {
            bool full = now >= _nextFull;
            if (full) _nextFull = now + FullEvery;
            var list = new JsonArray();
            var present = new HashSet<string>();
            var near = PlayerPositions(client);
            // interest management: only people the game shows as a Character (it does that near the camera) and that
            // are near a player, the nearest first. Ghosts without a Character are far away and nobody sees them.
            _pick.Clear();
            foreach (var kv in _byGhost)
            {
                var ch = kv.Value;
                if (ch == null) continue;
                var pos = ch.transform.position;
                float d = Nearest(near, pos);
                if (d < InterestRadius * InterestRadius) _pick.Add((d, kv.Key, ch));
            }
            // a player far from us: our game has no Characters there, so take the ghosts around them
            if (near.Count > 1)
            {
                foreach (var ghost in GhostManager.Instance._agentGhosts)
                {
                    if (ghost == null) continue;
                    string id = ghost.Id;
                    if (string.IsNullOrEmpty(id) || _byGhost.ContainsKey(id)) continue;
                    var gp = ghost.Position;
                    float best = float.MaxValue;
                    for (int i = 1; i < near.Count; i++) best = Mathf.Min(best, (near[i] - gp).sqrMagnitude);
                    if (best < GhostRadius * GhostRadius) _pick.Add((best, id, null));
                }
            }
            _pick.Sort((a, b) => a.D.CompareTo(b.D));
            int count = Math.Min(_pick.Count, MaxPeople);
            for (int i = 0; i < count; i++)
            {
                var (_, id, ch) = _pick[i];
                Vector3 pos; float ry = 0f; int walk = 0;
                if (ch != null) { pos = ch.transform.position; ry = ch.transform.eulerAngles.y; walk = (int)ch.CurrentWalk; }
                else
                {
                    var ghost = GhostManager.Instance.GetGhostById(id);
                    if (ghost == null) continue;
                    pos = ghost.Position;
                    if (_sent.TryGetValue(id, out var prev) && (pos - prev.Pos).sqrMagnitude > 0.0001f)
                        ry = Mathf.Atan2(pos.x - prev.Pos.x, pos.z - prev.Pos.z) * Mathf.Rad2Deg; // heading from the way it goes
                    else if (prev != null) ry = prev.Ry;
                    walk = prev != null && (pos - prev.Pos).sqrMagnitude > 0.0001f ? 1 : 0;
                }
                present.Add(id);
                if (!full && _sent.TryGetValue(id, out var last)
                    && (last.Pos - pos).sqrMagnitude < MoveEnough * MoveEnough && Mathf.Abs(Mathf.DeltaAngle(last.Ry, ry)) < TurnEnough && last.Walk == walk)
                    continue;
                _sent[id] = new Sent { Pos = pos, Ry = ry, Walk = walk };
                list.Add(new JsonObject { ["id"] = id, ["p"] = new JsonArray(R(pos.x), R(pos.y), R(pos.z)), ["r"] = Mathf.Round(ry), ["w"] = walk });
            }
            if (!full)
            {
                var gone = new List<string>();
                foreach (var id in _sent.Keys) if (!present.Contains(id)) gone.Add(id);
                foreach (var id in gone) { _sent.Remove(id); list.Add(new JsonObject { ["id"] = id, ["gone"] = true }); }
            }
            else
            {
                var gone = new List<string>();
                foreach (var id in _sent.Keys) if (!present.Contains(id)) gone.Add(id);
                foreach (var id in gone) _sent.Remove(id);
            }
            if (list.Count == 0 && !full) return;
            client.SendNpcState(list, ++_tick, full);
            if (!_loggedFirstSend) { _loggedFirstSend = true; Plugin.Logger.LogInfo($"people: first snapshot sent, {present.Count} people, {list.Count} entries"); }
            Line = $"People: we simulate them for everybody ({present.Count})";
        }

        static double R(float v) => Math.Round(v, 2);

        const float InterestRadius = 70f;
        static readonly List<Vector3> _near = new List<Vector3>();

        /// <summary>Where the players of this world are: us first, then everybody in our scene.</summary>
        static List<Vector3> PlayerPositions(CoopClient client)
        {
            _near.Clear();
            var cam = Camera.main;
            if (cam != null) _near.Add(cam.transform.position);
            string scene = LocalPose.SceneKey();
            double now = Time.realtimeSinceStartupAsDouble;
            foreach (var p in client.Players.Values)
                if (CoopHost.Interpolator.TryGet(p.Id, now, out var pose) && pose.Scene == scene) _near.Add(new Vector3(pose.X, pose.Y, pose.Z));
            return _near;
        }

        const int MaxPeople = 300;
        const float GhostRadius = 40f;
        static readonly List<(float D, string Id, Character Ch)> _pick = new List<(float, string, Character)>();

        /// <summary>Squared distance to the nearest player (0 when we do not know where anybody is).</summary>
        static float Nearest(List<Vector3> near, Vector3 pos)
        {
            if (near.Count == 0) return 0f;
            float best = float.MaxValue;
            foreach (var p in near) best = Mathf.Min(best, (p - pos).sqrMagnitude);
            return best;
        }

        // ---- follower -----------------------------------------------------------------------------------------------
        static void Follow()
        {
            _following = true;
            if (_sim != null) _sim.IsPaused = true;
            Plugin.Logger.LogInfo("people: following the session leader, our own simulation of people is paused");
        }

        static void Lead(string why)
        {
            _following = false;
            if (_sim != null) _sim.IsPaused = false;
            foreach (var ch in _hidden) if (ch != null) SetShown(ch, true);
            _hidden.Clear();
            ClearStandIns();
            _nextFull = 0;
            Plugin.Logger.LogInfo("people: " + why + ", our simulation runs again");
        }

        static void Stop(string why)
        {
            if (_following) Lead(why);
            _targets.Clear(); _sent.Clear(); _byGhost.Clear(); _haveSnapshot = false;
            Line = "";
        }

        static void FindSimulator()
        {
            if (_sim == null) _sim = UnityEngine.Object.FindObjectOfType<AgentGhostSimulator>();
        }

        static void IndexCharacters()
        {
            _byGhost.Clear();
            foreach (var ch in Character.instances)
            {
                if (ch == null) continue;
                var agent = ch.Agent;
                if (agent == null) continue;
                var ghost = agent.MyGhost;
                if (ghost == null || string.IsNullOrEmpty(ghost.Id)) continue;
                _byGhost[ghost.Id] = ch;
            }
        }

        // the ghosts follow too, so the game decides visibility and culling from the right places
        static void WriteGhosts()
        {
            if (!_haveSnapshot) return;
            var gm = GhostManager.Instance;
            foreach (var kv in _targets)
            {
                var ghost = gm.GetGhostById(kv.Key);
                if (ghost == null) continue;
                if ((ghost.Position - kv.Value.Pos).sqrMagnitude > MoveEnough * MoveEnough)
                {
                    try { gm.UpdatePosition(ghost, kv.Value.Pos); } catch { }
                }
            }
        }

        static void SetShown(Character ch, bool on)
        {
            foreach (var r in ch.GetComponentsInChildren<Renderer>(true)) r.enabled = on;
        }

        static void UpdateStandIns(float k)
        {
            var gm = GhostManager.Instance;
            foreach (var kv in _targets)
            {
                bool local = _byGhost.ContainsKey(kv.Key) || gm.GetGhostById(kv.Key) != null;
                if (local) { if (_standIns.TryGetValue(kv.Key, out var old)) { UnityEngine.Object.Destroy(old); _standIns.Remove(kv.Key); } continue; }
                if (!_standIns.TryGetValue(kv.Key, out var go)) _standIns[kv.Key] = go = BuildStandIn(kv.Key);
                go.transform.position = Vector3.Lerp(go.transform.position, kv.Value.Pos + Vector3.up * 0.85f, k);
                go.transform.rotation = Quaternion.Euler(0, kv.Value.Ry, 0);
            }
            if (_standIns.Count == 0) return;
            var gone = new List<string>();
            foreach (var id in _standIns.Keys) if (!_targets.ContainsKey(id)) gone.Add(id);
            foreach (var id in gone) { UnityEngine.Object.Destroy(_standIns[id]); _standIns.Remove(id); }
        }

        static GameObject BuildStandIn(string id)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = "NivalisCoop_Person_" + id;
            var col = go.GetComponent<Collider>();
            if (col != null) UnityEngine.Object.Destroy(col);
            go.transform.localScale = new Vector3(0.5f, 0.85f, 0.5f);
            var r = go.GetComponent<Renderer>();
            if (_standInMaterial == null)
            {
                var shader = Shader.Find("Unlit/Color");
                if (shader != null) _standInMaterial = new Material(shader) { color = new Color(0.55f, 0.55f, 0.6f) };
            }
            if (_standInMaterial != null) r.sharedMaterial = _standInMaterial;
            if (_targets.TryGetValue(id, out var t)) go.transform.position = t.Pos + Vector3.up * 0.85f;
            return go;
        }

        static void ClearStandIns()
        {
            foreach (var go in _standIns.Values) if (go != null) UnityEngine.Object.Destroy(go);
            _standIns.Clear();
        }
    }
}
