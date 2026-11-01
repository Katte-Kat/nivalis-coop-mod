using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Nivalis;
using Nivalis.Dialogue;
using Nivalis.GhostSystem.Ai;
using NivalisCoop.Net;
using UnityEngine;

namespace NivalisCoop.Plugin
{
    /// <summary>
    /// Conversations for everybody in the server world. The game runs a conversation deterministically from the same
    /// world state, so every client runs it itself and the server only (1) passes on that one started and (2) decides
    /// every choice from the players' votes (see server/nivalis_coop/dialogue.py for the rules).
    ///
    /// Three places in the game are touched:
    ///  * DialogueUI.DisplayChoices: the answer buttons call node.OnChoicePicked(index). The game re-uses ONE
    ///    DialogueStepDisplayData object for all steps and does not set OnChoicePicked again for each of them, so
    ///    there is exactly one dispatcher of ours on it. At every pick the dispatcher looks at what is going on right
    ///    now: during a vote the pick becomes a vote, otherwise (single answers, no vote running) the game's own
    ///    callback is called at once. (Wrapping the callback once per step, as the first version did, wrapped our own
    ///    wrapper from the second step on and swallowed every pick.)
    ///  * DialogueUI.OnDialogueStep: the first step of a conversation is reported to the server (unless it came from there).
    ///  * DialogueUI.OnDialogueEnd: the conversation is over.
    /// Outside a server world nothing is changed.
    /// </summary>
    internal static class DialogueSharing
    {
        sealed class Pending
        {
            public string Round;
            public int Options;
            public int MyVote = -1;
            public DialogueStepDisplayData Node;
            public Il2CppSystem.Action<int> Original;
            public IReadOnlyDictionary<int, int> Votes = new Dictionary<int, int>();
            public int MsLeft = -1;
            public float MsLeftAt;
        }

        static Pending _pending;
        static readonly Dictionary<string, DialogueDecided> _early = new Dictionary<string, DialogueDecided>();
        static string _currentTree;
        static string _suppressStartFor;   // a conversation we opened because somebody else started it: do not report it back
        static bool _inDialogue;
        static DialogueDecided _applyLater;
        static float _applyAt;
        static bool _loggedError;

        static float _lastActivity = -10f;
        static float _lastGameEvent;       // last step/choice/end the game reported
        static float _watchAt;             // diagnostics: did the game go on after a decision?
        static float _eventAtApply;

        static Il2CppSystem.Action<int> _real;        // the game's own callback
        static Il2CppSystem.Action<int> _dispatcher;  // ours, sits on the re-used step object
        static IntPtr _dispatcherPtr;
        static bool _applying;                        // true while WE call the game's callback for a decision

        /// <summary>A conversation is on screen (also while its vote overlay is up).</summary>
        public static bool InConversation => _inDialogue || _pending != null;

        /// <summary>Seconds since a conversation step, vote or decision happened: the Enter that confirmed the last answer must not open the chat.</summary>
        public static float SecondsSinceActivity => Time.realtimeSinceStartup - _lastActivity;

        static bool Active => CoopHost.Client.State == ClientState.Connected && ServerWorld.CurrentSlot != null;

        // ------------------------------------------------------------ game hooks (called from the patches below)
        public static void OnStep(DialogueStepDisplayData step)
        {
            _lastGameEvent = _lastActivity = Time.realtimeSinceStartup;
            if (!Active || step == null) return;
            string tree = step.DialogueTree != null ? step.DialogueTree.Guid : null;
            if (tree == null || (_inDialogue && tree == _currentTree)) return;
            _inDialogue = true;
            _currentTree = tree;
            if (_suppressStartFor == tree) { _suppressStartFor = null; return; }
            string person = step.Character != null ? step.Character.Guid : null;
            Plugin.Logger.LogInfo($"conversation started here: tree {tree}, person {person}; telling the others");
            CoopHost.Client.StartDialogue(tree, person, null);
        }

        public static void OnEnd()
        {
            _lastGameEvent = _lastActivity = Time.realtimeSinceStartup;
            _inDialogue = false;
            _currentTree = null;
            _pending = null;
            _applyLater = null;
        }

        public static void OnChoices(DialogueStepDisplayData node)
        {
            _lastGameEvent = _lastActivity = Time.realtimeSinceStartup;
            if (!Active || node == null || node.Choices == null) return;
            InstallDispatcher(node);
            int options = node.Choices.Count;
            if (options < 2 || _real == null) { _pending = null; return; }   // a single answer is never voted on

            var ids = new StringBuilder();
            for (int i = 0; i < options; i++) ids.Append(node.Choices[i].articyID).Append(',');
            string tree = node.DialogueTree != null ? node.DialogueTree.Guid : "?";
            string round = $"{ServerWorld.CurrentSlot}|{tree}|{Hash(ids.ToString())}";
            _pending = new Pending { Round = round, Options = options, Node = node, Original = _real };
            Plugin.Logger.LogInfo($"vote opened: {options} answers, round {round}");
            if (_early.TryGetValue(round, out var early))   // the others decided before this game got here
            {
                _early.Remove(round);
                _applyLater = early;
                _applyAt = Time.realtimeSinceStartup + 0.4f;
            }
        }

        /// <summary>Make sure our one dispatcher is the node's OnChoicePicked, and remember the game's own callback.</summary>
        static void InstallDispatcher(DialogueStepDisplayData node)
        {
            var current = node.OnChoicePicked;
            if (current == null) return;
            if (_dispatcher == null)
            {
                _dispatcher = (Il2CppSystem.Action<int>)new Action<int>(OnPick);
                _dispatcherPtr = _dispatcher.Pointer;
            }
            if (current.Pointer == _dispatcherPtr) return;   // still ours: the game re-uses the object
            _real = current;                                  // the game set a callback of its own: that is the real one now
            node.OnChoicePicked = _dispatcher;
        }

        /// <summary>Every answer click of the game ends up here.</summary>
        static void OnPick(int index)
        {
            var pending = _pending;
            if (_applying || pending == null || !Active)
            {
                _real?.Invoke(index);   // not a voting step (or our own call for a decision): the game goes on as usual
                return;
            }
            pending.MyVote = index;
            Plugin.Logger.LogInfo($"I vote for answer {index}");
            CoopHost.Client.VoteDialogue(pending.Round, index, pending.Options);
        }

        static string Hash(string text)
        {
            using (var sha = System.Security.Cryptography.SHA1.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                return BitConverter.ToString(bytes, 0, 5).Replace("-", "").ToLowerInvariant();
            }
        }

        // ------------------------------------------------------------ network events (game thread)
        public static void OnEvent(CoopEvent ev)
        {
            switch (ev)
            {
                case DialogueVotesChanged v when _pending != null && _pending.Round == v.Round:
                    _pending.Votes = v.Votes;
                    _pending.MsLeft = v.MsLeft;
                    _pending.MsLeftAt = Time.realtimeSinceStartup;
                    break;
                case DialogueDecided d:
                    if (_pending != null && _pending.Round == d.Round) Apply(d);
                    else
                    {
                        if (_early.Count > 20) _early.Clear();
                        _early[d.Round] = d;
                    }
                    break;
                case DialogueStarted s:
                    MirrorStart(s);
                    break;
                case DialogueRefused r:
                    Plugin.Logger.LogWarning($"vote refused: {r.Reason}");
                    break;
            }
        }

        public static void Tick()
        {
            if (_watchAt > 0 && Time.realtimeSinceStartup >= _watchAt)
            {
                _watchAt = 0;
                if (_lastGameEvent > _eventAtApply) Plugin.Logger.LogInfo("the game went on after the decision");
                else Plugin.Logger.LogWarning("the game did not report a next step after the decision (diagnostics only, nothing is repeated)");
            }
            if (_applyLater != null && Time.realtimeSinceStartup >= _applyAt)
            {
                var d = _applyLater;
                _applyLater = null;
                if (_pending != null && _pending.Round == d.Round) Apply(d);
            }
        }

        static void Apply(DialogueDecided d)
        {
            _lastActivity = Time.realtimeSinceStartup;
            var p = _pending;
            _pending = null;
            string text = d.Choice >= 0 && d.Choice < p.Options ? p.Node.Choices[d.Choice].Text : "?";
            Plugin.Logger.LogInfo($"decided: answer {d.Choice} ({(d.Drawn ? "drawn from the voted answers" : "agreed")}): {text}");
            CoopHost.Say($"{(d.Drawn ? "Drawn" : "Chosen")}: {text}");
            _eventAtApply = _lastGameEvent;
            _watchAt = Time.realtimeSinceStartup + 1.5f;
            _applying = true;
            try { p.Original.Invoke(d.Choice); }   // the game goes on exactly as if the player had clicked it
            finally { _applying = false; }
        }

        static void MirrorStart(DialogueStarted s)
        {
            if (!Active) return;
            if (_inDialogue) { Plugin.Logger.LogInfo($"conversation {s.Tree} started by another player, but one is already running here"); return; }
            try
            {
                var manager = Singleton<DialogueTreeProgressManager>.Instance;
                IDialogueTree tree;
                if (!manager.AllDialogueTrees.TryGetValue(s.Tree, out tree) || tree == null) tree = FindTree(s.Tree);
                if (tree == null) { Plugin.Logger.LogWarning("unknown conversation " + s.Tree); return; }
                Person person = null;
                if (s.Person != null && Singleton<PersonDataManager>.InstanceExist())
                    Singleton<PersonDataManager>.Instance.guidToPersons.TryGetValue(s.Person, out person);
                var args = new DialogueEventArgs { DialogueTree = tree, Character = person };
                _suppressStartFor = s.Tree;
                Plugin.Logger.LogInfo($"opening the conversation {s.Tree} that another player started");
                manager.OnDialogueRequest(args);
            }
            catch (Exception ex) { Plugin.Logger.LogError("could not open the other player's conversation: " + ex); }
        }

        // Not every conversation is in AllDialogueTrees (people's own small talks, orders ...): look through every data
        // object of the game that is a dialogue tree, by its guid.
        static readonly System.Collections.Generic.Dictionary<string, IDialogueTree> _trees = new System.Collections.Generic.Dictionary<string, IDialogueTree>();
        static float _treesScannedAt = -999f;

        static IDialogueTree FindTree(string guid)
        {
            if (_trees.TryGetValue(guid, out var t) && t != null) return t;
            if (Time.realtimeSinceStartup - _treesScannedAt < 5f) return null;
            _treesScannedAt = Time.realtimeSinceStartup;
            IntPtr iface = Il2CppInterop.Runtime.Il2CppClassPointerStore<IDialogueTree>.NativeClassPtr;
            foreach (var so in Resources.FindObjectsOfTypeAll<ScriptableObject>())
            {
                if (so == null || !Il2CppInterop.Runtime.IL2CPP.il2cpp_class_is_assignable_from(iface, Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(so.Pointer))) continue;
                var tree = new IDialogueTree(so.Pointer);
                string g = null;
                try { g = tree.Guid; } catch { }
                if (!string.IsNullOrEmpty(g)) _trees[g] = tree;
            }
            Plugin.Logger.LogInfo($"conversations: {_trees.Count} dialogue trees known");
            return _trees.TryGetValue(guid, out t) ? t : null;
        }

        // ------------------------------------------------------------ overlay: who voted for what
        static GUIStyle _head, _line;
        static int _styleHeight;

        public static void OnGUI()
        {
            var p = _pending;
            if (p == null || !Active) return;
            try
            {
                if (_styleHeight != Screen.height || _head == null)
                {
                    _styleHeight = Screen.height;
                    float k = Screen.height / 1080f;
                    _head = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(24 * k), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
                    _line = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(20 * k), alignment = TextAnchor.MiddleLeft };
                }
                var client = CoopHost.Client;
                float s = Screen.height / 1080f;
                var rows = new List<(int Id, string Name)> { (client.PlayerId, client.Name + " (you)") };
                foreach (var pl in client.Players.Values) rows.Add((pl.Id, pl.Name));
                float w = 560 * s, h = (96 + rows.Count * 30) * s;
                var box = new Rect(Screen.width - w - 24 * s, 130 * s, w, h);
                GUI.Box(box, "");
                string timer = "";
                if (p.MsLeft >= 0)
                {
                    float left = Mathf.Max(0f, p.MsLeft / 1000f - (Time.realtimeSinceStartup - p.MsLeftAt));
                    timer = $"   decides in {left:F0} s";
                }
                GUI.Label(new Rect(box.x + 14 * s, box.y + 6 * s, w - 28 * s, 34 * s), "Vote of all players" + timer, _head);
                float y = box.y + 46 * s;
                foreach (var (id, name) in rows)
                {
                    int choice = id == client.PlayerId ? p.MyVote : (p.Votes.TryGetValue(id, out var c) ? c : -1);
                    string said = choice >= 0 && choice < p.Options ? $"answer {choice + 1}: {Shorten(p.Node.Choices[choice].Text)}" : "has not voted yet";
                    GUI.Label(new Rect(box.x + 14 * s, y, w - 28 * s, 28 * s), $"{name}   {said}", _line);
                    y += 30 * s;
                }
                GUI.Label(new Rect(box.x + 14 * s, y + 4 * s, w - 28 * s, 28 * s),
                    p.MyVote < 0 ? "Click your answer" : "Waiting for the others, a draw decides if you disagree", _line);
            }
            catch (Exception ex)
            {
                if (_loggedError) return;
                _loggedError = true;
                Plugin.Logger.LogError("the vote overlay could not be drawn: " + ex);
            }
        }

        static string Shorten(string text) => text == null ? "" : (text.Length > 34 ? text.Substring(0, 33) + "…" : text);
    }

    // The patches themselves are thin: any failure must never break the game's own conversation.
    [HarmonyPatch(typeof(DialogueUI), nameof(DialogueUI.DisplayChoices))]
    internal static class DisplayChoicesPatch
    {
        static void Prefix(DialogueStepDisplayData node)
        {
            try { DialogueSharing.OnChoices(node); }
            catch (Exception ex) { Plugin.Logger.LogError("DisplayChoices hook: " + ex); }
        }
    }

    [HarmonyPatch(typeof(DialogueUI), nameof(DialogueUI.OnDialogueStep))]
    internal static class DialogueStepPatch
    {
        static void Prefix(DialogueStepDisplayData stepDisplayData)
        {
            try { DialogueSharing.OnStep(stepDisplayData); }
            catch (Exception ex) { Plugin.Logger.LogError("OnDialogueStep hook: " + ex); }
        }
    }

    [HarmonyPatch(typeof(DialogueUI), nameof(DialogueUI.OnDialogueEnd))]
    internal static class DialogueEndPatch
    {
        static void Prefix()
        {
            try { DialogueSharing.OnEnd(); }
            catch (Exception ex) { Plugin.Logger.LogError("OnDialogueEnd hook: " + ex); }
        }
    }
}
