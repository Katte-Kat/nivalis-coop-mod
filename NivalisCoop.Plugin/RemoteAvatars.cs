using System;
using System.Collections.Generic;
using NivalisCoop.Net;
using UnityEngine;

namespace NivalisCoop.Plugin
{
    /// <summary>
    /// Draws the other players in the world at their interpolated position, with the same scene check as the name
    /// tags: as the people model they chose (AvatarModels), or as a simple coloured body and head while they have not
    /// chosen one or this game has not loaded that model yet.
    /// </summary>
    internal static class RemoteAvatars
    {
        sealed class Avatar { public GameObject Root; public Renderer[] Renders; public bool Logged; public float BuiltAt; public string Model;
            public Animator Anim; public int[] SpeedParams; public Vector3 LastPos; public float Speed; public bool HasWalk; }
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
                bool visible = CoopHost.Interpolator.TryGet(player.Id, now, out var pose) && LocalPose.SameScene(pose.Scene, scene);
                if (!visible) { if (_avatars.TryGetValue(player.Id, out var hidden)) hidden.Root.SetActive(false); continue; }
                if (_avatars.TryGetValue(player.Id, out var avatar) && avatar.Model != player.Avatar && AvatarModels.Has(player.Avatar))
                {
                    Remove(player.Id); // they chose a model, or it has just become available here
                    avatar = null;
                }
                if (avatar == null) avatar = _avatars[player.Id] = Build(player.Id, player.Avatar);
                avatar.Root.SetActive(true);
                // the pose is the player's camera, about 1.6 m above the feet
                var feet = new Vector3(pose.X, pose.Y - 1.6f, pose.Z);
                avatar.Root.transform.position = feet;
                avatar.Root.transform.rotation = Quaternion.Euler(0, pose.Ry, 0);
                Animate(avatar, feet);
                if (!avatar.Logged && Time.realtimeSinceStartup > avatar.BuiltAt + 1.5f)
                {
                    avatar.Logged = true;
                    var r = avatar.Renders[0];
                    var cam = Camera.main;
                    Plugin.Logger.LogInfo($"avatar {player.Id} at {avatar.Root.transform.position}, layer {avatar.Root.layer}, cam mask {(cam != null ? cam.cullingMask : 0)}, shader {(r.sharedMaterial != null ? r.sharedMaterial.shader.name : "none")}, visible {r.isVisible}, enabled {r.enabled}");
                }
            }
        }

        static Avatar Build(int id, string model)
        {
            var copy = AvatarModels.Build(model, "NivalisCoop_Player_" + id);
            if (copy != null)
            {
                Plugin.Logger.LogInfo($"avatar for player {id}: model {model}");
                var anim = copy.GetComponentInChildren<Animator>(true);
                return new Avatar { Root = copy, Renders = copy.GetComponentsInChildren<Renderer>(true), BuiltAt = Time.realtimeSinceStartup, Model = model, Logged = true,
                                    Anim = anim, SpeedParams = SpeedParameters(anim), LastPos = copy.transform.position, HasWalk = HasFloat(anim, "Walk") };
            }
            var root = new GameObject("NivalisCoop_Player_" + id);
            var color = Colors[id % Colors.Length];
            var body = Part(PrimitiveType.Capsule, root.transform, new Vector3(0, 0.8f, 0), new Vector3(0.55f, 0.8f, 0.55f), color);
            var head = Part(PrimitiveType.Sphere, root.transform, new Vector3(0, 1.62f, 0), new Vector3(0.32f, 0.32f, 0.32f), color * 0.85f);
            var nose = Part(PrimitiveType.Cube, root.transform, new Vector3(0, 1.62f, 0.17f), new Vector3(0.08f, 0.08f, 0.14f), Color.white); // shows where they look
            Plugin.Logger.LogInfo($"avatar for player {id} built (material: {(_material != null ? _material.shader.name : "game default")})");
            return new Avatar { Root = root, Renders = new[] { body, head, nose }, BuiltAt = Time.realtimeSinceStartup, Model = AvatarModels.Has(model) ? model : null };
        }

        // The game's people walk by an animator float (CharacterMovement.speedId): the copy gets the speed the player
        // really moves at, so it stands, walks or runs like a person of the game.
        static int[] SpeedParameters(Animator anim)
        {
            var found = new List<int>();
            if (anim == null) return found.ToArray();
            try
            {
                foreach (var p in anim.parameters)
                    if (p.type == AnimatorControllerParameterType.Float && p.name.IndexOf("speed", StringComparison.OrdinalIgnoreCase) >= 0)
                        found.Add(p.nameHash);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning("avatar animator parameters: " + ex.Message); }
            return found.ToArray();
        }

        static readonly int WalkHash = Animator.StringToHash("Walk"), SprintHash = Animator.StringToHash("WalkSprint");

        static bool HasFloat(Animator anim, string name)
        {
            if (anim == null) return false;
            try { foreach (var p in anim.parameters) if (p.type == AnimatorControllerParameterType.Float && p.name == name) return true; }
            catch { }
            return false;
        }

        static void Animate(Avatar a, Vector3 feet)
        {
            if (a.Anim == null || a.SpeedParams == null || a.SpeedParams.Length == 0) return;
            float dt = Mathf.Max(Time.deltaTime, 0.001f);
            var d = feet - a.LastPos; d.y = 0;
            a.LastPos = feet;
            float v = d.magnitude / dt;
            if (v > 12f) v = 0f; // a jump (teleport, scene change), not walking
            a.Speed = Mathf.Lerp(a.Speed, v, 1f - Mathf.Exp(-6f * dt));
            foreach (var h in a.SpeedParams) a.Anim.SetFloat(h, a.Speed);
            // the walk style is a weight per style (Character.ChangeWalkWeight): plain walking, sprinting when fast
            if (a.HasWalk)
            {
                float sprint = Mathf.Clamp01((a.Speed - 3.5f) / 2f);
                a.Anim.SetFloat(WalkHash, 1f - sprint);
                a.Anim.SetFloat(SprintHash, sprint);
            }
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
            // the game's own materials are known to render in its pipeline: take the shader of one of them
            try
            {
                Shader found = null;
                foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
                {
                    if (m == null || m.shader == null) continue;
                    string n = m.shader.name;
                    if (n.StartsWith("Universal Render Pipeline/Unlit") || n.StartsWith("HDRP/Unlit")) { found = m.shader; break; }
                    if (found == null && (n.StartsWith("Universal Render Pipeline/Lit") || n.StartsWith("HDRP/Lit"))) found = m.shader;
                }
                if (found != null) { _material = new Material(found); return _material; }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning("could not look for a game material: " + ex.Message); }
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
