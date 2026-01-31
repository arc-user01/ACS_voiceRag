using System.Net.WebSockets;
using System.Text;
using Newtonsoft.Json;
using Azure.Communication.CallAutomation;

namespace CallAutomationOpenAI;

public class VoiceRagService
{
    private WebSocket? m_webSocket;
    private CancellationTokenSource m_cts;
    private AcsMediaStreamingHandler m_mediaStreaming;
    private string m_url;

    public VoiceRagService(AcsMediaStreamingHandler mediaStreaming, IConfiguration configuration)
    {            
        m_mediaStreaming = mediaStreaming;
        m_cts = new CancellationTokenSource();
        // Use the correct VoiceRag Realtime WebSocket URL
        m_url = configuration.GetValue<string>("VOICERAG_URL") ?? Environment.GetEnvironmentVariable("VOICERAG_URL") ?? "ws://localhost:8765/realtime";
        
        if (string.IsNullOrEmpty(m_url))
        {
            Console.WriteLine("VOICERAG_URL not set.");
        }
    }

    public void StartConversation()
    {
        _ = Task.Run(async () => await RunWebSocketLoop());
    }

    private async Task RunWebSocketLoop()
    {
        if (string.IsNullOrEmpty(m_url)) return;
        
        m_webSocket = new ClientWebSocket();
        try 
        {
            await ((ClientWebSocket)m_webSocket).ConnectAsync(new Uri(m_url), m_cts.Token);
            Console.WriteLine($"✅ Connected to VoiceRag at {m_url}");
            
            // Send initial session update if needed (optional since RTMT handles defaults)
            var sessionUpdate = new {
                type = "session.update",
                session = new {
                    modalities = new[] { "text", "audio" },
                    instructions = "You are a helpful RAG assistant. Use the search tool to answer questions.",
                    tool_choice = "auto",
                    input_audio_format = "pcm16",
                    output_audio_format = "pcm16"
                }
            };
            await m_webSocket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(sessionUpdate))), WebSocketMessageType.Text, true, m_cts.Token);

            var buffer = new byte[1024 * 8];
            while (m_webSocket.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult receiveResult;
                do
                {
                    receiveResult = await m_webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), m_cts.Token);
                    if (receiveResult.MessageType == WebSocketMessageType.Close) break;
                    ms.Write(buffer, 0, receiveResult.Count);
                }
                while (!receiveResult.EndOfMessage);

                if (receiveResult.MessageType == WebSocketMessageType.Close) break;

                ms.Seek(0, SeekOrigin.Begin);
                using var reader = new StreamReader(ms, Encoding.UTF8);
                var jsonStr = await reader.ReadToEndAsync();
                
                if (!string.IsNullOrEmpty(jsonStr))
                {
                    var message = JsonConvert.DeserializeObject<dynamic>(jsonStr);
                    
                    if (message != null)
                    {
                        string msgType = message.type;
                        if (msgType == "response.audio.delta")
                        {
                            string base64Audio = message.delta;
                            byte[] rawAudio = Convert.FromBase64String(base64Audio);

                            // ACS works best with steady 20ms chunks.
                            // At 24kHz, 16-bit Mono: 24000 * 0.02 * 2 = 960 bytes per chunk.
                            const int chunkSize = 960;
                            for (int i = 0; i < rawAudio.Length; i += chunkSize)
                            {
                                int length = Math.Min(chunkSize, rawAudio.Length - i);
                                byte[] chunk = new byte[length];
                                Array.Copy(rawAudio, i, chunk, 0, length);
                                
                                string acsMsg = OutStreamingData.GetAudioDataForOutbound(chunk);
                                await m_mediaStreaming.SendMessageAsync(acsMsg);
                                
                                // PACE the audio playback. Sending it all at once causes silence/buffer issues in ACS.
                                await Task.Delay(18); 
                            }
                        }
                        else if (msgType == "response.audio_transcript.delta")
                        {
                            Console.WriteLine($"🤖 Bot Transcript: {message.delta}");
                        }
                        else if (msgType == "error")
                        {
                            Console.WriteLine($"❌ AI Error: {jsonStr}");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ VoiceRag WebSocket Error: {ex.Message}");
        }
    }

    public async Task SendAudioToExternalAI(MemoryStream memoryStream)
    {
        if (m_webSocket != null && m_webSocket.State == WebSocketState.Open)
        {
            byte[] pcmData = memoryStream.ToArray();
            string base64Audio = Convert.ToBase64String(pcmData);

            // Wrap in GPT-4o Realtime JSON format
            var audioAppend = new {
                type = "input_audio_buffer.append",
                audio = base64Audio
            };

            var json = JsonConvert.SerializeObject(audioAppend);
            // Console.WriteLine($"[DEBUG] Sent audio chunk to VoiceRag: {pcmData.Length} bytes");
            await m_webSocket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)), WebSocketMessageType.Text, true, m_cts.Token);
        }
    }

    public void Close()
    {
        m_cts.Cancel();
        m_cts.Dispose();
        m_webSocket?.Dispose();
    }
}
