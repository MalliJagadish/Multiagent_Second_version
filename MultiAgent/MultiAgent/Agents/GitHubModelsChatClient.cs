using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace MultiAgent.Agents;

/// <summary>
/// GitHub Models IChatClient — FREE, uses existing GitHub PAT.
/// Model: openai/gpt-4.1-nano
/// Used for: ReviewAgent (different vendor = unbiased review)
/// </summary>
public class GitHubModelsChatClient : IChatClient
{
    private readonly HttpClient _http;
    private readonly string _model;
    private const string BaseUrl = "https://models.github.ai/inference/chat/completions";

    public ChatClientMetadata Metadata =>
        new("GitHubModels", new Uri("https://models.github.ai"), _model);

    public GitHubModelsChatClient(string githubPat, string model = "openai/gpt-4.1-nano")
    {
        _model = model;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {githubPat}");
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken ct = default)
    {
        var tools = ConvertTools(options);
        object payload = tools != null
            ? new
            {
                model = _model,
                max_tokens = options?.MaxOutputTokens ?? 16000,
                messages = ConvertMessages(messages),
                tools
            }
            : new
            {
                model = _model,
                max_tokens = options?.MaxOutputTokens ?? 16000,
                messages = ConvertMessages(messages)
            };

        var body = JsonSerializer.Serialize(payload);
        var resp = await _http.PostAsync(BaseUrl,
            new StringContent(body, Encoding.UTF8, "application/json"), ct);
        var json = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"GitHub Models {resp.StatusCode}: {json}");

        using var doc = JsonDocument.Parse(json);
        var choice = doc.RootElement.GetProperty("choices")[0];
        var msg = choice.GetProperty("message");

        if (msg.TryGetProperty("tool_calls", out var tcs) && tcs.GetArrayLength() > 0)
        {
            var contents = new List<AIContent>();
            foreach (var tc in tcs.EnumerateArray())
            {
                var callId = tc.GetProperty("id").GetString() ?? Guid.NewGuid().ToString();
                var fnName = tc.GetProperty("function").GetProperty("name").GetString()!;
                var fnArgs = tc.GetProperty("function").GetProperty("arguments").GetString() ?? "{}";
                var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(fnArgs)
                           ?? new Dictionary<string, object?>();
                contents.Add(new FunctionCallContent(callId, fnName, args));
            }
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, contents));
        }

        var text = msg.GetProperty("content").GetString() ?? "";
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var response = await GetResponseAsync(messages, options, ct);
        if (!string.IsNullOrEmpty(response.Text))
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
    }

    private static object[] ConvertMessages(IEnumerable<ChatMessage> messages)
    {
        var result = new List<object>();
        foreach (var m in messages)
        {
            var toolResults = m.Contents.OfType<FunctionResultContent>().ToList();
            if (toolResults.Count > 0)
            {
                foreach (var tr in toolResults)
                    result.Add(new { role = "tool", tool_call_id = tr.CallId, content = tr.Result?.ToString() ?? "" });
                continue;
            }

            var toolCalls = m.Contents.OfType<FunctionCallContent>().ToList();
            if (toolCalls.Count > 0 && m.Role == ChatRole.Assistant)
            {
                result.Add(new
                {
                    role = "assistant",
                    content = (string?)null,
                    tool_calls = toolCalls.Select(tc => new
                    {
                        id = tc.CallId,
                        type = "function",
                        function = new
                        {
                            name = tc.Name,
                            arguments = JsonSerializer.Serialize(tc.Arguments ?? new Dictionary<string, object?>())
                        }
                    }).ToArray()
                });
                continue;
            }

            result.Add(new
            {
                role = m.Role == ChatRole.System ? "system" : m.Role == ChatRole.Assistant ? "assistant" : "user",
                content = m.Text ?? ""
            });
        }
        return result.ToArray();
    }

    private static object[]? ConvertTools(ChatOptions? options)
    {
        var tools = options?.Tools?.OfType<AIFunction>().ToList();
        if (tools == null || tools.Count == 0) return null;
        return tools.Select(t => (object)new
        {
            type = "function",
            function = new { name = t.Name, description = t.Description ?? "", parameters = t.JsonSchema }
        }).ToArray();
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceKey is null && serviceType == typeof(ChatClientMetadata)) return Metadata;
        if (serviceKey is null && serviceType.IsInstanceOfType(this)) return this;
        return null;
    }

    public void Dispose() => _http.Dispose();
}