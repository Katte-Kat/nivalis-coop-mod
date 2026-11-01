using System.Text.Json.Nodes;

namespace NivalisCoop.Plugin
{
    /// <summary>
    /// Reading numbers and texts from JSON that may have been parsed (JsonElement inside) or built in memory (an int, a
    /// float ...). GetValue&lt;double&gt;() throws on an in-memory int, so everything goes through here.
    /// </summary>
    internal static class JsonNum
    {
        public static double D(JsonNode n)
        {
            if (n is not JsonValue v) return 0;
            if (v.TryGetValue<double>(out var d)) return d;
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<long>(out var l)) return l;
            if (v.TryGetValue<float>(out var f)) return f;
            return 0;
        }

        public static int I(JsonNode n) => (int)System.Math.Round(D(n));
        public static float F(JsonNode n) => (float)D(n);
        public static bool B(JsonNode n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        public static string S(JsonNode n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }
}
