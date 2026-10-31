using System;
using System.Collections.Generic;
using NivalisCoop.Net;
using UnityEngine;

namespace NivalisCoop.Plugin
{
    /// <summary>
    /// Draws the other players in the world: a simple body and head at their interpolated position, with the same
    /// scene check as the name tags. The player is first person, so the game has no body model to reuse.
    /// </summary>
    internal static class RemoteAvatars
    {
        sealed class Avatar { public GameObject Root; public Renderer[] Renders; }
        static readonly Dictionary<int, Avatar> _avatars = new Dictionary<int, Avatar>();
        static Material _material;
        static bool _materialTried;
        static readonly List<int> _gone = new List<int>();

        static readonly Color[] Colors =
        {
            new Color(0.95f, 0.45f, 0.25f), new Color(0.3f, 0.7f, 0.95f), new Color(0.6f, 0.85f, 0.35f),
            new Color(0.85f, 0.4f, 0.85f), new Color(0.95f, 0.85f, 0.3f), new Color(0.4f, 0.9f, 0.8f),
        };

        public static void Tick(CoopClient client)
        {
            if (client.State != ClientState.Connected || ServerWorld.CurrentSlot == null) { Clear(); return; }
            string scene = LocalPose.SceneKey();
            double now = Time.realtimeSinceStartupAsDouble;
            _gone.Clear();
            foreach (var id in _avatars.Keys) if (!client.Players.ContainsKey(id)) _gone.Add(id);
            foreach (var id in _gone) Remove(id);

            foreach (var player in client.Players.Values)
            {
                bool visible = CoopHost.Interpolator.TryGet(player.Id, now, out var pose) && pose.Scene == scene;
                if (!visible) { if (_avatars.TryGetValue(player.Id, out var hidden)) hidden.Root.SetActive(false); continue; }
                if (!_avatars.TryGetValue(player.Id, out var avatar)) avatar = _avatars[player.Id] = Build(player.Id);
                avatar.Root.SetActive(true);
                // the pose is the player's camera, about 1.6 m above the feet
                avatar.Root.transform.position = new Vector3(pose.X, pose.Y - 1.6f, pose.Z);
                avatar.Root.transform.rotation = Quaternion.Euler(0, pose.Ry, 0);
            }
        }

        static Avatar Build(int id)
        {
            var root = new GameObject("NivalisCoop_Player_" + id);
            var color = Colors[id % Colors.Length];
            var body = Part(PrimitiveType.Capsule, root.transform, new Vector3(0, 0.8f, 0), new Vector3(0.55f, 0.8f, 0.55f), color);
            var head = Part(PrimitiveType.Sphere, root.transform, new Vector3(0, 1.62f, 0), new Vector3(0.32f, 0.32f, 0.32f), color * 0.85f);
            var nose = Part(PrimitiveType.Cube, root.transform, new Vector3(0, 1.62f, 0.17f), new Vector3(0.08f, 0.08f, 0.14f), Color.white); // shows where they look
            Plugin.Logger.LogInfo($"avatar for player {id} built (material: {(_material != null ? _material.shader.name : "game default")})");
            return new Avatar { Root = root, Renders = new[] { body, head, nose } };
        }

        static Renderer Part(PrimitiveType type, Transform parent, Vector3 pos, Vector3 scale, Color color)
        {
            var go = GameObject.CreatePrimitive(type);
            var col = go.GetComponent<Collider>();
            if (col != null) UnityEngine.Object.Destroy(col); // nobody bumps into a puppet
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            var r = go.GetComponent<Renderer>();
            var mat = Material();
            if (mat != null) r.sharedMaterial = mat;
            var block = new MaterialPropertyBlock();
            block.SetColor("_BaseColor", color);
            block.SetColor("_Color", color);
            r.SetPropertyBlock(block);
            return r;
        }

        // The primitives' own material is the built-in Standard shader, which a render-pipeline game may draw pink.
        static Material Material()
        {
            if (_materialTried) return _material;
            _materialTried = true;
            foreach (var name in new[] { "Universal Render Pipeline/Unlit", "Universal Render Pipeline/Lit", "Unlit/Color", "Sprites/Default", "Standard" })
            {
                var shader = Shader.Find(name);
                if (shader == null) continue;
                _material = new Material(shader);
                return _material;
            }
            return null;
        }

        static void Remove(int id)
        {
            if (_avatars.TryGetValue(id, out var a) && a.Root != null) UnityEngine.Object.Destroy(a.Root);
            _avatars.Remove(id);
        }

        public static void Clear()
        {
            if (_avatars.Count == 0) return;
            foreach (var id in new List<int>(_avatars.Keys)) Remove(id);
        }
    }
}
