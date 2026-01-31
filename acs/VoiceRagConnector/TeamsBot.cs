using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;
using Newtonsoft.Json;
using System.Text;

namespace CallAutomationOpenAI;

public class TeamsBot : ActivityHandler
{
    private readonly HttpClient _httpClient;
    private readonly string _ragUrl;
    private readonly ILogger<TeamsBot> _logger;

    public TeamsBot(IConfiguration configuration, ILogger<TeamsBot> logger)
    {
        _httpClient = new HttpClient();
        _ragUrl = configuration.GetValue<string>("VOICERAG_REST_URL") 
                 ?? Environment.GetEnvironmentVariable("VOICERAG_REST_URL") 
                 ?? "http://localhost:8765/";
        _logger = logger;
    }

    protected override async Task OnMessageActivityAsync(ITurnContext<IMessageActivity> turnContext, CancellationToken cancellationToken)
    {
        var text = turnContext.Activity.Text;
        _logger.LogInformation($"[TeamsBot] Received message: {text}");

        if (string.IsNullOrWhiteSpace(text)) return;

        try
        {
            // 1. Forward to VoiceRag Backend
            var requestBody = JsonConvert.SerializeObject(new { question = text });
            var response = await _httpClient.PostAsync($"{_ragUrl}query", 
                new StringContent(requestBody, Encoding.UTF8, "application/json"), cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
                var result = JsonConvert.DeserializeObject<dynamic>(responseContent);
                string answer = result?.answer ?? "I received an empty response from the knowledge base.";

                // 2. Send answer back to Teams
                await turnContext.SendActivityAsync(MessageFactory.Text(answer), cancellationToken);
            }
            else
            {
                _logger.LogError($"[TeamsBot] Error from VoiceRag backend: {response.StatusCode}");
                await turnContext.SendActivityAsync(MessageFactory.Text("Sorry, I'm having trouble connecting to my knowledge base right now."), cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"[TeamsBot] Exception: {ex.Message}");
            await turnContext.SendActivityAsync(MessageFactory.Text("Oops, something went wrong on my end."), cancellationToken);
        }
    }

    protected override async Task OnMembersAddedAsync(IList<ChannelAccount> membersAdded, ITurnContext<IConversationUpdateActivity> turnContext, CancellationToken cancellationToken)
    {
        foreach (var member in membersAdded)
        {
            if (member.Id != turnContext.Activity.Recipient.Id)
            {
                await turnContext.SendActivityAsync(MessageFactory.Text("Hello! I am your VoiceRag assistant. How can I help you today?"), cancellationToken);
            }
        }
    }
}
