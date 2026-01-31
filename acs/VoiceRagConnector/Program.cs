using Azure;
using Azure.Communication;
using Azure.Communication.CallAutomation;
using Azure.Communication.Chat;
using Azure.Communication.Identity;
using Azure.Messaging;
using Azure.Messaging.EventGrid;
using Azure.Messaging.EventGrid.SystemEvents;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System.ComponentModel.DataAnnotations;
using CallAutomationOpenAI;


// Load .env file
// Load .env file
var root = Directory.GetCurrentDirectory();
var envPath = Path.Combine(root, "../.env");
Console.WriteLine($"[DEBUG] Current Directory: {root}");
Console.WriteLine($"[DEBUG] Looking for .env at: {Path.GetFullPath(envPath)}");

if (File.Exists(envPath))
{
    Console.WriteLine("[DEBUG] .env file FOUND. Parsing...");
    foreach (var line in File.ReadAllLines(envPath))
    {
        if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;
        var parts = line.Split('=', 2);
        if (parts.Length == 2)
        {
            var key = parts[0].Trim();
            var value = parts[1].Trim();
            if (value.StartsWith("\"") && value.EndsWith("\""))
            {
                value = value.Substring(1, value.Length - 2);
            }
            // Only log non-sensitive keys or just the key name
            // Console.WriteLine($"[DEBUG] Loaded Key: {key}");
            Environment.SetEnvironmentVariable(key, value);
        }
    }
}
else
{
    Console.WriteLine("[DEBUG] .env file NOT FOUND.");
}

var builder = WebApplication.CreateBuilder(args);

// Add CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

//Get ACS Connection String (prioritize Environment Variable from .env)
var acsConnectionString = Environment.GetEnvironmentVariable("ACS_CONNECTION_STRING") 
                         ?? builder.Configuration.GetValue<string>("AcsConnectionString");

CallAutomationClient client;

if (string.IsNullOrEmpty(acsConnectionString) || 
    acsConnectionString.Contains("your_acs_connection_string") || 
    acsConnectionString == "ACS_CONNECTION_STRING")
{
    Console.WriteLine("ERROR: ACS Connection String is missing or set to a placeholder.");
    return;
}
else if (!acsConnectionString.ToLower().Contains("endpoint=") || !acsConnectionString.ToLower().Contains("accesskey="))
{
    Console.WriteLine("ERROR: Invalid ACS Connection String format.");
    Console.WriteLine("Expected: endpoint=https://...;accesskey=...");
    Console.WriteLine($"Loaded value starts with: {(acsConnectionString.Length > 20 ? acsConnectionString.Substring(0, 20) : acsConnectionString)}...");
    return;
}
else 
{
    try 
    {
        client = new CallAutomationClient(acsConnectionString);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERROR initializing CallAutomationClient: {ex.Message}");
        return;
    }
}

// Ensure we have a Bot ID (check ENV then .env file then create)
var botId = Environment.GetEnvironmentVariable("ACS_BOT_ID");
if (string.IsNullOrEmpty(botId))
{
    try
    {
        var identityClient = new CommunicationIdentityClient(acsConnectionString);
        var botUser = identityClient.CreateUser();
        botId = botUser.Value.Id;
        Console.WriteLine($"\n[INFO] CREATED NEW BOT ID: {botId}");
        Console.WriteLine("[IMPORTANT] Please add this to your .env file: ACS_BOT_ID=" + botId + "\n");
        Environment.SetEnvironmentVariable("ACS_BOT_ID", botId);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"WARNING: Could not auto-generate Bot ID: {ex.Message}");
    }
}

var app = builder.Build();

// Initialize ChatService (using app.Logger to avoid ASP0000 warning)
var ragRestUrl = Environment.GetEnvironmentVariable("VOICERAG_REST_URL") ?? "http://localhost:8765/";
var chatService = new ChatService(acsConnectionString, ragRestUrl, botId ?? string.Empty, app.Logger);

var appBaseUrl = Environment.GetEnvironmentVariable("VS_TUNNEL_URL")?.TrimEnd('/');

if (string.IsNullOrEmpty(appBaseUrl))
{
    appBaseUrl = builder.Configuration.GetValue<string>("DevTunnelUri")?.TrimEnd('/');
}

if (string.IsNullOrEmpty(appBaseUrl))
{
    appBaseUrl = Environment.GetEnvironmentVariable("DEV_TUNNEL_URI")?.TrimEnd('/');
}

// Fallback to configuration if env var is null
if (string.IsNullOrEmpty(appBaseUrl))
{
    appBaseUrl = builder.Configuration.GetValue<string>("DevTunnelUri")?.TrimEnd('/');
}

if (!string.IsNullOrEmpty(appBaseUrl))
{
    Console.WriteLine($"[INFO] appBaseUrl: {appBaseUrl}");
}
else
{
    Console.WriteLine("[WARNING] appBaseUrl is not set. Check your .env file for DEV_TUNNEL_URI.");
}


app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/api/incomingCall", async (
    [FromBody] EventGridEvent[] eventGridEvents,
    ILogger<Program> logger) =>
{
    foreach (var eventGridEvent in eventGridEvents)
    {
        Console.WriteLine($"Incoming Call event received.");

        // Handle system events
        if (eventGridEvent.TryGetSystemEventData(out object eventData))
        {
            // Handle the subscription validation event.
            if (eventData is SubscriptionValidationEventData subscriptionValidationEventData)
            {
                var responseData = new SubscriptionValidationResponse
                {
                    ValidationResponse = subscriptionValidationEventData.ValidationCode
                };
                return Results.Ok(responseData);
            }
        }

        var jsonObject = Helper.GetJsonObject(eventGridEvent.Data);
        var callerId = Helper.GetCallerId(jsonObject);
        var incomingCallContext = Helper.GetIncomingCallContext(jsonObject);
        if (string.IsNullOrEmpty(appBaseUrl))
        {
            logger.LogError("appBaseUrl is null or empty. Cannot answer call.");
            return Results.BadRequest("Base URL not configured.");
        }

        var callbackUri = new Uri(new Uri(appBaseUrl), $"/api/callbacks/{Guid.NewGuid()}?callerId={callerId}");
        logger.LogInformation($"Callback Url: {callbackUri}");
        var websocketUri = appBaseUrl.Replace("https", "wss") + "/ws";
        logger.LogInformation($"WebSocket Url: {websocketUri}");

        var mediaStreamingOptions = new MediaStreamingOptions(MediaStreamingAudioChannel.Mixed)
        {
            TransportUri = new Uri(websocketUri),
            MediaStreamingContent = MediaStreamingContent.Audio,
            StartMediaStreaming = true,
            EnableBidirectional = true,
            AudioFormat = AudioFormat.Pcm24KMono
        };
      
        var options = new AnswerCallOptions(incomingCallContext, callbackUri)
        {
            MediaStreamingOptions = mediaStreamingOptions,
        };

        AnswerCallResult answerCallResult = await client.AnswerCallAsync(options);
        logger.LogInformation($"Answered call for connection id: {answerCallResult.CallConnection.CallConnectionId}");
    }
    return Results.Ok();
});

// Endpoint for ACS Chat events
app.MapPost("/api/chatEvents", async (
    [FromBody] EventGridEvent[] eventGridEvents,
    ILogger<Program> logger) =>
{
    foreach (var eventGridEvent in eventGridEvents)
    {
        // Handle subscription validation
        if (eventGridEvent.TryGetSystemEventData(out object eventData))
        {
            if (eventData is SubscriptionValidationEventData subscriptionValidationEventData)
            {
                var responseData = new SubscriptionValidationResponse
                {
                    ValidationResponse = subscriptionValidationEventData.ValidationCode
                };
                return Results.Ok(responseData);
            }
        }

        // Handle Chat Message Received
        if (eventGridEvent.EventType == "Microsoft.Communication.ChatMessageReceived")
        {
            var data = JsonConvert.DeserializeObject<dynamic>(eventGridEvent.Data.ToString());
            if (data != null)
            {
                string threadId = data.threadId ?? string.Empty;
                string messageId = data.messageId ?? string.Empty;
                string senderId = data.senderCommunicationIdentifier?.rawId ?? string.Empty;
                string messageBody = data.messageBody ?? string.Empty;

                logger.LogInformation($"🔔 Event Grid: MessageReceived - Thread={threadId}, MsgId={messageId}, Sender={senderId}");

                // Only process if it's from a real user (not the bot itself to avoid loops)
                if (!string.IsNullOrEmpty(messageBody) && !string.IsNullOrEmpty(threadId))
                {
                    await chatService.HandleMessageReceivedAsync(threadId, messageId, senderId, messageBody);
                }
                else
                {
                    logger.LogInformation($"⏭️ Skipping empty message or invalid thread");
                }
            }
        }
    }
    return Results.Ok();
});

// Helper endpoint to create a test thread since the portal doesn't always show them
app.MapGet("/api/createTestThread", async (string userId, ILogger<Program> logger) =>
{
    try
    {
        var identityClient = new CommunicationIdentityClient(acsConnectionString);
        var endpoint = acsConnectionString.Split(';').First(p => p.StartsWith("endpoint=")).Substring("endpoint=".Length);

        // Use the provided userId instead of creating a new one
        var userIdentifier = new CommunicationUserIdentifier(userId);
        var userToken = await identityClient.GetTokenAsync(userIdentifier, [CommunicationTokenScope.Chat]);
        var chatClient = new ChatClient(new Uri(endpoint), new CommunicationTokenCredential(userToken.Value.Token));
        
        // Add both the user and the bot to the thread
        var participants = new List<ChatParticipant> { 
            new ChatParticipant(userIdentifier) { DisplayName = "Tester" }
        };

        if (!string.IsNullOrEmpty(botId))
        {
            participants.Add(new ChatParticipant(new CommunicationUserIdentifier(botId)) { DisplayName = "RAG Bot" });
        }

        var threadResult = await chatClient.CreateChatThreadAsync("VoiceRag Test Thread", participants);
        
        var threadId = threadResult.Value.ChatThread.Id;
        logger.LogInformation($"✅ Created test thread: {threadId}");
        
        return Results.Ok(new {
            Message = "Thread created successfully!",
            ThreadId = threadId,
            BotId = botId,
            Instructions = "You can now send a message to this thread. Example: /api/sendChatMessage?threadId=" + threadId + "&text=Hello"
        });
    }
    catch (Exception ex)
    {
        logger.LogError($"❌ Error creating thread: {ex.Message}");
        logger.LogError($"Stack trace: {ex.StackTrace}");
        return Results.Problem($"Error: {ex.Message}");
    }
});

// Helper endpoint to send a message to a thread (since the portal UI is missing)
app.MapGet("/api/sendChatMessage", async (string threadId, string userId, string text, ILogger<Program> logger) =>
{
    try
    {
        var identityClient = new CommunicationIdentityClient(acsConnectionString);
        
        // Use the PROVIDED userId instead of creating a new one
        var userIdentifier = new CommunicationUserIdentifier(userId);
        var userToken = await identityClient.GetTokenAsync(userIdentifier, [CommunicationTokenScope.Chat]);
        
        var endpoint = acsConnectionString.Split(';').First(p => p.StartsWith("endpoint=")).Substring("endpoint=".Length);
        var chatClient = new ChatClient(new Uri(endpoint), new CommunicationTokenCredential(userToken.Value.Token));

        var chatThreadClient = chatClient.GetChatThreadClient(threadId);
        
        SendChatMessageResult sendResult;
        try 
        {
            sendResult = await chatThreadClient.SendMessageAsync(text);
        }
        catch (RequestFailedException rfe)
        {
             logger.LogError($"ACS Send Error: {rfe.Status} - {rfe.Message}");
             return Results.Problem($"ACS Error: {rfe.Message}");
        }
        
        logger.LogInformation($"✅ Sent message '{text}' to thread {threadId}");
        
        return Results.Ok(new {
            Message = "Message sent successfully!",
            MessageId = sendResult.Id,
            Note = "This should trigger your Event Grid /api/chatEvents if configured."
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

// Helper endpoint to get messages (polling for UI)
app.MapGet("/api/getThreadMessages", async (string threadId, ILogger<Program> logger) =>
{
    try
    {
        // Use a temp user to read messages? Or re-use the bot?
        // Better to use the bot since it's already in the thread.
        if (string.IsNullOrEmpty(botId)) return Results.BadRequest("Bot ID not initialized");

        var identityClient = new CommunicationIdentityClient(acsConnectionString);
        var botToken = await identityClient.GetTokenAsync(new CommunicationUserIdentifier(botId), [CommunicationTokenScope.Chat]);
        
        var endpoint = acsConnectionString.Split(';').First(p => p.StartsWith("endpoint=")).Substring("endpoint=".Length);
        var botChatClient = new ChatClient(new Uri(endpoint), new CommunicationTokenCredential(botToken.Value.Token));
        var botThreadClient = botChatClient.GetChatThreadClient(threadId);

        var messages = new List<object>();
        var messagesPage = botThreadClient.GetMessagesAsync();
        
        await foreach (var message in messagesPage)
        {
            if (messages.Count >= 20) break;
            if (message.Type == ChatMessageType.Text) // Only return text for now
            {
                messages.Add(new {
                    Id = message.Id,
                    SenderId = message.Sender.RawId,
                    Content = message.Content.Message,
                    CreatedOn = message.CreatedOn
                });
            }
        }
        
        return Results.Ok(messages);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

// api to handle call back events
app.MapPost("/api/callbacks/{contextId}", async (
    [FromBody] CloudEvent[] cloudEvents,
    [FromRoute] string contextId,
    [Required] string callerId,
    ILogger<Program> logger) =>
{
    foreach (var cloudEvent in cloudEvents)
    {
        CallAutomationEventBase @event = CallAutomationEventParser.Parse(cloudEvent);
        logger.LogInformation($"Event received: {JsonConvert.SerializeObject(@event, Formatting.Indented)}");
    }

    return Results.Ok();
});

app.UseWebSockets();

app.Use(async (context, next) =>
{
    if (context.Request.Path == "/ws")
    {
        if (context.WebSockets.IsWebSocketRequest)
        {
            try
            {
                Console.WriteLine("🔗 Attempting to accept ACS Media WebSocket...");
                var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                Console.WriteLine("✅ ACS Media WebSocket ACCEPTED. Media streaming starting.");
                
                var mediaService = new AcsMediaStreamingHandler(webSocket, builder.Configuration);

                // Set the single WebSocket connection
                await mediaService.ProcessWebSocketAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception received {ex}");
            }
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
        }
    }
    else
    {
        await next(context);
    }
});


// Endpoint for the UI to get a token and user ID
app.MapGet("/api/token", async () =>
{
    try
    {
        var identityClient = new CommunicationIdentityClient(acsConnectionString);
        var userResponse = await identityClient.CreateUserAsync();
        var tokenResponse = await identityClient.GetTokenAsync(userResponse.Value, [CommunicationTokenScope.Chat, CommunicationTokenScope.VoIP]);
        
        var endpoint = acsConnectionString.Split(';').First(p => p.StartsWith("endpoint=")).Substring("endpoint=".Length);

        return Results.Ok(new 
        { 
            Token = tokenResponse.Value.Token, 
            UserId = userResponse.Value.Id,
            Endpoint = endpoint,
            BotId = botId
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

app.Run();