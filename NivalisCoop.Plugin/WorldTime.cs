using System;
using Nivalis;
using NivalisCoop.Net;
using UnityEngine;

namespace NivalisCoop.Plugin
{
    /// <summary>
    /// Makes the world's clock one clock for everybody in the server world (the server owns it, see
    /// server/nivalis_coop/clock.py). The game keeps its own clock in TimeOfDayManager and counts whole game seconds
    /// (TimeStamp.TotalGameSeconds). This class
    ///  * measures how fast the game's clock runs and, if the server asks (first player in the world), starts the
    ///    server clock with the local time;
    ///  * follows the server clock: when this game is behind it adds the missing seconds (TimeOfDayManager.AddTime),
    ///    when it is ahead it holds its own time with the game's pause lock until the server has caught up, because
    ///    time never runs backwards;
    ///  * reports a conversation's pause (the game stops its clock there anyway) and a jump forward (sleep) to the server.
    /// A personal menu pauses only this game: afterwards it catches up, like in Minecraft.
    /// </summary>
    internal static class WorldTime
    {
        const float TolerateSeconds = 1.5f;       // real seconds this game may differ from the server clock

        static bool _have;                         // we know the server's clock
        static double _serverSeconds, _serverRate;
        static bool _serverPaused;
        static float _serverAt;
        static bool _needAsked;                    // the server asked for our time
        static bool _firstSync;                    // the first sync after entering a world sets the time exactly (also backwards)

        // measuring the game's speed
        static float _measureReal;
        static int _measureGame;
        static double _rate;                       // game seconds per real second, 0 = not measured yet

        // our own pause
        static bool _reportedPause;
        static Il2CppSystem.Object _holdOwner;
        static OverrideableBool.OverrideLock _hold;

        // jump detection
        static int _lastLocal = -1;
        static float _lastReal;
        static float _ignoreJumpsUntil;
        static bool _wasFastForwarding;

        public static string Line { get; private set; } = "";

        static bool Active => CoopHost.Client.State == ClientState.Connected && ServerWorld.CurrentSlot != null;

        /// <summary>A server world was just loaded into this game: its time must be set from the server's clock.</summary>
        public static void OnWorldLoaded()
        {
            _firstSync = true;
            _measureReal = 0;
            _lastLocal = -1;
            ReleaseHold("world loaded");
        }

        public static void OnEvent(CoopEvent ev)
        {
            switch (ev)
            {
                case TimeNeeded _:
                    _needAsked = true;
                    break;
                case TimeSync s:
                    _have = true;
                    _serverSeconds = s.Seconds;
                    _serverRate = s.Rate;
                    _serverPaused = s.Paused;
                    _serverAt = Time.realtimeSinceStartup;
                    if (s.Jump) _jumpPending = true;
                    break;
                case TimeRefused r:
                    Plugin.Logger.LogWarning("world time refused: " + r.Reason);
                    break;
                case Disconnected _:
                    Reset("disconnected");
                    break;
            }
        }

        static bool _jumpPending;

        static void Reset(string why)
        {
            if (_have || _hold != null) Plugin.Logger.LogInfo("world time stopped: " + why);
            _have = false; _needAsked = false; _reportedPause = false; _jumpPending = false;
            ReleaseHold(why);
            Line = "";
        }

        public static void Tick()
        {
            if (!Active)
            {
                if (_have || _hold != null || _reportedPause) Reset("not in a server world");
                return;
            }
            if (!Singleton<TimeOfDayManager>.InstanceExist()) return;
            var manager = Singleton<TimeOfDayManager>.Instance;
            float now = Time.realtimeSinceStartup;
            int local = TimeOfDayManager.CurrentTime.TotalGameSeconds;
            bool paused = manager.IsPaused;
            bool fast = TimeOfDayManager.IsFastForwarding;

            // 1. a fast-forward (sleep) that ended is a jump forward for everybody
            if (_wasFastForwarding && !fast && _have && local > ServerNow(now) + 60)
            {
                Plugin.Logger.LogInfo($"world time: this game skipped time (to {local}), telling the others");
                CoopHost.Client.JumpTime(local);
                _ignoreJumpsUntil = now + 1f;
            }
            _wasFastForwarding = fast;
            if (fast) { ReleaseHold("fast forward"); _lastLocal = local; _lastReal = now; return; }

            // 2. measure the game's speed, from stretches where nothing paused or jumped
            if (paused || _measureReal == 0 || local < _measureGame || now < _ignoreJumpsUntil) { _measureReal = now; _measureGame = local; }
            else if (now - _measureReal >= 8f)
            {
                _rate = (local - _measureGame) / (double)(now - _measureReal);
                _measureReal = now; _measureGame = local;
            }

            // 3. detect a jump of this game's clock that we did not cause (the game skipping time in another way)
            if (_lastLocal >= 0 && _have && _rate > 0 && now >= _ignoreJumpsUntil && !_firstSync)
            {
                double expected = _rate * (now - _lastReal);
                if (local - _lastLocal > expected * 20 + 600)
                {
                    Plugin.Logger.LogInfo($"world time: this game's clock jumped by {local - _lastLocal} s, telling the others");
                    CoopHost.Client.JumpTime(local);
                }
            }
            _lastLocal = local; _lastReal = now;

            // 4. the server asked for the time: we are the first in this world
            if (_needAsked && _rate > 0 && !paused)
            {
                _needAsked = false;
                Plugin.Logger.LogInfo($"world time: starting the world's clock at {local} (the game runs {_rate:F2} game seconds per second)");
                CoopHost.Client.InitTime(local, _rate);
                _firstSync = false;
            }

            // 5. a conversation holds the world's clock for everybody
            bool wantPause = DialogueSharing.InConversation && paused;
            if (wantPause != _reportedPause)
            {
                _reportedPause = wantPause;
                CoopHost.Client.PauseTime(wantPause, "dialogue");
            }

            if (!_have) return;
            FollowServer(manager, local, paused, now);
        }

        static double ServerNow(float now) => _serverSeconds + (_serverPaused ? 0 : (now - _serverAt) * _serverRate);

        static void FollowServer(TimeOfDayManager manager, int local, bool paused, float now)
        {
            double serverNow = ServerNow(now);
            double diff = serverNow - local;                 // > 0: this game is behind
            double rate = _serverRate > 0 ? _serverRate : 1;

            if (_firstSync)
            {
                // entering a world whose clock is already running: take its time, backwards too (nothing has happened yet)
                _firstSync = false;
                _jumpPending = false;
                SetExactly(manager, serverNow, local);
                Line = "Time: synced with the server";
                return;
            }
            if (_jumpPending)
            {
                _jumpPending = false;
                if (diff > 0) CatchUp(manager, diff, local, "somebody slept or skipped time");
                return;
            }

            double lagSeconds = diff / rate;                 // the same in real seconds
            bool inConversation = DialogueSharing.InConversation;
            if (lagSeconds > TolerateSeconds)
            {
                ReleaseHold("behind the server");
                if (!(paused && inConversation)) CatchUp(manager, diff, local, "behind the server clock");
            }
            else if (lagSeconds < -TolerateSeconds)
            {
                if (_hold == null)
                {
                    _holdOwner = _holdOwner ?? new Il2CppSystem.Object();
                    _hold = TimeOfDayManager.Pause(_holdOwner);
                    Plugin.Logger.LogInfo($"world time: this game is {-lagSeconds:F1} s ahead of the server clock, holding it");
                }
            }
            else if (lagSeconds > -0.2) ReleaseHold("the server clock caught up");

            Line = _hold != null ? "Time: waiting for the others" : (_serverPaused ? "Time: held by a conversation" : "Time: synced with the server");
        }

        static void CatchUp(TimeOfDayManager manager, double diff, int local, string why)
        {
            _ignoreJumpsUntil = Time.realtimeSinceStartup + 1f;   // our own jump must not be reported back as a skip
            manager.AddTime((float)diff);
            int after = TimeOfDayManager.CurrentTime.TotalGameSeconds;
            Plugin.Logger.LogInfo($"world time: {why}, added {diff:F0} game seconds ({local} -> {after})");
        }

        static void SetExactly(TimeOfDayManager manager, double serverNow, int local)
        {
            _ignoreJumpsUntil = Time.realtimeSinceStartup + 1f;
            int target = (int)serverNow;
            if (target >= local) { if (target > local) manager.AddTime(target - local); }
            else manager.SetTime(new SerializableTimeStamp(new TimeOfDayManager.TimeStamp(target)));
            Plugin.Logger.LogInfo($"world time: first sync, this game had {local}, the server clock says {target} (now {TimeOfDayManager.CurrentTime.TotalGameSeconds})");
        }

        static void ReleaseHold(string why)
        {
            if (_hold == null) return;
            try { _hold.Release(); } catch (Exception) { }
            _hold = null;
            Plugin.Logger.LogInfo("world time: released the hold (" + why + ")");
        }
    }
}
