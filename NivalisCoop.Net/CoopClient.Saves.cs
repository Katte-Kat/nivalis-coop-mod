using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace NivalisCoop.Net;

/// <summary>One version of a savegame the server holds.</summary>
public sealed record SaveInfo(string Slot, int Version, long Size, string Sha256, string Uploader, double Ts, string? Note);

/// <summary>Somebody else stored a new version of a server savegame.</summary>
public sealed record SaveUpdated(SaveInfo Save) : CoopEvent;

public sealed class SaveException : Exception
{
    public SaveException(string message) : base(message) { }
}

// Server-held savegames. A save is ~10 MB, so it moves in 128 KiB chunks; the network threads do the file work,
// the game thread only has to start a transfer and look at the Task.
public sealed partial class CoopClient
{
    const int SaveChunk = 128 * 1024;

    IReadOnlyList<SaveInfo> _saves = Array.Empty<SaveInfo>();
    TaskCompletionSource<JsonObject>? _saveReply;
    SaveDownload? _download;

    /// <summary>Latest version of every savegame the server holds.</summary>
    public IReadOnlyList<SaveInfo> Saves => _saves;

    public SaveInfo? SaveOf(string slot) => _saves.FirstOrDefault(s => s.Slot == slot);

    /// <summary>
    /// Store a file as the next version of a server savegame. <paramref name="baseVersion"/> is the version this
    /// game was loaded from (0 for none); if the server has a newer one meanwhile it answers "stale" and nothing is overwritten.
    /// </summary>
    public async Task<SaveInfo> UploadSaveAsync(string slot, string path, int baseVersion, string? note = null,
        Action<double>? progress = null, CancellationToken ct = default)
    {
        byte[] data = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        string sha = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        var put = new JsonObject { ["t"] = "save_put", ["slot"] = slot, ["size"] = data.Length, ["sha256"] = sha, ["base"] = baseVersion };
        if (note != null) put["note"] = note;
        await SaveStep(put, ct).ConfigureAwait(false);

        int chunks = (data.Length + SaveChunk - 1) / SaveChunk;
        for (int n = 0; n < chunks; n++)
        {
            int start = n * SaveChunk;
            Send(new JsonObject
            {
                ["t"] = "save_data", ["slot"] = slot, ["n"] = n,
                ["data"] = Convert.ToBase64String(data, start, Math.Min(SaveChunk, data.Length - start)),
            });
            progress?.Invoke((n + 1) / (double)chunks);
        }
        var stored = await SaveStep(new JsonObject { ["t"] = "save_end", ["slot"] = slot }, ct).ConfigureAwait(false);
        var info = ParseSave(stored);
        SetSave(info);
        return info;
    }

    /// <summary>Download a version (default: the newest) of a server savegame into <paramref name="destPath"/>.</summary>
    public async Task<SaveInfo> DownloadSaveAsync(string slot, string destPath, int? version = null,
        Action<double>? progress = null, CancellationToken ct = default)
    {
        if (_state != ClientState.Connected) throw new SaveException("not connected");
        var dl = new SaveDownload(destPath + ".part", progress);
        if (Interlocked.CompareExchange(ref _download, dl, null) != null) { dl.Dispose(); throw new SaveException("a download is already running"); }
        try
        {
            var get = new JsonObject { ["t"] = "save_get", ["slot"] = slot };
            if (version != null) get["version"] = version.Value;
            Send(get);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            var info = await dl.Done.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            dl.Dispose();
            File.Move(dl.Path, destPath, overwrite: true);
            return info;
        }
        catch
        {
            dl.Dispose();
            try { File.Delete(dl.Path); } catch { }
            throw;
        }
        finally { Interlocked.CompareExchange(ref _download, null, dl); }
    }

    async Task<JsonObject> SaveStep(JsonObject request, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref _saveReply, tcs, null) != null) throw new SaveException("a save transfer is already running");
        try
        {
            if (_state != ClientState.Connected) throw new SaveException("not connected");
            Send(request);
            var reply = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
            if (Str(reply["t"]) == "save_reject") throw new SaveException(Str(reply["reason"], "refused"));
            return reply;
        }
        finally { Interlocked.CompareExchange(ref _saveReply, null, tcs); }
    }

    void HandleSave(string type, JsonObject m)
    {
        switch (type)
        {
            case "save_put_ok": case "save_stored":
                _saveReply?.TrySetResult(m);
                break;
            case "save_reject":
                if (_saveReply != null) _saveReply.TrySetResult(m);
                else _download?.Done.TrySetException(new SaveException(Str(m["reason"], "refused")));
                break;
            case "save_info":
                if (_download != null) _download.Info = ParseSave(m);
                break;
            case "save_data":
                try { _download?.Write(Convert.FromBase64String(Str(m["data"])), Int(m["n"])); }
                catch (Exception ex) { _download?.Done.TrySetException(new SaveException(ex.Message)); }
                break;
            case "save_done":
                _download?.Finish();
                break;
            case "save_updated":
            {
                var info = ParseSave(m);
                SetSave(info);
                _events.Enqueue(new SaveUpdated(info));
                break;
            }
        }
    }

    void SetSave(SaveInfo info) =>
        _saves = _saves.Where(s => s.Slot != info.Slot).Append(info).OrderBy(s => s.Slot).ToArray();

    void FailSaveTransfers(string reason)
    {
        _saveReply?.TrySetException(new SaveException(reason));
        _download?.Done.TrySetException(new SaveException(reason));
    }

    static SaveInfo ParseSave(JsonObject m) => new(Str(m["slot"]), Int(m["version"]), Lng(m["size"]), Str(m["sha256"]),
        Str(m["uploader"]), Dbl(m["ts"]), m["note"] is JsonValue ? Str(m["note"]) : null);

    sealed class SaveDownload : IDisposable
    {
        readonly FileStream _file;
        readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        readonly Action<double>? _progress;
        long _received;
        int _next;

        public SaveDownload(string path, Action<double>? progress)
        {
            Path = path;
            _progress = progress;
            _file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        }

        public string Path { get; }
        public SaveInfo? Info { get; set; }
        public TaskCompletionSource<SaveInfo> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Write(byte[] chunk, int n)
        {
            if (n != _next++) throw new SaveException("chunks out of order");
            _file.Write(chunk, 0, chunk.Length);
            _hash.AppendData(chunk);
            _received += chunk.Length;
            if (Info != null && Info.Size > 0) _progress?.Invoke(Math.Min(1.0, _received / (double)Info.Size));
        }

        public void Finish()
        {
            if (Info == null) { Done.TrySetException(new SaveException("save_done without save_info")); return; }
            _file.Flush();
            string sha = Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
            if (_received != Info.Size) Done.TrySetException(new SaveException($"incomplete: {_received} of {Info.Size} bytes"));
            else if (sha != Info.Sha256) Done.TrySetException(new SaveException("checksum mismatch, the download is damaged"));
            else Done.TrySetResult(Info);
        }

        public void Dispose()
        {
            _file.Dispose();
            _hash.Dispose();
        }
    }
}
