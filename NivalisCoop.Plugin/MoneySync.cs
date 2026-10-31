using System.Collections.Generic;
using System.Text.Json.Nodes;
using Nivalis.InventorySystem;
using NivalisCoop.Net;
using UnityEngine;

namespace NivalisCoop.Plugin
{
    /// <summary>
    /// Makes the player's money one shared account kept by the server. The game keeps simulating its own restaurant;
    /// every change of <c>PlayerInventory.Money</c> is booked on the server as a +/- amount (additions from several
    /// players are summed, never overwritten) and the server's total is written back into the game.
    ///
    /// Why amounts and not "set the value": two players earn at the same moment, and both must count.
    /// Why pending operations: between sending a booking and its acknowledgement the server's total does not contain
    /// it yet, so the local value has to be total + (our bookings still on their way).
    /// </summary>
    internal static class MoneySync
    {
        /// <summary>Every world has its own account: shared/&lt;world&gt;/bank/cash</summary>
        public static string Key => "shared/" + ServerWorld.CurrentSlot + "/bank/cash";

        static PlayerInventory _inv;
        static string _slot;
        static float _nextFind, _nextApply;
        static int _local;                 // what we believe the local money is: last seen or last written
        static bool _aligned, _seedSent;
        // Bookings on their way. The server acknowledges at once, but its new total reaches us only with the next
        // state delta; until that delta (revision >= AckRev) has arrived, the total does not contain the booking yet.
        sealed class Booking { public int Amount; public int AckRev = -1; }
        static readonly Dictionary<int, Booking> _pending = new Dictionary<int, Booking>();

        public static string Line { get; private set; } = "";

        public static void OnEvent(CoopEvent ev)
        {
            switch (ev)
            {
                case StateAcked a when a.Key == Key:
                    if (_pending.TryGetValue(a.Cid, out var booking))
                    {
                        booking.AckRev = a.Rev;
                        if (booking.Amount != a.Applied)
                            Plugin.Logger.LogWarning($"money booking {a.Cid}: asked {booking.Amount}, server applied {a.Applied} (clamped at the account limits)");
                    }
                    break;
                case StateRejected r when r.Key == Key:
                    // a refusal does not say which booking it was; forget what is on its way instead of waiting for ever
                    Plugin.Logger.LogWarning($"money booking refused: {r.Reason}");
                    _pending.Clear();
                    break;
                case Disconnected _:
                    Reset("disconnected");
                    break;
            }
        }

        static void Reset(string why)
        {
            if (_aligned || _seedSent) Plugin.Logger.LogInfo("money sync stopped: " + why);
            _inv = null; _aligned = false; _seedSent = false;
            _pending.Clear();
            Line = "";
        }

        public static void Tick(CoopClient client)
        {
            if (!Plugin.SyncMoney.Value || client.State != ClientState.Connected || ServerWorld.CurrentSlot == null)
            {
                if (_aligned || _seedSent || _inv != null) Reset(!Plugin.SyncMoney.Value ? "switched off" : ServerWorld.CurrentSlot == null ? "not in a server world" : "not connected");
                return;
            }

            if (_slot != ServerWorld.CurrentSlot) { Reset("another world"); _slot = ServerWorld.CurrentSlot; }
            var inv = FindInventory();
            if (inv == null)
            {
                if (_aligned || _seedSent) Reset("no player inventory (menu or loading screen)");
                Line = "Money: waiting for a loaded game";
                return;
            }
            if (!ReferenceEquals(inv, _inv)) // a (new) save was loaded: never book its money as a change
            {
                Reset("new player inventory");
                _inv = inv;
            }

            int money = inv.Money;
            if (!_aligned) { Align(client, money); return; }

            // 1. what the game changed since we looked last becomes a booking
            if (money != _local)
            {
                int delta = money - _local;
                Plugin.Logger.LogInfo($"money sync: the game changed money by {delta:+#;-#;0} ({_local} -> {money}), booking it");
                _local = money;
                int cid = client.AddState(Key, delta, "game");
                _pending[cid] = new Booking { Amount = delta };
            }

            // 2. the server's total (plus our bookings still on their way) is what the game should show
            if (!TryTotal(client, out long total)) return;
            List<int> done = null;
            foreach (var kv in _pending)
                if (kv.Value.AckRev >= 0 && client.Rev >= kv.Value.AckRev) (done ??= new List<int>()).Add(kv.Key); // now part of the total
            if (done != null) foreach (var cid in done) _pending.Remove(cid);
            long pending = 0;
            foreach (var p in _pending.Values) pending += p.Amount;
            long desired = System.Math.Max(int.MinValue, System.Math.Min(int.MaxValue, total + pending));
            Line = $"Money: shared account {total}" + (_pending.Count > 0 ? $" (+{_pending.Count} booking(s) on the way)" : "");
            if (desired != _local && Time.realtimeSinceStartup >= _nextApply)
            {
                _nextApply = Time.realtimeSinceStartup + 0.25f; // never hammer the game if it clamps what we write
                Write(inv, (int)desired);
            }
        }

        static void Align(CoopClient client, int money)
        {
            if (TryTotal(client, out long total))
            {
                Plugin.Logger.LogInfo($"money sync: adopting the shared account ({total}); this game had {money}");
                Write(_inv, (int)System.Math.Max(int.MinValue, System.Math.Min(int.MaxValue, total)));
                _aligned = true;
                return;
            }
            if (!_seedSent)
            {
                // the account does not exist yet: the first game to arrive opens it with its own money.
                // If two arrive at once the server keeps one and the other adopts it (state_set with "expect: null").
                _seedSent = true;
                client.SetStateIfAbsent(Key, money);
                Plugin.Logger.LogInfo($"money sync: opening the shared account with {money}");
            }
            Line = "Money: opening the shared account...";
        }

        static bool TryTotal(CoopClient client, out long total)
        {
            total = 0;
            return client.World.TryGetValue(Key, out var node) && node is JsonValue v && v.TryGetValue(out total);
        }

        static void Write(PlayerInventory inv, int target)
        {
            int before = inv.Money;
            if (target == before) { _local = before; return; }
            inv.ChangeMoneyWithoutReceipt(target - before);
            _local = inv.Money; // what the game really holds now, so our own write is never mistaken for a game change
            Plugin.Logger.LogInfo($"money sync: wrote {target - before:+#;-#;0} into the game, money {before} -> {_local}");
            if (_local != target)
                Plugin.Logger.LogWarning($"money sync: wrote {target - before}, the game now shows {_local} instead of {target}");
        }

        static PlayerInventory FindInventory()
        {
            if (_inv != null) return _inv; // UnityEngine.Object compares equal to null once it was destroyed
            if (Time.realtimeSinceStartup < _nextFind) return null;
            _nextFind = Time.realtimeSinceStartup + 1f; // the search is not free
            return UnityEngine.Object.FindObjectOfType<PlayerInventory>();
        }
    }
}
