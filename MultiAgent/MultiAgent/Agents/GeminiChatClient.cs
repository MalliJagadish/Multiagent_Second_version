using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace MultiAgent.Agents;

/// <summary>
/// Google Gemini IChatClient adapter — FREE tier.
/// Model: gemini-2.0-flash
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
                generationConfig = new { maxOutputTokens = options?.MaxOutputTokens ?? 16000 }
            }
            : new
            {
                system_instruction = new { parts = new[] { new { text = sysText } } },
                contents,
                generationConfig = new { maxOutputTokens = options?.MaxOutputTokens ?? 16000 }
            };

        var body = JsonSerializer.Serialize(payload);
        var url = $"{BaseUrl}/{_model}:generateContent?key={_apiKey}";
        var resp = await _http.PostAsync(url,
            new StringContent(body, Encoding.UTF8, "application/json"), ct);
        var json = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Gemini {resp.StatusCode}: {json}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Guard: no candidates at all
        if (!root.TryGetProperty("candidates", out var candidates)
            || candidates.GetArrayLength() == 0)
        {
            // Could be a promptFeedback block (safety filter)
            var feedback = root.TryGetProperty("promptFeedback", out var pf)
                ? pf.GetRawText() : json;
            throw new Exception($"Gemini returned no candidates: {feedback}");
        }

        var candidate = candidates[0];

        // Check finishReason before parsing content
        var finishReason = candidate.TryGetProperty("finishReason", out var fr)
            ? fr.GetString() : null;

        if (!candidate.TryGetProperty("content", out var content))
            throw new Exception($"Gemini candidate has no content. finishReason={finishReason}. Raw={json[..Math.Min(json.Length, 300)]}");

        if (!content.TryGetProperty("parts", out var parts)
            || parts.GetArrayLength() == 0)
            throw new Exception($"Gemini content has no parts. finishReason={finishReason}");

        // Collect ALL function calls across parts (Gemini 2.5 can batch them)
        var functionCalls = new List<AIContent>();
        var textParts = new List<string>();

        foreach (var part in parts.EnumerateArray())
        {
            if (part.TryGetProperty("functionCall", out var fc))
            {
                var fnName = fc.GetProperty("name").GetString()!;
                var fnArgs = fc.TryGetProperty("args", out var argsEl)
                    ? argsEl.GetRawText() : "{}";
                var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(fnArgs)
                           ?? new Dictionary<string, object?>();
                functionCalls.Add(new FunctionCallContent(Guid.NewGuid().ToString(), fnName, args));
            }
            else if (part.TryGetProperty("text", out var textEl))
            {
                var t = textEl.GetString();
                if (!string.IsNullOrEmpty(t)) textParts.Add(t);
            }
            // ignore unknown part types (e.g. executableCode, codeExecutionResult)
        }

        if (functionCalls.Count > 0)
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, functionCalls));

        var text = string.Join("", textParts);
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

        // Build a lookup of callId → function name from assistant messages
        // so functionResponse can reference the correct name
        var callIdToName = new Dictionary<string, string>();
        foreach (var m in messages.Where(m => m.Role == ChatRole.Assistant))
            foreach (var fc in m.Contents.OfType<FunctionCallContent>())
                callIdToName[fc.CallId] = fc.Name;

        foreach (var m in messages.Where(m => m.Role != ChatRole.System))
        {
            // Tool results → role:user with functionResponse
            var toolResults = m.Contents.OfType<FunctionResultContent>().ToList();
            if (toolResults.Count > 0)
            {
                // Group all results into a single user turn (Gemini prefers this)
                result.Add(new
                {
                    role = "user",
                    parts = toolResults.Select(tr => (object)new
                    {
                        functionResponse = new
                        {
                            // Gemini needs the function NAME here, not the call ID
                            name = callIdToName.TryGetValue(tr.CallId, out var n) ? n : tr.CallId,
                            response = new { content = tr.Result?.ToString() ?? "" }
                        }
                    }).ToArray()
                });
                continue;
            }

            // Tool calls → role:model with functionCall
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

            // Regular text message
            var text = m.Text ?? "";
            if (string.IsNullOrWhiteSpace(text)) continue; // skip empty turns

            result.Add(new
            {
                role = m.Role == ChatRole.Assistant ? "model" : "user",
                parts = new[] { new { text } }
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