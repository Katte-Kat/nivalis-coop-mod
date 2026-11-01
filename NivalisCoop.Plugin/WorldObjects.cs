using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using HarmonyLib;
using Nivalis;
using Nivalis.GhostSystem;
using Nivalis.InventorySystem;
using NivalisCoop.Net;
using UnityEngine;

namespace NivalisCoop.Plugin
{
    /// <summary>
    /// Things that come into the world or leave it. Moving what is already there is ObjectSync; this is the rest:
    ///
    /// * Out of an inventory into the world (the "hold" button of the inventory, the furniture placement mode of a
    ///   venue): when the player puts it down, their game tells everybody "coop.spawn" with the item and where it
    ///   stands. Every other game makes the same item there from the item's own prefab, the session leader's game too,
    ///   so it is in the one save. Every game remembers which of its objects stands for the other player's one, so
    ///   later moves of it (ObjectSync, keyed by the placing player's id) find it.
    /// * From the world into an inventory (picking something up and storing it): "coop.store" with the object's id;
    ///   the others take it out of their world.
    /// </summary>
    internal static class WorldObjects
    {
        const string SpawnKind = "coop.spawn", StoreKind = "coop.store";
        static float _fromInventoryUntil;
        static readonly HashSet<IntPtr> _newHeld = new HashSet<IntPtr>();               // entities this game took out of an inventory
        static readonly Dictionary<string, HoldableEntity> _standIns = new Dictionary<string, HoldableEntity>(); // other player's id -> ours
        static bool _applying;
        public static int Spawned, Made, Stored, Removed;

        public static string Line => ServerWorld.CurrentSlot == null ? "" : $"New objects: {Spawned} placed, {Made} made for others; stored: {Stored} by us, {Removed} for others";

        static bool Active => CoopHost.Client.State == ClientState.Connected && ServerWorld.CurrentSlot != null && !_applying;

        /// <summary>The player is taking something out of an inventory: the next object in their hands is new to the world.</summary>
        public static void TakingOutOfInventory() => _fromInventoryUntil = Time.realtimeSinceStartup + 3f;

        public static void OnHold(HoldableEntity entity, bool ok)
        {
            if (!ok || entity == null || Time.realtimeSinceStartup > _fromInventoryUntil) return;
            _newHeld.Add(entity.Pointer);
        }

        // the ghosts the world had when it was loaded (plus the ones we made for others): anything placed that is not
        // among them is new, whichever menu it came from
        static readonly HashSet<string> _atLoad = new HashSet<string>();
        static string _snapWorld;
        static float _snapAt;

        public static void OnWorldLoaded() { _atLoad.Clear(); _snapWorld = null; _snapAt = Time.realtimeSinceStartup + 15f; }

        static void SnapshotGhosts()
        {
            if (_snapWorld == ServerWorld.CurrentSlot || ServerWorld.CurrentSlot == null || !GhostManager.HasInstance || Time.realtimeSinceStartup < _snapAt) return;
            try
            {
                _atLoad.Clear();
                foreach (var kv in GhostManager.Instance._ghostsDictionary) _atLoad.Add(kv.Key);
                _snapWorld = ServerWorld.CurrentSlot;
                Plugin.Logger.LogInfo($"world objects: {_atLoad.Count} ghosts in the loaded world");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning("world objects snapshot: " + ex.Message); _snapAt = Time.realtimeSinceStartup + 10f; }
        }

        static bool IsNew(HoldableEntity entity, bool fromInventory)
        {
            if (fromInventory) return !string.IsNullOrEmpty(entity.GhostId) && _announced.Add(entity.GhostId);
            string id = entity.GhostId;
            return _snapWorld == ServerWorld.CurrentSlot && !string.IsNullOrEmpty(id) && !_atLoad.Contains(id) && _announced.Add(id);
        }

        static readonly HashSet<string> _announced = new HashSet<string>();

        /// <summary>From ObjectSync when something was put down: a new object is announced before its position.</summary>
        public static void OnPlaced(HoldableEntity entity)
        {
            if (!Active || entity == null) return;
            // the game may give a new object its ghost only after it was put down: look a second later
            _checkLater.Add((entity, _newHeld.Remove(entity.Pointer), Time.realtimeSinceStartup + 1.0f));
        }

        static readonly List<(HoldableEntity E, bool FromInventory, float At)> _checkLater = new List<(HoldableEntity, bool, float)>();

        static void CheckPlaced()
        {
            for (int i = _checkLater.Count - 1; i >= 0; i--)
            {
                var (e, fromInv, at) = _checkLater[i];
                if (Time.realtimeSinceStartup < at) continue;
                _checkLater.RemoveAt(i);
                try { if (e != null && Active && IsNew(e, fromInv)) Announce(e); }
                catch (Exception ex) { Plugin.Logger.LogWarning("new object: " + ex.Message); }
            }
        }

        static void Announce(HoldableEntity entity)
        {
            var type = entity.ItemType;
            string id = entity.GhostId;
            if (type == null || string.IsNullOrEmpty(id)) { Plugin.Logger.LogWarning("a new object without item type or id: not shared"); return; }
            var t = entity.transform;
            CoopHost.Client.SendEvent(SpawnKind, new JsonObject
            {
                ["world"] = ServerWorld.CurrentSlot, ["id"] = id, ["item"] = type.name, ["scene"] = SceneOf(entity),
                ["p"] = new JsonArray(t.position.x, t.position.y, t.position.z, t.rotation.x, t.rotation.y, t.rotation.z, t.rotation.w),
            });
            Spawned++;
            Plugin.Logger.LogInfo($"new object {type.name} ({id}) placed, the others make it too");
        }

        static string _storing;

        public static void BeforeStore(PlayerObjectHolder holder) => _storing = Active ? holder.HeldObject?.GhostId : null;

        public static void AfterStore(bool ok)
        {
            string id = _storing;
            _storing = null;
            if (!ok || string.IsNullOrEmpty(id) || !Active) return;
            CoopHost.Client.SendEvent(StoreKind, new JsonObject { ["world"] = ServerWorld.CurrentSlot, ["id"] = id });
            Stored++;
            Plugin.Logger.LogInfo($"object {id} stored in our inventory, the others take it out of their world");
        }

        /// <summary>Our object for an id: one of the save, or the one we made for another player's new object.</summary>
        public static bool Find(string id, out HoldableEntity entity)
        {
            if (HoldableEntity.TryGetLoadedEntity(id, out entity) && entity != null) return true;
            if (_standIns.TryGetValue(id, out entity) && entity != null) return true;
            entity = null;
            return false;
        }

        public static void OnEvent(CoopEvent ev)
        {
            if (ev is Disconnected) { _standIns.Clear(); _newHeld.Clear(); return; }
            if (ev is not GameEvent ge || (ge.Kind != SpawnKind && ge.Kind != StoreKind) || ge.Data is not JsonObject d) return;
            if (ServerWorld.CurrentSlot == null || !(d["world"] is JsonValue w && JsonNum.S(w) == ServerWorld.CurrentSlot)) return;
            string id = JsonNum.S(d["id"]);
            if (string.IsNullOrEmpty(id)) return;
            _applying = true;
            try
            {
                if (ge.Kind == SpawnKind) Make(id, d);
                else Remove(id);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"object {id}: {ex.Message}"); }
            finally { _applying = false; }
        }

        static void Make(string id, JsonObject d)
        {
            if (Find(id, out _)) return; // we have it already
            string item = JsonNum.S(d["item"]);
            var type = FindType(item);
            if (type == null || type.EntityPrefab == null) { Plugin.Logger.LogWarning($"object {item}: no such item in this game"); return; }
            var p = d["p"] as JsonArray;
            var pos = new Vector3(F(p[0]), F(p[1]), F(p[2]));
            var rot = new Quaternion(F(p[3]), F(p[4]), F(p[5]), F(p[6]));
            // A furniture view only lives with a ghost, so do it the game's way round: make the ghost from the prefab's
            // view, put it there and register it; the GhostManager then makes the view for it (and saves the ghost).
            int scene = d["scene"] is JsonValue sv ? JsonNum.I(sv) : NpcSync.LocalGhostScene;
            var ghost = GhostFromPrefab(type, pos, rot, scene);
            if (ghost != null)
            {
                _ghostMade.Add((id, ghost.Id, type.name, Time.realtimeSinceStartup + 0.3f));
                _atLoad.Add(ghost.Id); // ours stands for theirs: never announce it as new
                Made++;
                Plugin.Logger.LogInfo($"object {item} made for another player as ghost {ghost.Id} ({id})");
                SaveGuard.RequestUpload();
                return;
            }
            var go = UnityEngine.Object.Instantiate(type.EntityPrefab.gameObject, pos, rot);
            var entity = go.GetComponentInChildren<HoldableEntity>(true);
            if (entity == null) { UnityEngine.Object.Destroy(go); Plugin.Logger.LogWarning($"object {item}: its prefab cannot be placed"); return; }
            _standIns[id] = entity;
            _pendingPlace.Add((entity, pos, rot, Time.realtimeSinceStartup + 0.5f));
            Made++;
            Plugin.Logger.LogInfo($"object {item} made for another player ({id})");
        }

        // the game sets an object up in its first frames (ghost, colliders); it is put down where it belongs after that
        static readonly List<(HoldableEntity E, Vector3 P, Quaternion R, float At)> _pendingPlace = new List<(HoldableEntity, Vector3, Quaternion, float)>();

        static readonly List<(string Remote, string Ghost, string Item, float At)> _ghostMade = new List<(string, string, string, float)>();

        static int SceneOf(HoldableEntity e)
        {
            try
            {
                if (GhostManager.HasInstance && !string.IsNullOrEmpty(e.GhostId))
                {
                    var g = GhostManager.Instance.GetGhostById(e.GhostId);
                    if (g != null) return g.SceneIndex;
                }
            }
            catch { }
            return NpcSync.LocalGhostScene;
        }

        static Ghost GhostFromPrefab(ItemType type, Vector3 pos, Quaternion rot, int scene)
        {
            if (!GhostManager.HasInstance) return null;
            IntPtr iface = Il2CppInterop.Runtime.Il2CppClassPointerStore<IGhostView>.NativeClassPtr;
            foreach (var c in type.EntityPrefab.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (c == null || !Il2CppInterop.Runtime.IL2CPP.il2cpp_class_is_assignable_from(iface, Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(c.Pointer))) continue;
                try
                {
                    var view = new IGhostView(c.Pointer);
                    var ghost = view.CreateGhostBaseFromView();
                    if (ghost == null) return null;
                    try { view.FillGhostBaseFromViewState(ghost, null); } catch (Exception ex) { Plugin.Logger.LogInfo("ghost state from the prefab: " + ex.Message); }
                    var gm = GhostManager.Instance;
                    if (string.IsNullOrEmpty(ghost.Id)) ghost.Id = GhostManager.GenerateGUID(); // the base ghost comes without ids
                    if (string.IsNullOrEmpty(ghost.ViewId)) ghost.ViewId = GhostManager.GenerateGUID();
                    if (scene >= 0) ghost.SceneIndex = scene; // the part of the city it stands in: the view is made there
                    ghost.Position = pos;
                    ghost.Rotation = rot;
                    gm.RegisterGhostImmediate(ghost);
                    gm.InstantiateGhostViewIfNeeded(ghost);
                    try { gm.UpdateGhostViewPresence(ghost); } catch (Exception ex) { Plugin.Logger.LogInfo("view presence: " + ex.Message); }
                    Plugin.Logger.LogInfo($"ghost {ghost.Id} of {type.name}: scene {ghost.SceneIndex}, prefab {ghost.PrefabId}");
                    return ghost;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"ghost from {type.name}: {ex.Message}"); return null; }
            }
            return null;
        }

        static bool _listed;

        // once per game start, for testing: a few item types that become saved world objects (their prefab has a ghost view)
        static void ListFurnitureOnce()
        {
            if (_listed || ServerWorld.CurrentSlot == null || !GhostManager.HasInstance) return;
            _listed = true;
            // a test of 0.6.0 left a ghost without an id in a world: it must not stay in the save
            try
            {
                var gm = GhostManager.Instance;
                var bad = gm.GetGhostById("");
                if (bad != null) { gm.DeregisterGhostImmediate(bad); Plugin.Logger.LogInfo("removed a ghost without an id"); SaveGuard.RequestUpload(); }
                bad = gm.GetGhostByViewId("");
                if (bad != null) { gm.DeregisterGhostImmediate(bad); Plugin.Logger.LogInfo($"removed ghost {bad.Id} without a view id"); SaveGuard.RequestUpload(); }
            }
            catch (Exception ex) { Plugin.Logger.LogInfo("looking for a ghost without an id: " + ex.Message); }
            try
            {
                IntPtr iface = Il2CppInterop.Runtime.Il2CppClassPointerStore<IGhostView>.NativeClassPtr;
                var found = new List<string>();
                foreach (var it in Resources.FindObjectsOfTypeAll<ItemType>())
                {
                    if (it?.EntityPrefab == null) continue;
                    foreach (var c in it.EntityPrefab.GetComponentsInChildren<MonoBehaviour>(true))
                        if (c != null && Il2CppInterop.Runtime.IL2CPP.il2cpp_class_is_assignable_from(iface, Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(c.Pointer)))
                        { found.Add(it.name + " (" + c.GetIl2CppType().Name + ")"); break; }
                    if (found.Count >= 12) break;
                }
                Plugin.Logger.LogInfo("items that become world objects: " + string.Join(", ", found));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning("listing world object items: " + ex.Message); }
        }

        public static void Tick()
        {
            if (Time.realtimeSinceStartup > 20f) ListFurnitureOnce();
            SnapshotGhosts();
            if (_checkLater.Count > 0) CheckPlaced();
            for (int i = _ghostMade.Count - 1; i >= 0; i--)
            {
                var (remote, ghostId, item, at) = _ghostMade[i];
                if (Time.realtimeSinceStartup < at) continue;
                if (HoldableEntity.TryGetLoadedEntity(ghostId, out var made) && made != null)
                {
                    _standIns[remote] = made;
                    _ghostMade.RemoveAt(i);
                    Plugin.Logger.LogInfo($"object {item} stands in the world ({ghostId})");
                }
                else if (Time.realtimeSinceStartup > at + 10f)
                {
                    _ghostMade.RemoveAt(i);
                    Plugin.Logger.LogWarning($"object {item}: ghost {ghostId} is registered but no view came (it is still saved)");
                }
            }
            if (_pendingPlace.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            for (int i = _pendingPlace.Count - 1; i >= 0; i--)
            {
                var (e, p, r, at) = _pendingPlace[i];
                if (now < at) continue;
                _pendingPlace.RemoveAt(i);
                if (e == null) continue;
                _applying = true;
                try
                {
                    e.Place(true, Vector3.zero, null);
                    e.MovePlaced(new UnityEngine.Pose(p, r));
                    string ghostId = EnsureGhost(e);
                    Plugin.Logger.LogInfo(ghostId != null ? $"made object is in the world as ghost {ghostId}" : "made object has no ghost: it will not be in the save");
                    SaveGuard.RequestUpload();
                }
                catch (Exception ex) { Plugin.Logger.LogWarning("placing a made object: " + ex.Message); }
                finally { _applying = false; }
            }
        }

        // A world object is a view (chair, table, decoration, machine ...) with a ghost in the GhostManager; only the
        // ghost is saved. An object made from a prefab has the view but no ghost yet: the view makes it, as the game's
        // own placement does.
        static string EnsureGhost(HoldableEntity e)
        {
            if (!string.IsNullOrEmpty(e.GhostId)) return e.GhostId;
            IntPtr iface = Il2CppInterop.Runtime.Il2CppClassPointerStore<IGhostView>.NativeClassPtr;
            foreach (var c in e.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (c == null) continue;
                IntPtr klass = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(c.Pointer);
                if (!Il2CppInterop.Runtime.IL2CPP.il2cpp_class_is_assignable_from(iface, klass)) continue;
                var view = new IGhostView(c.Pointer);
                var ghost = view.BaseGhost ?? view.CreateGhostFromView(null);
                if (ghost == null) { Plugin.Logger.LogWarning($"view {c.GetIl2CppType().Name} made no ghost"); continue; }
                if (GhostManager.HasInstance && GhostManager.Instance.GetGhostById(ghost.Id) == null) GhostManager.Instance.RegisterGhostImmediate(ghost);
                Plugin.Logger.LogInfo($"ghost {ghost.Id} made by view {c.GetIl2CppType().Name}");
                return string.IsNullOrEmpty(e.GhostId) ? ghost.Id : e.GhostId;
            }
            var names = new System.Text.StringBuilder();
            foreach (var c in e.GetComponentsInChildren<Component>(true)) if (c != null) names.Append(c.GetIl2CppType().Name).Append(' ');
            Plugin.Logger.LogInfo("no ghost view on the made object: " + names);
            return null;
        }

        static void Remove(string id)
        {
            if (!Find(id, out var entity)) return;
            _standIns.Remove(id);
            try
            {
                if (GhostManager.HasInstance)
                {
                    var ghost = string.IsNullOrEmpty(entity.GhostId) ? null : GhostManager.Instance.GetGhostById(entity.GhostId);
                    if (ghost != null) GhostManager.DeregisterGhost(ghost);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"object {id}: its ghost could not be removed: {ex.Message}"); }
            UnityEngine.Object.Destroy(entity.gameObject);
            Removed++;
            SaveGuard.RequestUpload();
            Plugin.Logger.LogInfo($"object {id} was stored by another player: taken out of our world");
        }

        public static bool Applying => _applying;

        static float F(JsonNode n) => JsonNum.F(n);

        static readonly Dictionary<string, ItemType> _types = new Dictionary<string, ItemType>();

        static ItemType FindType(string name)
        {
            if (name == null) return null;
            if (_types.TryGetValue(name, out var t) && t != null) return t;
            foreach (var it in Resources.FindObjectsOfTypeAll<ItemType>()) if (it != null && it.name == name) return _types[name] = it;
            return null;
        }
    }

    [HarmonyPatch(typeof(Nivalis.UI.InventoryItemDisplayUI), nameof(Nivalis.UI.InventoryItemDisplayUI.HoldButtonListener))]
    internal static class HoldButtonPatch
    {
        static void Prefix() { try { WorldObjects.TakingOutOfInventory(); } catch (Exception ex) { Plugin.Logger.LogError("hold button hook: " + ex); } }
    }

    [HarmonyPatch(typeof(Nivalis.UI.VenueFurnitureInventoryUI), nameof(Nivalis.UI.VenueFurnitureInventoryUI.OnPlacementModeClicked))]
    internal static class FurniturePlacementPatch
    {
        static void Prefix() { try { WorldObjects.TakingOutOfInventory(); } catch (Exception ex) { Plugin.Logger.LogError("furniture placement hook: " + ex); } }
    }

    [HarmonyPatch(typeof(PlayerObjectHolder), nameof(PlayerObjectHolder.HoldObject))]
    internal static class HoldObjectPatch
    {
        static void Postfix(HoldableEntity entity, bool __result) { try { WorldObjects.OnHold(entity, __result); } catch (Exception ex) { Plugin.Logger.LogError("hold hook: " + ex); } }
    }

    [HarmonyPatch(typeof(PlayerObjectHolder), nameof(PlayerObjectHolder.StoreEntity))]
    internal static class StoreEntityPatch
    {
        static void Prefix(PlayerObjectHolder __instance) { try { WorldObjects.BeforeStore(__instance); } catch (Exception ex) { Plugin.Logger.LogError("store hook: " + ex); } }
        static void Postfix(bool __result) { try { WorldObjects.AfterStore(__result); } catch (Exception ex) { Plugin.Logger.LogError("store hook: " + ex); } }
    }
}
