using System.Net.WebSockets;
using CallAutomationOpenAI;
using Azure.Communication.CallAutomation;
using System.Text;
#pragma warning disable OPENAI002

public class AcsMediaStreamingHandler
{
    private WebSocket m_webSocket;
    private CancellationTokenSource m_cts;
    private MemoryStream m_buffer;
    private VoiceRagService? m_aiServiceHandler;
    private IConfiguration m_configuration;

    public AcsMediaStreamingHandler(WebSocket webSocket, IConfiguration configuration)
    {
        m_webSocket = webSocket;
        m_configuration = configuration;
        m_buffer = new MemoryStream();
        m_cts = new CancellationTokenSource();
    }
      
    public async Task ProcessWebSocketAsync()
    {    
        if (m_webSocket == null)
        {
            return;
        }
        
        m_aiServiceHandler = new VoiceRagService(this, m_configuration);
        
        try
        {
            m_aiServiceHandler.StartConversation();
            await StartReceivingFromAcsMediaWebSocket();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Exception -> {ex}");
        }
        finally
        {
            m_aiServiceHandler?.Close();
            this.Close();
        }
    }

    public async Task SendMessageAsync(string message)
    {
        if (m_webSocket?.State == WebSocketState.Open)
        {
            byte[] jsonBytes = Encoding.UTF8.GetBytes(message);
            await m_webSocket.SendAsync(new ArraySegment<byte>(jsonBytes), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        }
    }

    public async Task CloseWebSocketAsync(WebSocketReceiveResult result)
    {
        if (result.CloseStatus.HasValue)
        {
            await m_webSocket.CloseAsync(result.CloseStatus.Value, result.CloseStatusDescription, CancellationToken.None);
        }
    }

    public async Task CloseNormalWebSocketAsync()
    {
        await m_webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Stream completed", CancellationToken.None);
    }

    public void Close()
    {
        m_cts.Cancel();
        m_cts.Dispose();
        m_buffer.Dispose();
    }

    private async Task WriteToAzOpenAIServiceInputStream(string data)
    {
        var input = AcsStreamingParser.Parse(data);
        if (input is AcsAudioPacket audioData)
        {
            if (!audioData.IsSilent && m_aiServiceHandler != null)
            {
                // Uncommment for heavy debugging
                // Console.WriteLine($"[DEBUG] Forwarding {audioData.Data.Length} bytes to AI");
                using (var ms = new MemoryStream(audioData.Data))
                {
                    await m_aiServiceHandler.SendAudioToExternalAI(ms);
                }
            }
        }
    }

    private async Task StartReceivingFromAcsMediaWebSocket()
    {
        if (m_webSocket == null)
        {
            return;
        }
        try
        {
            var buffer = new byte[1024 * 8];
            while (m_webSocket.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult receiveResult;
                do
                {
                    receiveResult = await m_webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), m_cts.Token);
                    if (receiveResult.MessageType == WebSocketMessageType.Close)
                    {
                        Console.WriteLine("ACS WebSocket closed by client.");
                        return;
                    }
                    ms.Write(buffer, 0, receiveResult.Count);
                }
                while (!receiveResult.EndOfMessage);

                ms.Seek(0, SeekOrigin.Begin);
                using var reader = new StreamReader(ms, Encoding.UTF8);
                string data = await reader.ReadToEndAsync();
                
                // Debug logging (first 50 chars)
                // Console.WriteLine($"[DEBUG] Received from ACS: {data.Substring(0, Math.Min(data.Length, 50))}...");
                
                await WriteToAzOpenAIServiceInputStream(data);               
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Exception in ACS WebSocket receiver: {ex.Message}");
        }
    }
}