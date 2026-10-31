using System.Text.Json.Nodes;

namespace NivalisCoop.Net;

internal static class JsonExtensions
{
    /// <summary>
    /// JsonNode.DeepClone() exists from .NET 8 on, BepInEx runs plugins on .NET 6. Where the real
    /// instance method exists it wins over this extension, so only the mod build uses the copy via text.
    /// </summary>
    public static JsonNode? DeepClone(this JsonNode? node) => node is null ? null : JsonNode.Parse(node.ToJsonString());
}
