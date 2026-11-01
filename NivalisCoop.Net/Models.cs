using System.Text.Json.Nodes;

namespace NivalisCoop.Net;

public enum ClientState { Disconnected, Connecting, Connected }

/// <summary>Where a player is and what they are doing; the server relays it as [x,y,z,ry,anim,scene].</summary>
public readonly record struct Pose(float X, float Y, float Z, float Ry, string Anim, string Scene)
{
    public static Pose FromJson(JsonNode? node)
    {
        if (node is not JsonArray a || a.Count < 4) return default;
        return new Pose(F(a[0]), F(a[1]), F(a[2]), F(a[3]),
            a.Count > 4 && a[4] is JsonValue v && v.TryGetValue<string>(out var anim) ? anim : "idle",
            a.Count > 5 && a[5] is JsonValue s && s.TryGetValue<string>(out var scene) ? scene : "");
    }

    static float F(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? (float)d : 0f;
}

public sealed class PlayerInfo
{
    public PlayerInfo(int id, string name, Pose pose, string? avatar = null) { Id = id; Name = name; Pose = pose; Avatar = avatar; }
    public int Id { get; }
    public string Name { get; }
    public Pose Pose { get; internal set; }
    /// <summary>The game model this player chose to be shown as, null when they have not chosen yet.</summary>
    public string? Avatar { get; internal set; }
}

public sealed record ServerStatus(
    string Name, string Motd, int Online, int Max, string[] Players, bool PasswordRequired, bool Whitelist,
    int Proto, string Version, double Tps, double Mspt, int UptimeSeconds, int Rev, int LatencyMs);

public sealed record LoginResult(
    bool Ok, string? Reason, int PlayerId = 0, string Name = "", string? NewToken = null, bool IsOp = false,
    int TickRate = 0, string ServerName = "", string Motd = "")
{
    public static LoginResult Fail(string reason) => new(false, reason);
}

// Everything the network thread learns is queued as one of these and handed to the game
// thread by CoopClient.Poll(), so Unity objects are only ever touched from Unity's thread.
public abstract record CoopEvent;
public sealed record PlayerJoined(int Id, string Name, Pose Pose, string? Avatar = null) : CoopEvent;
/// <summary>A player chose another model to be shown as (also sent back to the one who chose).</summary>
public sealed record PlayerAvatarChanged(int Id, string Avatar) : CoopEvent;
public sealed record PlayerLeft(int Id, string Name) : CoopEvent;
public sealed record PlayersMoved(int Tick, IReadOnlyDictionary<int, Pose> Poses) : CoopEvent;
public sealed record StateChanged(int Rev, IReadOnlyDictionary<string, JsonNode?> Set, IReadOnlyList<string> Deleted) : CoopEvent;
/// <summary>Answer to <see cref="CoopClient.AddState"/>: the counter's new total and how much of the delta was applied (it can be clamped).</summary>
/// <param name="Rev">the world revision that contains this booking; until a StateChanged with Rev >= this has arrived, the total you mirror does not include it yet</param>
public sealed record StateAcked(int Cid, string Key, long Value, long Applied, int Rev) : CoopEvent;
/// <summary>Somebody in your world started a conversation; start the same one.</summary>
public sealed record DialogueStarted(int From, string Tree, string? Person, string? Prev) : CoopEvent;
/// <summary>Who voted for what in the running vote, and how long the others still have (once somebody voted).</summary>
public sealed record DialogueVotesChanged(string Round, int Options, IReadOnlyDictionary<int, int> Votes, IReadOnlyList<int> Expected, int MsLeft) : CoopEvent;
/// <summary>The server decided a choice. <paramref name="Drawn"/>: the voters disagreed and one voted answer was drawn at random.</summary>
public sealed record DialogueDecided(string Round, int Choice, bool Drawn, IReadOnlyDictionary<int, int> Votes) : CoopEvent;
public sealed record DialogueRefused(string? Round, string Reason) : CoopEvent;
/// <summary>The world has no clock yet: you are the first one here, send yours with <see cref="CoopClient.InitTime"/>.</summary>
public sealed record TimeNeeded() : CoopEvent;
/// <summary>The server's clock of your world. <paramref name="Jump"/>: somebody slept or fast-forwarded, set your time to it now.</summary>
public sealed record TimeSync(double Seconds, double Rate, bool Paused, IReadOnlyList<string> Reasons, bool Jump) : CoopEvent;
public sealed record TimeRefused(string Reason) : CoopEvent;
public sealed record StateRejected(string? Key, string Reason, JsonNode? Current) : CoopEvent;
public sealed record ChatReceived(int Id, string Name, string Text) : CoopEvent;
public sealed record GameEvent(int From, string Kind, JsonNode? Data) : CoopEvent;
/// <summary>Whose game simulates the people of your world. <paramref name="Handover"/>: the old leader left and this one takes over.</summary>
public sealed record LeaderChanged(string World, int? Id, string? Name, bool Handover) : CoopEvent;
/// <summary>The session leader's snapshot of the people in the world. <paramref name="Full"/>: false = only these changed.</summary>
public sealed record NpcSnapshot(int From, int Tick, bool Full, JsonArray Npcs) : CoopEvent;
/// <summary>To the session leader: another player wants something done with a person (talk to them, ...).</summary>
public sealed record NpcAction(int From, string? Npc, string Action, JsonNode? Data) : CoopEvent;
public sealed record NpcRefused(string Reason) : CoopEvent;
public sealed record Disconnected(string Reason) : CoopEvent;
