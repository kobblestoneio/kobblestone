using System.Text.Json.Nodes;

namespace Kobblestone.Operator.Utils;

/// <summary>
/// JSON merge patch (RFC 7386): objects are merged recursively, all other values (including arrays) replace the
/// target's value, and <c>null</c> removes it.
/// </summary>
public static class JsonMergePatch
{
    public static JsonNode? Apply(JsonNode? target, JsonNode? patch)
    {
        if (patch is not JsonObject patchObject)
        {
            return patch?.DeepClone();
        }

        var result = target is JsonObject targetObject ? (JsonObject)targetObject.DeepClone() : new JsonObject();

        foreach (var (key, value) in patchObject)
        {
            if (value is null)
            {
                result.Remove(key);
            }
            else
            {
                result[key] = Apply(result[key], value);
            }
        }

        return result;
    }

    /// <summary>
    /// Removes all <c>null</c> properties, recursively. For patches from typed resources, where <c>null</c> means "not
    /// set" rather than "remove".
    /// </summary>
    public static JsonNode? WithoutNulls(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Where(p => p.Value is null).Select(p => p.Key).ToList())
                {
                    obj.Remove(key);
                }

                foreach (var (_, value) in obj)
                {
                    WithoutNulls(value);
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    WithoutNulls(item);
                }

                break;
        }

        return node;
    }
}
