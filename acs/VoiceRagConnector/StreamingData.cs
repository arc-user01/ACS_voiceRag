using Newtonsoft.Json;

namespace CallAutomationOpenAI;

public static class AcsStreamingParser
{
    public static object? Parse(string json)
    {
        try 
        {
            var dynamicPacket = JsonConvert.DeserializeObject<dynamic>(json);
            if (dynamicPacket == null) return null;

            string? kind = (string?)dynamicPacket.kind ?? (string?)dynamicPacket.Kind;
            if (kind == "AudioData")
            {
                string? base64Data = null;
                var audioDataNode = dynamicPacket.audioData ?? dynamicPacket.AudioData;
                
                if (audioDataNode is string s)
                {
                    base64Data = s;
                }
                else if (audioDataNode != null && audioDataNode.data != null)
                {
                    base64Data = (string)audioDataNode.data;
                }

                if (!string.IsNullOrEmpty(base64Data))
                {
                    return new AcsAudioPacket 
                    { 
                       Data = Convert.FromBase64String(base64Data) 
                    };
                }
            }
            else if (kind != "KeepAlive")
            {
                // Uncommment to see non-audio packet types (metadata, etc)
                // Console.WriteLine($"[DEBUG] ACS Packet Kind: {kind}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Parser failure: {ex.Message}");
        }
        return null;
    }
}

public class AcsAudioPacket
{
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public bool IsSilent => Data == null || Data.Length == 0;
}

public static class OutStreamingData
{
    public static string GetStopAudioForOutbound()
    {
         return JsonConvert.SerializeObject(new { kind = "StopAudio" });
    }

    public static string GetAudioDataForOutbound(byte[] data)
    {
         // Modern ACS schema uses a nested object for audioData
         return JsonConvert.SerializeObject(new { 
             kind = "AudioData", 
             audioData = new {
                 data = Convert.ToBase64String(data)
             }
         });
    }
}
