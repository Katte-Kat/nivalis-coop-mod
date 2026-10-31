namespace NivalisCoop.Net;

/// <summary>
/// Smooths the 20 Hz position stream: every remote player is drawn a little in the past
/// (<see cref="RenderDelay"/>) and blended between the two samples around that moment,
/// the way Minecraft clients interpolate other entities. Times are local seconds.
/// </summary>
public sealed class PoseInterpolator
{
    public const double RenderDelay = 0.1;

    readonly Dictionary<int, List<(double T, Pose P)>> _samples = new();

    public void Add(int id, double time, Pose pose)
    {
        if (!_samples.TryGetValue(id, out var list)) _samples[id] = list = new();
        list.Add((time, pose));
        if (list.Count > 4) list.RemoveAt(0);
    }

    public void Remove(int id) => _samples.Remove(id);
    public void Clear() => _samples.Clear();

    /// <param name="now">local time in seconds; the delay is applied here</param>
    public bool TryGet(int id, double now, out Pose pose)
    {
        pose = default;
        if (!_samples.TryGetValue(id, out var list) || list.Count == 0) return false;
        double t = now - RenderDelay;
        if (t <= list[0].T) { pose = list[0].P; return true; }
        for (int i = 1; i < list.Count; i++)
        {
            if (t > list[i].T) continue;
            var (t0, a) = list[i - 1];
            var (t1, b) = list[i];
            float k = t1 > t0 ? (float)((t - t0) / (t1 - t0)) : 1f;
            pose = new Pose(
                a.X + (b.X - a.X) * k, a.Y + (b.Y - a.Y) * k, a.Z + (b.Z - a.Z) * k,
                LerpAngle(a.Ry, b.Ry, k), b.Anim, b.Scene);
            return true;
        }
        pose = list[^1].P; // no newer sample yet: hold the last known pose
        return true;
    }

    public static float LerpAngle(float a, float b, float k)
    {
        float delta = ((b - a + 540f) % 360f) - 180f;
        return (a + delta * k + 360f) % 360f;
    }
}
