using System;
using System.IO;
using HarmonyLib;
using Nivalis;
using UnityEngine;

namespace NivalisCoop.Plugin
{
    /// <summary>
    /// In a server world nothing is saved into the normal single-player saves: the game's own saves and autosaves are
    /// stopped, and the session leader's game stores the world on the server instead (at most once a minute). The
    /// temporary COOP_&lt;world&gt;.sav that the upload and the download need is removed again as soon as it is not needed,
    /// so it never shows up under "Laden" or "Fortsetzen" and Steam Cloud does not keep it.
    /// </summary>
    internal static class SaveGuard
    {
        /// <summary>True while ServerWorld itself writes the file it uploads.</summary>
        public static bool OwnSave;
        const float MinSecondsBetweenUploads = 60f;
        static float _nextUpload;
        static bool _uploadWanted;

        /// <summary>Should the game's own save go ahead? Outside a server world always.</summary>
        public static bool Allow(string what)
        {
            if (OwnSave || ServerWorld.CurrentSlot == null) return true;
            var client = CoopHost.Client;
            if (client.IsLeader) { _uploadWanted = true; Plugin.Logger.LogInfo($"{what} in a server world: stored on the server instead"); }
            else Plugin.Logger.LogInfo($"{what} in a server world skipped: the session leader's game saves this world on the server");
            return false;
        }

        public static void Tick()
        {
            if (!_uploadWanted || ServerWorld.CurrentSlot == null || ServerWorld.Busy) return;
            if (!CoopHost.Client.IsLeader) { _uploadWanted = false; return; }
            if (Time.realtimeSinceStartup < _nextUpload) return;
            _uploadWanted = false;
            _nextUpload = Time.realtimeSinceStartup + MinSecondsBetweenUploads;
            ServerWorld.SaveToServer();
        }

        /// <summary>Remove the temporary save of a world (and the screenshot the game writes next to it).</summary>
        public static void DeleteLocal(string saveName)
        {
            try
            {
                SerializationManager.DeleteSaveFiles(saveName);
                foreach (var f in Directory.GetFiles(Application.persistentDataPath, saveName + ".*")) File.Delete(f);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"could not remove the temporary save {saveName}: {ex.Message}"); }
        }

        /// <summary>At start: temporary saves left behind by a crash.</summary>
        public static void CleanLeftovers()
        {
            try
            {
                foreach (var f in Directory.GetFiles(Application.persistentDataPath, "COOP_*"))
                {
                    File.Delete(f);
                    Plugin.Logger.LogInfo("removed the leftover temporary save " + Path.GetFileName(f));
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning("could not look for leftover temporary saves: " + ex.Message); }
        }
    }

    [HarmonyPatch(typeof(SerializationManager), nameof(SerializationManager.Save))]
    internal static class SavePatch
    {
        static bool Prefix(ref bool __result)
        {
            try
            {
                if (SaveGuard.Allow("save")) return true;
                __result = true; // the game shows its "saved" note; the world is on the server
                return false;
            }
            catch (Exception ex) { Plugin.Logger.LogError("Save hook: " + ex); return true; }
        }
    }

    [HarmonyPatch(typeof(SerializationManager), nameof(SerializationManager.AutoSave))]
    internal static class AutoSavePatch
    {
        static bool Prefix(ref bool __result)
        {
            try
            {
                if (SaveGuard.Allow("autosave")) return true;
                __result = true;
                return false;
            }
            catch (Exception ex) { Plugin.Logger.LogError("AutoSave hook: " + ex); return true; }
        }
    }

    [HarmonyPatch(typeof(SerializationManager), nameof(SerializationManager.SaveGlobalSave))]
    internal static class GlobalSavePatch
    {
        // the global save remembers the last game for "Fortsetzen": a server world must not end up there
        static bool Prefix()
        {
            try { return ServerWorld.CurrentSlot == null; }
            catch (Exception ex) { Plugin.Logger.LogError("SaveGlobalSave hook: " + ex); return true; }
        }
    }
}
