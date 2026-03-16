using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace MultiAgent.Agents;

/// <summary>
/// Groq IChatClient adapter — FREE tier.
/// Model: llama-3.3-70b-versatile (best free tool-calling model)
/// Free: 500k tokens/day · 6k tokens/min
/// Key: console.groq.com
/// </summary>
public class GroqChatClient : IChatClient
{
    private readonly HttpClient _http;
    private readonly string _model;
    private const string BaseUrl = "https://api.groq.com/openai/v1/chat/completions";

    public ChatClientMetadata Metadata =>
        new("Groq", new Uri("https://api.groq.com"), _model);

    public GroqChatClient(string apiKey, string model = "llama-3.3-70b-versatile")
    {
        _model = model;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
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
                tools,
                tool_choice = "auto"
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
        {
            // Groq returns tool_use_failed when Llama emits XML-style function calls
            // Parse the XML ourselves and build a proper FunctionCallContent
            if (json.Contains("tool_use_failed"))
            {
                try
                {
                    using var errorDoc = JsonDocument.Parse(json);
                    var failedGen = errorDoc.RootElement
                        .GetProperty("error")
                        .GetProperty("failed_generation")
                        .GetString() ?? "";

                    // Extract function name — appears after <function= and before any of: > { space =
                    var nameMatch = System.Text.RegularExpressions.Regex.Match(
                        failedGen, @"<function=(\w+)");

                    // Extract JSON object — first { to last }
                    var jsonStart = failedGen.IndexOf('{');
                    var jsonEnd = failedGen.LastIndexOf('}');

                    if (nameMatch.Success && jsonStart >= 0 && jsonEnd > jsonStart)
                    {
                        var fnName = nameMatch.Groups[1].Value;
                        var fnArgsJson = failedGen.Substring(jsonStart, jsonEnd - jsonStart + 1);
                        var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(fnArgsJson)
                                   ?? new Dictionary<string, object?>();
                        var callId = Guid.NewGuid().ToString();
                        return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                            new List<AIContent> { new FunctionCallContent(callId, fnName, args) }));
                    }
                }
                catch { }
            }

            throw new Exception($"Groq {resp.StatusCode}: {json}");
        }

        using var doc = JsonDocument.Parse(json);
        var choice = doc.RootElement.GetProperty("choices")[0];
        var msg = choice.GetProperty("message");

        // Parse tool calls
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
            // Tool result → back to LLM
            var toolResults = m.Contents.OfType<FunctionResultContent>().ToList();
            if (toolResults.Count > 0)
            {
                foreach (var tr in toolResults)
                    result.Add(new
                    {
                        role = "tool",
                        tool_call_id = tr.CallId,
                        content = tr.Result?.ToString() ?? ""
                    });
                continue;
            }

            // Assistant tool call → preserve for conversation history
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
                            arguments = JsonSerializer.Serialize(
                                tc.Arguments ?? new Dictionary<string, object?>())
                        }
                    }).ToArray()
                });
                continue;
            }

            // Regular text
            result.Add(new
            {
                role = m.Role == ChatRole.System ? "system"
                     : m.Role == ChatRole.Assistant ? "assistant"
                     : "user",
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
            function = new
            {
                name = t.Name,
                description = t.Description ?? "",
                parameters = t.JsonSchema
            }
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