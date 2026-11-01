using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace NivalisCoop.Net;

/// <summary>
/// Connection to a Nivalis Coop server. Two background tasks read and write the socket;
/// the game calls <see cref="Poll"/> once per frame to apply what arrived and to react to it.
/// Keepalives are answered on the network thread, so a stalling frame does not get you kicked.
/// </summary>
public sealed partial class CoopClient : IDisposable
{
    public const string ModVersion = "0.3.0";

    readonly ConcurrentQueue<CoopEvent> _events = new();
    readonly Dictionary<int, PlayerInfo> _players = new();
    readonly Dictionary<string, JsonNode?> _world = new();
    Channel<byte[]>? _outbox;
    TcpClient? _tcp;
    CancellationTokenSource? _cts;
    int _finished = 1;
    int _cid;
    long _lastMove;
    volatile ClientState _state = ClientState.Disconnected;

    public ClientState State => _state;
    public int PlayerId { get; private set; }
    public string Name { get; private set; } = "";
    public bool IsOp { get; private set; }
    public int Rev { get; private set; }
    public int TickRate { get; private set; }
    /// <summary>Server clock minus local clock in ms (from the last keepalive).</summary>
    public long ServerClockOffsetMs { get; private set; }
    public IReadOnlyDictionary<int, PlayerInfo> Players => _players;
    public IReadOnlyDictionary<string, JsonNode?> World => _world;

    // connect -------------------------------------------------------------
    public async Task<LoginResult> ConnectAsync(string host, int port, string name, string? token = null,
        string? password = null, string game = "", CancellationToken ct = default)
    {
        if (_state != ClientState.Disconnected) throw new InvalidOperationException("already connected");
        _state = ClientState.Connecting;
        _players.Clear();
        _world.Clear();
        while (_events.TryDequeue(out _)) { }

        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await tcp.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            var stream = tcp.GetStream();

            await stream.WriteAsync(Protocol.Encode(new JsonObject
            {
                ["t"] = "hello", ["proto"] = Protocol.Version, ["mod"] = "NivalisCoop/" + ModVersion,
                ["game"] = game, ["intent"] = "login",
            }), timeout.Token).ConfigureAwait(false);
            var login = new JsonObject { ["t"] = "login", ["name"] = name };
            if (token != null) login["token"] = token;
            if (password != null) login["password"] = password;
            await stream.WriteAsync(Protocol.Encode(login), timeout.Token).ConfigureAwait(false);

            var reply = await Protocol.ReadFrameAsync(stream, timeout.Token).ConfigureAwait(false);
            string type = Str(reply["t"]);
            if (type == "kick") { tcp.Dispose(); _state = ClientState.Disconnected; return LoginResult.Fail(Str(reply["reason"], "Kicked")); }
            if (type != "login_ok") throw new ProtocolException($"expected login_ok, got {type}");

            PlayerId = Int(reply["id"]);
            Name = Str(reply["name"], name);
            IsOp = reply["op"] is JsonValue op && op.TryGetValue<bool>(out var isOp) && isOp;
            TickRate = Int(reply["tick_rate"], 20);
            Rev = Int(reply["rev"]);
            if (reply["players"] is JsonArray players)
                foreach (var p in players.OfType<JsonObject>())
                    _players[Int(p["id"])] = new PlayerInfo(Int(p["id"]), Str(p["name"]), Pose.FromJson(p["pose"]));
            if (reply["state"] is JsonObject state)
                foreach (var kv in state) _world[kv.Key] = kv.Value?.DeepClone();
            _saves = (reply["saves"] as JsonArray)?.OfType<JsonObject>().Select(ParseSave).ToArray() ?? Array.Empty<SaveInfo>();

            _tcp = tcp;
            _cts = new CancellationTokenSource();
            _outbox = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
            _finished = 0;
            _state = ClientState.Connected;
            _ = Task.Run(() => ReadLoop(stream, _cts.Token));
            _ = Task.Run(() => WriteLoop(stream, _outbox.Reader, tcp));

            return new LoginResult(true, null, PlayerId, Name, reply["token"] is JsonValue t && t.TryGetValue<string>(out var tok) ? tok : null,
                IsOp, TickRate, Str(reply["server"]), Str(reply["motd"]));
        }
        catch (Exception ex)
        {
            tcp.Dispose();
            _state = ClientState.Disconnected;
            if (ct.IsCancellationRequested) throw;
            return LoginResult.Fail(ex is OperationCanceledException ? "Timed out" : ex is EndOfStreamException ? "Connection closed" : ex.Message);
        }
    }

    /// <summary>The server-list ping: no login, no slot used.</summary>
    public static async Task<ServerStatus> PingAsync(string host, int port, CancellationToken ct = default)
    {
        using var tcp = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        long began = Stopwatch.GetTimestamp();
        await tcp.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
        var stream = tcp.GetStream();
        await stream.WriteAsync(Protocol.Encode(new JsonObject
        {
            ["t"] = "hello", ["proto"] = Protocol.Version, ["mod"] = "NivalisCoop/" + ModVersion, ["intent"] = "status",
        }), timeout.Token).ConfigureAwait(false);
        var m = await Protocol.ReadFrameAsync(stream, timeout.Token).ConfigureAwait(false);
        if (Str(m["t"]) != "status") throw new ProtocolException("expected status");
        return new ServerStatus(Str(m["name"]), Str(m["motd"]), Int(m["online"]), Int(m["max"]),
            (m["players"] as JsonArray)?.Select(n => Str(n)).ToArray() ?? Array.Empty<string>(),
            Bool(m["password"]), Bool(m["whitelist"]), Int(m["proto"]), Str(m["version"]),
            Dbl(m["tps"]), Dbl(m["mspt"]), Int(m["uptime"]), Int(m["rev"]),
            (int)((Stopwatch.GetTimestamp() - began) * 1000 / Stopwatch.Frequency)); // GetElapsedTime is .NET 7+, the mod runs on 6
    }

    // game thread ---------------------------------------------------------
    /// <summary>Apply everything that arrived since the last call. Call from the game thread, once per frame.</summary>
    public void Poll(Action<CoopEvent>? handler = null)
    {
        while (_events.TryDequeue(out var ev))
        {
            switch (ev)
            {
                case PlayerJoined j: _players[j.Id] = new PlayerInfo(j.Id, j.Name, j.Pose); break;
                case PlayerLeft l: _players.Remove(l.Id); break;
                case PlayersMoved m:
                    foreach (var kv in m.Poses)
                        if (_players.TryGetValue(kv.Key, out var p)) p.Pose = kv.Value;
                    break;
                case StateChanged s:
                    foreach (var kv in s.Set) _world[kv.Key] = kv.Value;
                    foreach (var key in s.Deleted) _world.Remove(key);
                    Rev = s.Rev;
                    break;
                case Disconnected: _players.Clear(); break;
            }
            handler?.Invoke(ev);
        }
    }

    public void SendMove(Pose pose)
    {
        long now = Stopwatch.GetTimestamp();
        if (now - _lastMove < Stopwatch.Frequency / 30) return; // the server drops anything faster anyway
        _lastMove = now;
        Send(new JsonObject
        {
            ["t"] = "move", ["x"] = pose.X, ["y"] = pose.Y, ["z"] = pose.Z, ["ry"] = pose.Ry,
            ["anim"] = pose.Anim, ["scene"] = pose.Scene,
        });
    }

    public void SendChat(string text) => Send(new JsonObject { ["t"] = "chat", ["text"] = text });

    /// <summary>Write a shared-world key (no conflict check: last writer wins).</summary>
    public void SetState(string key, JsonNode value) =>
        Send(new JsonObject { ["t"] = "state_set", ["key"] = key, ["value"] = value.DeepClone() });

    /// <summary>Write only if the key is still unset: two players racing for the same order, one wins.</summary>
    public void SetStateIfAbsent(string key, JsonNode value) =>
        Send(new JsonObject { ["t"] = "state_set", ["key"] = key, ["value"] = value.DeepClone(), ["expect"] = null });

    /// <summary>Write only if the key still holds <paramref name="expected"/> (compare-and-swap).</summary>
    public void SetStateIf(string key, JsonNode value, JsonNode expected) =>
        Send(new JsonObject { ["t"] = "state_set", ["key"] = key, ["value"] = value.DeepClone(), ["expect"] = expected.DeepClone() });

    /// <summary>
    /// Add a (possibly negative) amount to a shared counter such as the shared bank account. Adds from several
    /// players are summed by the server instead of overwriting each other. Returns an operation id; the server
    /// answers with <see cref="StateAcked"/> (or <see cref="StateRejected"/>), and the new total arrives as a StateChanged.
    /// </summary>
    public int AddState(string key, int delta, string? reason = null)
    {
        int cid = Interlocked.Increment(ref _cid);
        var msg = new JsonObject { ["t"] = "state_add", ["key"] = key, ["delta"] = delta, ["cid"] = cid };
        if (reason != null) msg["reason"] = reason;
        Send(msg);
        return cid;
    }

    /// <summary>Tell the server which world you play in; shared conversations only reach players of the same world (null = none).</summary>
    public void JoinWorld(string? slot) { LeaderId = null; Send(new JsonObject { ["t"] = "join_world", ["slot"] = slot }); }

    /// <summary>You started a conversation: the others in your world are told to open the same one.</summary>
    public void StartDialogue(string tree, string? person, string? prev) =>
        Send(new JsonObject { ["t"] = "dlg_start", ["tree"] = tree, ["person"] = person, ["prev"] = prev });

    /// <summary>Vote for an answer. <paramref name="round"/> must be the same string on every client for the same choice.</summary>
    public void VoteDialogue(string round, int choice, int options) =>
        Send(new JsonObject { ["t"] = "dlg_vote", ["round"] = round, ["choice"] = choice, ["options"] = options });

    /// <summary>You are the first in this world: start its clock with your game's time (game seconds) and speed (game seconds per real second).</summary>
    public void InitTime(double seconds, double rate) =>
        Send(new JsonObject { ["t"] = "time_init", ["seconds"] = seconds, ["rate"] = rate });

    /// <summary>Hold the world's clock (a conversation) or let it run again. The server keeps one pause per player.</summary>
    public void PauseTime(bool on, string reason = "dialogue") =>
        Send(new JsonObject { ["t"] = "time_pause", ["on"] = on, ["reason"] = reason });

    /// <summary>The world jumped forward on your side (sleep, fast-forward): everybody follows. Time never runs backwards.</summary>
    public void JumpTime(double to) => Send(new JsonObject { ["t"] = "time_jump", ["to"] = to });

    /// <summary>The session leader of your world (whose game simulates its people), null outside a world.</summary>
    public int? LeaderId { get; private set; }
    public bool IsLeader => LeaderId != null && LeaderId == PlayerId;

    /// <summary>Session leader only: the people of the world. <paramref name="full"/> = false sends only what changed.</summary>
    public void SendNpcState(JsonArray npcs, int tick, bool full = true) =>
        Send(new JsonObject { ["t"] = "npc_state", ["tick"] = tick, ["full"] = full, ["npcs"] = npcs });

    /// <summary>Ask the session leader's game to do something with a person.</summary>
    public void SendNpcAction(string? npc, string action, JsonNode? data = null) =>
        Send(new JsonObject { ["t"] = "npc_act", ["npc"] = npc, ["action"] = action, ["data"] = data?.DeepClone() });

    public void DeleteState(string key) => Send(new JsonObject { ["t"] = "state_del", ["key"] = key });

    /// <summary>One-shot message to everybody else, or to one player (trade request, wave, ...).</summary>
    public void SendEvent(string kind, JsonNode? data = null, int? to = null)
    {
        var msg = new JsonObject { ["t"] = "event", ["kind"] = kind, ["data"] = data?.DeepClone() };
        if (to != null) msg["to"] = to;
        Send(msg);
    }

    public void Disconnect()
    {
        if (_state != ClientState.Connected) return;
        Send(new JsonObject { ["t"] = "bye" });
        _outbox?.Writer.TryComplete(); // the write loop flushes "bye", then half-closes; the server hangs up
    }

    public void Dispose() => Finish("Disposed");

    // network threads -----------------------------------------------------
    void Send(JsonObject msg)
    {
        if (_state != ClientState.Connected || _outbox is null) return;
        var data = Protocol.Encode(msg);
        if (data.Length - 4 > Protocol.MaxFrameToServer) return;
        _outbox.Writer.TryWrite(data);
    }

    async Task WriteLoop(NetworkStream stream, ChannelReader<byte[]> reader, TcpClient tcp)
    {
        try
        {
            await foreach (var data in reader.ReadAllAsync().ConfigureAwait(false))
                await stream.WriteAsync(data).ConfigureAwait(false);
            tcp.Client.Shutdown(SocketShutdown.Send);
        }
        catch (Exception ex) { Finish(ex.Message); }
    }

    async Task ReadLoop(NetworkStream stream, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
                Handle(await Protocol.ReadFrameAsync(stream, ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
        catch (EndOfStreamException) { Finish("Connection closed"); }
        catch (ProtocolException ex) { Finish("Protocol error: " + ex.Message); }
        catch (Exception ex) { Finish(ex.Message); }
    }

    void Handle(JsonObject m)
    {
        switch (Str(m["t"]))
        {
            case "keepalive":
                ServerClockOffsetMs = Lng(m["st"]) - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                Send(new JsonObject { ["t"] = "keepalive", ["id"] = Lng(m["id"]) });
                break;
            case "tick":
            {
                var poses = new Dictionary<int, Pose>();
                if (m["p"] is JsonObject p)
                    foreach (var kv in p)
                        if (int.TryParse(kv.Key, out int id)) poses[id] = Pose.FromJson(kv.Value);
                _events.Enqueue(new PlayersMoved(Int(m["n"]), poses));
                break;
            }
            case "player_join": _events.Enqueue(new PlayerJoined(Int(m["id"]), Str(m["name"]), Pose.FromJson(m["pose"]))); break;
            case "player_leave": _events.Enqueue(new PlayerLeft(Int(m["id"]), Str(m["name"]))); break;
            case "state":
            {
                var set = new Dictionary<string, JsonNode?>();
                if (m["set"] is JsonObject s) foreach (var kv in s) set[kv.Key] = kv.Value?.DeepClone();
                var del = (m["del"] as JsonArray)?.Select(n => Str(n)).ToList() ?? new List<string>();
                _events.Enqueue(new StateChanged(Int(m["rev"]), set, del));
                break;
            }
            case "time_need":
                _events.Enqueue(new TimeNeeded());
                break;
            case "time":
                _events.Enqueue(new TimeSync(Dbl(m["seconds"]), Dbl(m["rate"]), Bool(m["paused"]),
                    (m["reasons"] as JsonArray)?.Select(n => Str(n)).ToList() ?? new List<string>(), Bool(m["jump"])));
                break;
            case "leader":
                LeaderId = m["id"] is JsonValue ? Int(m["id"]) : null;
                _events.Enqueue(new LeaderChanged(Str(m["world"]), LeaderId, m["name"] is JsonValue ? Str(m["name"]) : null, Bool(m["handover"])));
                break;
            case "npc_state":
                _events.Enqueue(new NpcSnapshot(Int(m["from"]), Int(m["tick"]), Bool(m["full"]), (m["npcs"] as JsonArray)?.DeepClone() as JsonArray ?? new JsonArray()));
                break;
            case "npc_act":
                _events.Enqueue(new NpcAction(Int(m["from"]), m["npc"] is JsonValue ? Str(m["npc"]) : null, Str(m["action"]), m["data"]?.DeepClone()));
                break;
            case "npc_reject":
                _events.Enqueue(new NpcRefused(Str(m["reason"])));
                break;
            case "time_reject":
                _events.Enqueue(new TimeRefused(Str(m["reason"])));
                break;
            case "dlg_start":
                _events.Enqueue(new DialogueStarted(Int(m["from"]), Str(m["tree"]), m["person"] is JsonValue ? Str(m["person"]) : null, m["prev"] is JsonValue ? Str(m["prev"]) : null));
                break;
            case "dlg_votes":
                _events.Enqueue(new DialogueVotesChanged(Str(m["round"]), Int(m["options"]), IntMap(m["votes"]),
                    (m["expected"] as JsonArray)?.Select(n => Int(n)).ToList() ?? new List<int>(), Int(m["ms_left"])));
                break;
            case "dlg_result":
                _events.Enqueue(new DialogueDecided(Str(m["round"]), Int(m["choice"]), Bool(m["random"]), IntMap(m["votes"])));
                break;
            case "dlg_reject":
                _events.Enqueue(new DialogueRefused(m["round"] is JsonValue ? Str(m["round"]) : null, Str(m["reason"])));
                break;
            case "state_ack":
                _events.Enqueue(new StateAcked(Int(m["cid"]), Str(m["key"]), Lng(m["value"]), Lng(m["applied"]), Int(m["rev"])));
                break;
            case "state_reject":
                _events.Enqueue(new StateRejected(m["key"] is JsonValue ? Str(m["key"]) : null, Str(m["reason"]), m["value"]?.DeepClone()));
                break;
            case "chat": _events.Enqueue(new ChatReceived(Int(m["id"]), Str(m["name"]), Str(m["text"]))); break;
            case "event": _events.Enqueue(new GameEvent(Int(m["from"]), Str(m["kind"]), m["data"]?.DeepClone())); break;
            case "save_put_ok": case "save_stored": case "save_reject": case "save_info": case "save_data": case "save_done": case "save_updated": case "save_list":
                HandleSave(Str(m["t"]), m);
                break;
            case "kick": Finish(Str(m["reason"], "Kicked")); break;
            // unknown packet types are ignored, so a newer server stays usable
        }
    }

    void Finish(string reason)
    {
        if (Interlocked.Exchange(ref _finished, 1) == 1) return;
        _state = ClientState.Disconnected;
        _events.Enqueue(new Disconnected(reason));
        FailSaveTransfers("Disconnected: " + reason);
        try { _outbox?.Writer.TryComplete(); _cts?.Cancel(); _tcp?.Close(); } catch { }
    }

    // json helpers --------------------------------------------------------
    static IReadOnlyDictionary<int, int> IntMap(JsonNode? n)
    {
        var map = new Dictionary<int, int>();
        if (n is JsonObject o)
            foreach (var kv in o)
                if (int.TryParse(kv.Key, out int k)) map[k] = Int(kv.Value);
        return map;
    }

    static string Str(JsonNode? n, string fallback = "") => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : fallback;
    static int Int(JsonNode? n, int fallback = 0) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : fallback;
    static long Lng(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var i) ? i : 0;
    static double Dbl(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;
    static bool Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
}
