using Azure;
using Azure.Communication;
using Azure.Communication.Chat;
using Azure.Communication.Identity;
using System.Text;
using Newtonsoft.Json;

namespace CallAutomationOpenAI;

public class ChatService
{
    private ChatClient? _chatClient;
    private readonly HttpClient _httpClient;
    private readonly string _ragUrl;
    private readonly string _acsConnectionString;
    private readonly ILogger _logger;
    private readonly CommunicationIdentityClient _identityClient;
    private readonly string _botId;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _processedMessages = new();

    public ChatService(string acsConnectionString, string ragUrl, string botId, ILogger logger)
    {
        _acsConnectionString = acsConnectionString;
        _identityClient = new CommunicationIdentityClient(acsConnectionString);
        _httpClient = new HttpClient();
        _ragUrl = ragUrl;
        _botId = botId;
        _logger = logger;
        
        // Simple manual cleanup of old messages every time we instantiate (not efficient but safe for demo)
        foreach (var key in _processedMessages.Keys)
        {
             if (_processedMessages.TryGetValue(key, out var expiry) && DateTime.Now > expiry)
             {
                 _processedMessages.TryRemove(key, out _);
             }
        }
    }

    private async Task EnsureChatClientAsync(string threadId)
    {
        if (_chatClient == null && !string.IsNullOrEmpty(_botId))
        {
            try
            {
                var tokenResponse = await _identityClient.GetTokenAsync(new CommunicationUserIdentifier(_botId), new[] { CommunicationTokenScope.Chat });
                var endpoint = _acsConnectionString.Split(';').First(p => p.StartsWith("endpoint=")).Substring("endpoint=".Length);
                _chatClient = new ChatClient(new Uri(endpoint), new CommunicationTokenCredential(tokenResponse.Value.Token));
                _logger.LogInformation($"✅ ChatClient initialized for Bot: {_botId}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ Failed to initialize ChatClient with botId {_botId}: {ex.Message}");
                throw;
            }
        }
    }

    public async Task HandleMessageReceivedAsync(string threadId, string messageId, string senderId, string content)
    {
        _logger.LogInformation($"📨 Message event: ID={messageId}, Sender={senderId}, Bot={_botId}");
        
        // Ignore empty messages
        if (string.IsNullOrEmpty(content))
        {
            _logger.LogInformation($"⏭️ Skipping empty message");
            return;
        }
        
        // Ignore bot's own messages
        if (senderId == _botId)
        {
            _logger.LogInformation($"⏭️ Skipping bot's own message");
            return;
        }

        // Deduplication
        if (_processedMessages.ContainsKey(messageId))
        {
            _logger.LogWarning($"⚠️ Ignoring duplicate message: {messageId}");
            return;
        }
        _processedMessages.TryAdd(messageId, DateTime.Now.AddMinutes(5));

        _logger.LogInformation($"💬 Processing message from {senderId}: {content}");

        try
        {
            await EnsureChatClientAsync(threadId);

            // 1. Forward to VoiceRag Backend
            var requestBody = JsonConvert.SerializeObject(new { question = content });
            var response = await _httpClient.PostAsync($"{_ragUrl}query", new StringContent(requestBody, Encoding.UTF8, "application/json"));

            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                var result = JsonConvert.DeserializeObject<dynamic>(responseContent);
                string answer = result?.answer ?? "I received an empty response from the knowledge base.";

                // 2. Send answer back to ACS Chat Thread
                var chatThreadClient = _chatClient!.GetChatThreadClient(threadId);
                await chatThreadClient.SendMessageAsync(answer);
                _logger.LogInformation("✅ Responded to chat message.");
            }
            else
            {
                _logger.LogError($"❌ Error from VoiceRag backend: {response.StatusCode}");
                var chatThreadClient = _chatClient!.GetChatThreadClient(threadId);
                await chatThreadClient.SendMessageAsync("Sorry, I'm having trouble connecting to my knowledge base right now.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"❌ ChatService Exception: {ex.Message}");
        }
    }
}
