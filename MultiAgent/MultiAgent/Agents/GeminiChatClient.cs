using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace MultiAgent.Agents;

/// <summary>
/// Google Gemini IChatClient adapter — FREE tier.
/// Model: gemini-2.0-flash
/// Free: 1,500 req/day · 15 req/min
/// Key: aistudio.google.com → Get API Key
/// </summary>
public class GeminiChatClient : IChatClient
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly string _apiKey;
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models";

    public ChatClientMetadata Metadata =>
        new("Google", new Uri("https://generativelanguage.googleapis.com"), _model);

    public GeminiChatClient(string apiKey, string model = "gemini-2.0-flash")
    {
        _apiKey = apiKey;
        _model = model;
        _http = new HttpClient();
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken ct = default)
    {
        var msgList = messages.ToList();
        var sysText = string.Join("\n",
            msgList.Where(m => m.Role == ChatRole.System).Select(m => m.Text ?? ""));
        var contents = ConvertMessages(msgList);
        var tools = ConvertTools(options);

        object payload = tools != null
            ? new
            {
                system_instruction = new { parts = new[] { new { text = sysText } } },
                contents,
                tools,
                generationConfig = new { maxOutputTokens = options?.MaxOutputTokens ?? 8096 }
            }
            : new
            {
                system_instruction = new { parts = new[] { new { text = sysText } } },
                contents,
                generationConfig = new { maxOutputTokens = options?.MaxOutputTokens ?? 8096 }
            };

        var body = JsonSerializer.Serialize(payload);
        var url = $"{BaseUrl}/{_model}:generateContent?key={_apiKey}";
        var resp = await _http.PostAsync(url,
            new StringContent(body, Encoding.UTF8, "application/json"), ct);
        var json = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Gemini {resp.StatusCode}: {json}");

        using var doc = JsonDocument.Parse(json);
        var candidate = doc.RootElement.GetProperty("candidates")[0].GetProperty("content");
        var parts = candidate.GetProperty("parts");

        // Check for function calls
        foreach (var part in parts.EnumerateArray())
        {
            if (part.TryGetProperty("functionCall", out var fc))
            {
                var fnName = fc.GetProperty("name").GetString()!;
                var fnArgs = fc.GetProperty("args").GetRawText();
                var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(fnArgs)
                           ?? new Dictionary<string, object?>();
                var callId = Guid.NewGuid().ToString();
                return new ChatResponse(
                    new ChatMessage(ChatRole.Assistant,
                        new List<AIContent> { new FunctionCallContent(callId, fnName, args) }));
            }
        }

        var text = parts[0].GetProperty("text").GetString() ?? "";
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

    private static object[] ConvertMessages(List<ChatMessage> messages)
    {
        var result = new List<object>();
        foreach (var m in messages.Where(m => m.Role != ChatRole.System))
        {
            // Gemini expects functionResponse under role:"user"
            var toolResults = m.Contents.OfType<FunctionResultContent>().ToList();
            if (toolResults.Count > 0)
            {
                foreach (var tr in toolResults)
                    result.Add(new
                    {
                        role = "user",
                        parts = new object[]
                        {
                            new
                            {
                                functionResponse = new
                                {
                                    name = tr.CallId,
                                    response = new { content = tr.Result?.ToString() ?? "" }
                                }
                            }
                        }
                    });
                continue;
            }

            // Gemini expects functionCall under role:"model"
            var toolCalls = m.Contents.OfType<FunctionCallContent>().ToList();
            if (toolCalls.Count > 0)
            {
                result.Add(new
                {
                    role = "model",
                    parts = toolCalls.Select(tc => (object)new
                    {
                        functionCall = new
                        {
                            name = tc.Name,
                            args = tc.Arguments ?? new Dictionary<string, object?>()
                        }
                    }).ToArray()
                });
                continue;
            }

            result.Add(new
            {
                role = m.Role == ChatRole.Assistant ? "model" : "user",
                parts = new[] { new { text = m.Text ?? "" } }
            });
        }
        return result.ToArray();
    }

    private static object[]? ConvertTools(ChatOptions? options)
    {
        var tools = options?.Tools?.OfType<AIFunction>().ToList();
        if (tools == null || tools.Count == 0) return null;
        return new object[]
        {
            new
            {
                function_declarations = tools.Select(t => new
                {
                    name = t.Name,
                    description = t.Description ?? "",
                    parameters = new
                    {
                        type = "object",
                        properties = BuildGeminiProperties(t),
                        required = Array.Empty<string>()
                    }
                }).ToArray()
            }
        };
    }

    private static Dictionary<string, object> BuildGeminiProperties(AIFunction fn)
    {
        var schema = fn.JsonSchema;
        if (schema.TryGetProperty("properties", out var props))
        {
            return props.EnumerateObject().ToDictionary(
                p => p.Name,
                p =>
                {
                    var d = new Dictionary<string, object>();
                    d["type"] = p.Value.TryGetProperty("type", out var t) ? t.ToString() : "string";
                    if (p.Value.TryGetProperty("description", out var desc))
                        d["description"] = desc.GetString()!;
                    return (object)d;
                });
        }
        return new Dictionary<string, object>();
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceKey is null && serviceType == typeof(ChatClientMetadata)) return Metadata;
        if (serviceKey is null && serviceType.IsInstanceOfType(this)) return this;
        return null;
    }

    public void Dispose() => _http.Dispose();
}