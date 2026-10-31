// End-to-end test of NivalisCoop.Net against a running server.
//   dotnet run --project NivalisCoop.NetTest -- 127.0.0.1 27800
// Uses throw-away player names; the server keeps their records and one world key.
using System.Text.Json.Nodes;
using NivalisCoop.Net;

string host = args.Length > 0 ? args[0] : "127.0.0.1";
int port = args.Length > 1 ? int.Parse(args[1]) : 27800;
string run = Guid.NewGuid().ToString("N")[..6];
int failures = 0;

void Check(bool ok, string what)
{
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}");
    if (!ok) failures++;
}

Console.WriteLine($"server {host}:{port}, run {run}");

// ---- status ping -------------------------------------------------------
var status = await CoopClient.PingAsync(host, port);
Console.WriteLine($"{status.Name}: {status.Online}/{status.Max} online, {status.Tps} tps, proto {status.Proto}, {status.LatencyMs} ms");
Check(status.Proto == Protocol.Version, "status ping answers with the same protocol version");

// ---- two players -------------------------------------------------------
var a = new Probe(new CoopClient());
var b = new Probe(new CoopClient());
var ra = await a.Client.ConnectAsync(host, port, $"CsA-{run}");
var rb = await b.Client.ConnectAsync(host, port, $"CsB-{run}");
Check(ra.Ok && rb.Ok, $"both log in ({ra.Reason}{rb.Reason})");
Check(ra.NewToken != null, "first login hands out a token");
Check(a.Client.State == ClientState.Connected, "client state is Connected");

var joined = await a.Wait<PlayerJoined>(e => e.Name == $"CsB-{run}");
Check(joined != null, "A is told that B joined");
Check(a.Client.Players.ContainsKey(rb.PlayerId), "A's player table has B after Poll");

// movement
b.Client.SendMove(new Pose(1.5f, 2f, 3f, 90f, "walk", "cafe"));
var moved = await a.Wait<PlayersMoved>(e => e.Poses.ContainsKey(rb.PlayerId));
Check(moved != null && moved.Poses[rb.PlayerId] == new Pose(1.5f, 2f, 3f, 90f, "walk", "cafe"), "A sees B's pose");
Check(a.Client.Players[rb.PlayerId].Pose.Anim == "walk", "player table is updated by Poll");

// chat and events
a.Client.SendChat("hello from C#");
Check(await b.Wait<ChatReceived>(e => e.Text == "hello from C#" && e.Name == $"CsA-{run}") != null, "chat arrives");
a.Client.SendEvent("trade.request", new JsonObject { ["item"] = "tray" }, rb.PlayerId);
var ev = await b.Wait<GameEvent>(e => e.Kind == "trade.request");
Check(ev != null && ev.From == ra.PlayerId && ev.Data?["item"]?.GetValue<string>() == "tray", "targeted event arrives with its data");

// shared world: two players race for one order
string key = $"world/order/{run}";
a.Client.SetStateIfAbsent(key, "A");
await Task.Delay(100);
b.Client.SetStateIfAbsent(key, "B");
var rejected = await b.Wait<StateRejected>(e => e.Key == key);
Check(rejected != null && rejected.Reason == "conflict" && rejected.Current?.GetValue<string>() == "A", "second claim is rejected and told who won");
await a.Wait<StateChanged>(e => e.Set.ContainsKey(key));
await b.Wait<StateChanged>(e => e.Set.ContainsKey(key));
Check(a.Client.World[key]?.GetValue<string>() == "A" && b.Client.World[key]?.GetValue<string>() == "A", "both mirrors hold the winner");
a.Client.SetStateIf(key, "A-done", "A");
await b.Wait<StateChanged>(e => e.Set.TryGetValue(key, out var v) && v?.GetValue<string>() == "A-done");
Check(b.Client.World[key]?.GetValue<string>() == "A-done", "compare-and-swap with the right expectation goes through");
a.Client.DeleteState(key);
Check(await b.Wait<StateChanged>(e => e.Deleted.Contains(key)) != null && !b.Client.World.ContainsKey(key), "delete reaches the mirror");

// shared conversations: one starts, all see it; votes decide, disagreement is drawn
string world = "w" + run;
a.Client.JoinWorld(world); b.Client.JoinWorld(world);
await Task.Delay(150);
a.Client.StartDialogue("tree-guid-1", "person-guid-9", null);
var started = await b.Wait<DialogueStarted>(e => e.Tree == "tree-guid-1");
Check(started != null && started.From == ra.PlayerId && started.Person == "person-guid-9", "a conversation started by A opens for B (and carries its ids)");
Check(await a.Wait<DialogueStarted>(_ => true, 300) == null, "the starter is not sent its own conversation back");
string round = $"{world}|tree-guid-1|abc";
a.Client.VoteDialogue(round, 1, 3);
var seen = await b.Wait<DialogueVotesChanged>(e => e.Round == round);
Check(seen != null && seen.Votes.Count == 1 && seen.Votes[ra.PlayerId] == 1 && seen.MsLeft > 0, "B sees A's vote and the time left");
b.Client.VoteDialogue(round, 2, 3);
var decidedA = await a.Wait<DialogueDecided>(e => e.Round == round);
var decidedB = await b.Wait<DialogueDecided>(e => e.Round == round);
Check(decidedA != null && decidedB != null && decidedA.Choice == decidedB.Choice && decidedA.Drawn && (decidedA.Choice == 1 || decidedA.Choice == 2),
    "different votes are decided at once, the same for both, drawn from the voted answers");
a.Client.VoteDialogue(round + "|2", 0, 2); b.Client.VoteDialogue(round + "|2", 0, 2);
Check((await a.Wait<DialogueDecided>(e => e.Round == round + "|2"))?.Drawn == false, "the same vote from both is decided without a draw");
a.Client.JoinWorld(null); b.Client.JoinWorld(null);

// the world's clock: the first one starts it, the second just follows, a pause holds it, a jump moves everybody
string clockWorld = "c" + run;
// the conversation block above entered a world without a clock: both were asked for the time then, forget that
while (await a.Wait<TimeNeeded>(_ => true, 60) != null) { }
while (await b.Wait<TimeNeeded>(_ => true, 60) != null) { }
a.Client.JoinWorld(clockWorld);
Check(await a.Wait<TimeNeeded>(_ => true) != null, "the first player in a world is asked for the time");
a.Client.InitTime(86_400 * 2 + 3600 * 8, 120.0);
var first = await a.Wait<TimeSync>(e => e.Seconds > 86_400 * 2);
Check(first != null && first.Rate == 120.0 && !first.Paused, "the server's clock starts at the reported time and speed");
b.Client.JoinWorld(clockWorld);
var follow = await b.Wait<TimeSync>(_ => true);
Check(follow != null && follow.Seconds >= first!.Seconds && await b.Wait<TimeNeeded>(_ => true, 200) == null, "the second player gets the clock without being asked");
a.Client.PauseTime(true, "dialogue");
var held = await b.Wait<TimeSync>(e => e.Paused);
Check(held != null && held.Reasons.Contains("dialogue"), "a pause by one reaches the other");
a.Client.PauseTime(false);
Check(await b.Wait<TimeSync>(e => !e.Paused) != null, "ending the pause starts the clock again");
double now = (await b.Wait<TimeSync>(_ => true))!.Seconds;
a.Client.JumpTime(now + 3600);
var jumped = await b.Wait<TimeSync>(e => e.Jump);
Check(jumped != null && jumped.Seconds >= now + 3590, "a jump forward (sleep) is followed by the others");
a.Client.JoinWorld(null); b.Client.JoinWorld(null);

// shared account: both players book at once, the server sums, nobody overwrites the other
string bank = $"shared/bank/{run}";
int cidA = a.Client.AddState(bank, 500, "start");
await a.Wait<StateAcked>(e => e.Cid == cidA);
int cidA2 = a.Client.AddState(bank, 120, "sold lunch");
int cidB = b.Client.AddState(bank, -70, "bought flour");
var ackA2 = await a.Wait<StateAcked>(e => e.Cid == cidA2);
var ackB = await b.Wait<StateAcked>(e => e.Cid == cidB);
Check(ackA2 != null && ackB != null && ackA2.Applied == 120 && ackB.Applied == -70, "both bookings are applied in full");
await a.Wait<StateChanged>(e => e.Set.TryGetValue(bank, out var v) && v?.GetValue<long>() == 550);
await b.Wait<StateChanged>(e => e.Set.TryGetValue(bank, out var v) && v?.GetValue<long>() == 550);
Check(a.Client.World[bank]?.GetValue<long>() == 550 && b.Client.World[bank]?.GetValue<long>() == 550, "both mirrors show 500 + 120 - 70 = 550");
int cidOver = b.Client.AddState(bank, -2000, "too much");
var ackOver = await b.Wait<StateAcked>(e => e.Cid == cidOver);
Check(ackOver != null && ackOver.Value == 0 && ackOver.Applied == -550, "an overdraft is clamped at zero and the ack says how much was applied");

// server-held savegame: A uploads a file, B is told, B downloads the identical bytes, a stale upload is refused
string slot = "t" + run;
string srcFile = Path.Combine(Path.GetTempPath(), $"coop-{run}.sav"), dstFile = srcFile + ".down";
var payload = new byte[2_500_000 + 77];
new Random(7).NextBytes(payload);
File.WriteAllBytes(srcFile, payload);
double lastProgress = 0;
var stored = await a.Client.UploadSaveAsync(slot, srcFile, 0, "csharp test", p => lastProgress = p);
Check(stored.Version == 1 && stored.Size == payload.Length && lastProgress == 1.0, "upload stores version 1 and reports progress");
Check(a.Client.SaveOf(slot)?.Version == 1, "the uploader's list of server saves is updated");
var told = await b.Wait<SaveUpdated>(e => e.Save.Slot == slot);
Check(told != null && told.Save.Uploader == a.Client.Name && b.Client.SaveOf(slot)?.Version == 1, "the other player is told about the new version");
var got = await b.Client.DownloadSaveAsync(slot, dstFile);
Check(got.Version == 1 && File.ReadAllBytes(dstFile).AsSpan().SequenceEqual(payload), "download returns identical bytes (checksum verified)");
bool refused = false;
try { await b.Client.UploadSaveAsync(slot, srcFile, 0); } catch (SaveException ex) { refused = ex.Message.Contains("stale"); }
Check(refused, "an upload that started from an older version is refused as stale");
var second = await b.Client.UploadSaveAsync(slot, srcFile, 1);
Check(second.Version == 2, "continuing from the version it loaded works");
bool missing = false;
try { await b.Client.DownloadSaveAsync("nothere" + run, dstFile); } catch (SaveException ex) { missing = ex.Message.Contains("no such save"); }
Check(missing && !File.Exists(dstFile + ".part"), "an unknown slot is reported and leaves no half-written file behind");
File.Delete(srcFile); File.Delete(dstFile);

// not allowed outside the player prefixes
b.Client.SetState("admin/secret", 1);
Check((await b.Wait<StateRejected>(e => e.Key == "admin/secret"))?.Reason == "not allowed", "keys outside world/ and shared/ are refused");

// a late joiner gets the world with the login
b.Client.SetState($"world/late/{run}", new JsonObject { ["n"] = 1 });
await a.Wait<StateChanged>(e => e.Set.ContainsKey($"world/late/{run}"));
var c = new Probe(new CoopClient());
var rc = await c.Client.ConnectAsync(host, port, $"CsC-{run}");
Check(rc.Ok && c.Client.World.ContainsKey($"world/late/{run}"), "late joiner receives the world snapshot in login_ok");

// name protection
var thief = new CoopClient();
var rt = await thief.ConnectAsync(host, port, $"CsA-{run}");
Check(!rt.Ok && rt.Reason!.Contains("belongs to another player"), "taking somebody else's name without the token is refused");
Check(thief.State == ClientState.Disconnected, "failed login leaves the client Disconnected");

// reconnect with the token
a.Client.Disconnect();
var left = await b.Wait<PlayerLeft>(e => e.Id == ra.PlayerId);
Check(left != null, "B is told that A left");
Check((await a.Wait<Disconnected>(_ => true)) != null && a.Client.State == ClientState.Disconnected, "A's client reports Disconnected");
var a2 = new Probe(new CoopClient());
var ra2 = await a2.Client.ConnectAsync(host, port, $"CsA-{run}", ra.NewToken);
Check(ra2.Ok && ra2.NewToken == null, "A comes back with its token and gets no new one");

// refused status: a wrong protocol cannot join is covered by the server tests; here the clean exit
foreach (var p in new[] { a2, b, c }) p.Client.Disconnect();
await Task.Delay(300);

// ---- interpolator ------------------------------------------------------
Console.WriteLine("interpolator");
var it = new PoseInterpolator();
it.Add(1, 0.00, new Pose(0, 0, 0, 0, "walk", "s"));
it.Add(1, 0.10, new Pose(10, 0, 0, 350, "run", "s"));
it.TryGet(1, 0.05 + PoseInterpolator.RenderDelay, out var mid);
Check(Math.Abs(mid.X - 5) < 0.01 && mid.Anim == "run", "halfway between two samples");
Check(Math.Abs(mid.Ry - 355) < 0.01, "angles take the short way round (0 -> 350 passes 355)");
it.TryGet(1, -5, out var early); it.TryGet(1, 5, out var late);
Check(early.X == 0 && late.X == 10, "before the first sample / after the last one the pose is held");
Check(!it.TryGet(2, 0, out _), "unknown players have no pose");

Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;

/// <summary>A client plus a log of everything Poll delivered, so tests can wait for events in any order.</summary>
sealed class Probe
{
    public CoopClient Client { get; }
    readonly List<CoopEvent> _log = new();
    public Probe(CoopClient client) => Client = client;

    public async Task<T?> Wait<T>(Func<T, bool> match, int timeoutMs = 3000) where T : CoopEvent
    {
        long end = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            Client.Poll(_log.Add);
            for (int i = 0; i < _log.Count; i++)
                if (_log[i] is T hit && match(hit)) { _log.RemoveAt(i); return hit; }
            if (Environment.TickCount64 > end) return null;
            await Task.Delay(10);
        }
    }
}
