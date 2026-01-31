using System.Text.Json.Nodes;

public static class Helper
{
    public static JsonObject GetJsonObject(BinaryData data)
    {
        return JsonNode.Parse(data)?.AsObject() ?? new JsonObject();
    }
    public static string GetCallerId(JsonObject jsonObject)
    {
        return (string?)jsonObject["from"]?["rawId"] ?? "unknown";
    }

    public static string GetIncomingCallContext(JsonObject jsonObject)
    {
        return (string?)jsonObject["incomingCallContext"] ?? string.Empty;
    }
}