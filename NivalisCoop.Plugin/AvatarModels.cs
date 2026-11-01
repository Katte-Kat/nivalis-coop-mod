using System;
using System.Collections.Generic;
using Nivalis;
using UnityEngine;

namespace NivalisCoop.Plugin
{
    /// <summary>
    /// The game's people models, as avatars for the players. Every person in the game is a Character; the models are
    /// whatever Characters the game has loaded (its prefabs and the people around you), named after their prefab.
    /// A player chooses one name; the others build a copy of that model for them.
    ///
    /// A copy must look like the person and nothing else: it is made under an inactive holder (so none of the game's
    /// scripts on it wake up and register a new person with the game), then every script, collider and physics part is
    /// removed and only the meshes and the animator stay.
    /// </summary>
    internal static class AvatarModels
    {
        static readonly SortedDictionary<string, Character> _models = new SortedDictionary<string, Character>(StringComparer.OrdinalIgnoreCase);
        static float _nextScan;
        static GameObject _holder;

        /// <summary>The model names known so far (it grows while you walk through the city).</summary>
        public static IReadOnlyCollection<string> Names { get { Scan(false); return _models.Keys; } }
        public static int Count => _models.Count;

        public static string Clean(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            int cut = name.IndexOf("(Clone)", StringComparison.Ordinal);
            if (cut >= 0) name = name.Substring(0, cut);
            return name.Trim();
        }

        public static void Scan(bool force)
        {
            if (!force && Time.realtimeSinceStartup < _nextScan) return;
            _nextScan = Time.realtimeSinceStartup + 10f;
            try
            {
                int before = _models.Count;
                foreach (var ch in Resources.FindObjectsOfTypeAll<Character>())
                {
                    if (ch == null) continue;
                    string name = Clean(ch.gameObject.name);
                    if (name.Length == 0 || name.Length > 80) continue;
                    if (ch.GetComponentInChildren<SkinnedMeshRenderer>(true) == null) continue; // no body to show
                    if (!_models.TryGetValue(name, out var have) || have == null) _models[name] = ch;
                }
                if (_models.Count != before) Plugin.Logger.LogInfo($"avatar models: {_models.Count} known");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning("avatar models: " + ex.Message); }
        }

        public static bool Has(string name) { Scan(false); return name != null && _models.TryGetValue(name, out var c) && c != null; }

        /// <summary>A copy of the model for a player, or null when the game has not loaded this model (yet).</summary>
        public static GameObject Build(string name, string label)
        {
            Scan(false);
            if (name == null || !_models.TryGetValue(name, out var source) || source == null) return null;
            if (_holder == null)
            {
                _holder = new GameObject("NivalisCoop_AvatarHolder");
                _holder.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(_holder);
            }
            GameObject copy = null;
            try
            {
                copy = UnityEngine.Object.Instantiate(source.gameObject, _holder.transform);
                copy.name = label;
                Strip(copy);
                copy.transform.SetParent(null, false);
                copy.SetActive(true);
                foreach (var r in copy.GetComponentsInChildren<Renderer>(true)) r.enabled = true;
                var animator = copy.GetComponentInChildren<Animator>(true);
                if (animator != null) { animator.enabled = true; animator.applyRootMotion = false; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate; }
                if (!_loggedAnimator && animator != null) { _loggedAnimator = true; LogAnimator(animator); }
                return copy;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"avatar model {name} could not be built: {ex.Message}");
                if (copy != null) UnityEngine.Object.Destroy(copy);
                return null;
            }
        }

        static bool _loggedAnimator;

        static void LogAnimator(Animator a)
        {
            try
            {
                var sb = new System.Text.StringBuilder("avatar animator: layers ");
                for (int i = 0; i < a.layerCount; i++) sb.Append(i).Append('=').Append(a.GetLayerName(i)).Append(' ');
                sb.Append("| parameters ");
                foreach (var p in a.parameters) sb.Append(p.name).Append(':').Append(p.type).Append(' ');
                var ctrl = a.runtimeAnimatorController;
                if (ctrl != null)
                {
                    sb.Append("| clips ");
                    int n = 0;
                    foreach (var c in ctrl.animationClips) { if (c == null) continue; if (n++ > 80) break; sb.Append(c.name).Append(", "); }
                }
                Plugin.Logger.LogInfo(sb.ToString());
            }
            catch (Exception ex) { Plugin.Logger.LogWarning("avatar animator: " + ex.Message); }
        }

        // everything that is not looks: the game's scripts, colliders, physics, audio, navigation
        static void Strip(GameObject go)
        {
            for (int pass = 0; pass < 3; pass++) // a component another one requires goes in a later pass
            {
                foreach (var c in go.GetComponentsInChildren<Component>(true))
                {
                    // IL2CPP objects come back as plain Components: C# "is" does not see their real type, TryCast does
                    if (c == null || c.TryCast<Transform>() != null || c.TryCast<Renderer>() != null || c.TryCast<MeshFilter>() != null
                        || c.TryCast<Animator>() != null || c.TryCast<LODGroup>() != null) continue;
                    try { UnityEngine.Object.DestroyImmediate(c); } catch { }
                }
            }
        }
    }
}
