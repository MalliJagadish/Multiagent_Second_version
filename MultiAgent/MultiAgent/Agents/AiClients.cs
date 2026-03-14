//using Microsoft.Extensions.AI;
//using System.Text;
//using System.Text.Json;
//using System.Runtime.CompilerServices;

//namespace DevPipeline.Agents;

///// <summary>
///// Claude (Anthropic) adapter for IChatClient (Microsoft.Extensions.AI).
/////
///// CONFIRMED IChatClient interface signature (from dotnet/dotnet-api-docs):
/////   Task&lt;ChatResponse&gt; GetResponseAsync(IEnumerable&lt;ChatMessage&gt;, ChatOptions?, CancellationToken)
/////   IAsyncEnumerable&lt;ChatResponseUpdate&gt; GetStreamingResponseAsync(IEnumerable&lt;ChatMessage&gt;, ChatOptions?, CancellationToken)
/////   object? GetService(Type serviceType, object? serviceKey = null)   ← NOT generic
/////   ChatClientMetadata Metadata { get; }
///// </summary>
//public class AnthropicChatClient : IChatClient
//{
//    private readonly HttpClient _http;
//    private readonly string _model;
//    private const string BaseUrl = "https://api.anthropic.com/v1/messages";

//    public ChatClientMetadata Metadata =>
//        new("Anthropic", new Uri("https://api.anthropic.com"), _model);

//    public AnthropicChatClient(string apiKey, string model = "claude-sonnet-4-20250514")
//    {
//        _model = model;
//        _http = new HttpClient();
//        _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
//        _http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
//    }

//    public async Task<ChatResponse> GetResponseAsync(
//        IEnumerable<ChatMessage> messages,
//        ChatOptions? options = null,
//        CancellationToken cancellationToken = default)
//    {
//        var (system, msgs) = ConvertMessages(messages.ToList());
//        var body = JsonSerializer.Serialize(new
//        {
//            model = _model,
//            max_tokens = options?.MaxOutputTokens ?? 8096,
//            system,
//            messages = msgs
//        });

//        var resp = await _http.PostAsync(BaseUrl,
//            new StringContent(body, Encoding.UTF8, "application/json"), cancellationToken);
//        var json = await resp.Content.ReadAsStringAsync(cancellationToken);
//        if (!resp.IsSuccessStatusCode)
//            throw new Exception($"Claude error {resp.StatusCode}: {json}");

//        using var doc = JsonDocument.Parse(json);
//        var text = doc.RootElement
//            .GetProperty("content")[0]
//            .GetProperty("text").GetString() ?? "";

//        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text));
//    }

//    // CS1626 fix: yield cannot be inside try/catch.
//    // Collect all tokens in a regular Task first, then yield them outside try/catch.
//    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
//        IEnumerable<ChatMessage> messages,
//        ChatOptions? options = null,
//        [EnumeratorCancellation] CancellationToken cancellationToken = default)
//    {
//        var tokens = await CollectTokensAsync(messages, options, cancellationToken);
//        foreach (var token in tokens)
//            yield return new ChatResponseUpdate(ChatRole.Assistant, token);
//    }

//    private async Task<List<string>> CollectTokensAsync(
//        IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken ct)
//    {
//        var (system, msgs) = ConvertMessages(messages.ToList());
//        var body = JsonSerializer.Serialize(new
//        {
//            model = _model,
//            max_tokens = options?.MaxOutputTokens ?? 8096,
//            system,
//            messages = msgs,
//            stream = true
//        });

//        var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl)
//        {
//            Content = new StringContent(body, Encoding.UTF8, "application/json")
//        };

//        var tokens = new List<string>();
//        try
//        {
//            using var resp = await _http.SendAsync(req,
//                HttpCompletionOption.ResponseHeadersRead, ct);
//            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
//            using var reader = new StreamReader(stream);

//            while (!ct.IsCancellationRequested)
//            {
//                var line = await reader.ReadLineAsync(ct);
//                if (line == null) break;
//                if (!line.StartsWith("data: ")) continue;
//                var data = line["data: ".Length..];
//                if (data == "[DONE]") break;
//                try
//                {
//                    using var doc = JsonDocument.Parse(data);
//                    var root = doc.RootElement;
//                    if (root.TryGetProperty("type", out var t)
//                        && t.GetString() == "content_block_delta"
//                        && root.TryGetProperty("delta", out var d)
//                        && d.TryGetProperty("text", out var txt))
//                    {
//                        var token = txt.GetString() ?? "";
//                        if (!string.IsNullOrEmpty(token)) tokens.Add(token);
//                    }
//                }
//                catch { /* skip malformed SSE line */ }
//            }
//        }
//        catch (Exception ex) { tokens.Add($"\n[Claude error: {ex.Message}]"); }

//        return tokens;
//    }

//    private static (string system, object[] messages) ConvertMessages(List<ChatMessage> messages)
//    {
//        var system = string.Join("\n", messages
//            .Where(m => m.Role == ChatRole.System).Select(m => m.Text ?? ""));
//        var converted = messages
//            .Where(m => m.Role != ChatRole.System)
//            .Select(m => new
//            {
//                role = m.Role == ChatRole.Assistant ? "assistant" : "user",
//                content = m.Text ?? ""
//            }).ToArray<object>();
//        return (system, converted);
//    }

//    // CONFIRMED correct signature: object? GetService(Type serviceType, object? serviceKey)
//    // Source: dotnet/dotnet-api-docs IChatClient.xml + NuGet Gallery sample code
//    public object? GetService(Type serviceType, object? serviceKey = null)
//    {
//        if (serviceKey is null && serviceType == typeof(ChatClientMetadata)) return Metadata;
//        if (serviceKey is null && serviceType.IsInstanceOfType(this)) return this;
//        return null;
//    }

//    public void Dispose() => _http.Dispose();
//}

///// <summary>
///// Google Gemini adapter for IChatClient.
///// Same confirmed signatures applied.
///// </summary>
//public class GeminiChatClient : IChatClient
//{
//    private readonly HttpClient _http;
//    private readonly string _model;
//    private readonly string _apiKey;
//    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models";

//    public ChatClientMetadata Metadata =>
//        new("Google", new Uri("https://generativelanguage.googleapis.com"), _model);

//    public GeminiChatClient(string apiKey, string model = "gemini-2.0-flash")
//    {
//        _apiKey = apiKey; _model = model;
//        _http = new HttpClient();
//    }

//    public async Task<ChatResponse> GetResponseAsync(
//        IEnumerable<ChatMessage> messages,
//        ChatOptions? options = null,
//        CancellationToken cancellationToken = default)
//    {
//        var (sys, contents) = ConvertMessages(messages.ToList());
//        var body = JsonSerializer.Serialize(new
//        {
//            system_instruction = sys,
//            contents,
//            generationConfig = new { maxOutputTokens = options?.MaxOutputTokens ?? 8096 }
//        });

//        var url = $"{BaseUrl}/{_model}:generateContent?key={_apiKey}";
//        var resp = await _http.PostAsync(url,
//            new StringContent(body, Encoding.UTF8, "application/json"), cancellationToken);
//        var json = await resp.Content.ReadAsStringAsync(cancellationToken);
//        if (!resp.IsSuccessStatusCode)
//            throw new Exception($"Gemini error {resp.StatusCode}: {json}");

//        using var doc = JsonDocument.Parse(json);
//        var text = doc.RootElement
//            .GetProperty("candidates")[0].GetProperty("content")
//            .GetProperty("parts")[0].GetProperty("text").GetString() ?? "";

//        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text));
//    }

//    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
//        IEnumerable<ChatMessage> messages,
//        ChatOptions? options = null,
//        [EnumeratorCancellation] CancellationToken cancellationToken = default)
//    {
//        var tokens = await CollectTokensAsync(messages, options, cancellationToken);
//        foreach (var token in tokens)
//            yield return new ChatResponseUpdate(ChatRole.Assistant, token);
//    }

//    private async Task<List<string>> CollectTokensAsync(
//        IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken ct)
//    {
//        var (sys, contents) = ConvertMessages(messages.ToList());
//        var body = JsonSerializer.Serialize(new { system_instruction = sys, contents });
//        var url = $"{BaseUrl}/{_model}:streamGenerateContent?alt=sse&key={_apiKey}";
//        var req = new HttpRequestMessage(HttpMethod.Post, url)
//        {
//            Content = new StringContent(body, Encoding.UTF8, "application/json")
//        };

//        var tokens = new List<string>();
//        try
//        {
//            using var resp = await _http.SendAsync(req,
//                HttpCompletionOption.ResponseHeadersRead, ct);
//            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
//            using var reader = new StreamReader(stream);

//            while (!ct.IsCancellationRequested)
//            {
//                var line = await reader.ReadLineAsync(ct);
//                if (line == null) break;
//                if (!line.StartsWith("data: ")) continue;
//                try
//                {
//                    using var doc = JsonDocument.Parse(line["data: ".Length..]);
//                    var text = doc.RootElement
//                        .GetProperty("candidates")[0].GetProperty("content")
//                        .GetProperty("parts")[0].GetProperty("text").GetString() ?? "";
//                    if (!string.IsNullOrEmpty(text)) tokens.Add(text);
//                }
//                catch { /* skip malformed chunk */ }
//            }
//        }
//        catch (Exception ex) { tokens.Add($"\n[Gemini error: {ex.Message}]"); }

//        return tokens;
//    }

//    private static (object sys, object[] contents) ConvertMessages(List<ChatMessage> messages)
//    {
//        var sysText = string.Join("\n", messages
//            .Where(m => m.Role == ChatRole.System).Select(m => m.Text ?? ""));
//        var sys = new { parts = new[] { new { text = sysText } } };
//        var contents = messages
//            .Where(m => m.Role != ChatRole.System)
//            .Select(m => new
//            {
//                role = m.Role == ChatRole.Assistant ? "model" : "user",
//                parts = new[] { new { text = m.Text ?? "" } }
//            }).ToArray<object>();
//        return (sys, contents);
//    }

//    public object? GetService(Type serviceType, object? serviceKey = null)
//    {
//        if (serviceKey is null && serviceType == typeof(ChatClientMetadata)) return Metadata;
//        if (serviceKey is null && serviceType.IsInstanceOfType(this)) return this;
//        return null;
//    }

//    public void Dispose() => _http.Dispose();
//}
//end of claude and openAI usage code above.


using Microsoft.Extensions.AI;
using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;

namespace DevPipeline.Agents;

/// <summary>
/// Claude (Anthropic) adapter for IChatClient (Microsoft.Extensions.AI).
///
/// CONFIRMED IChatClient interface signature (from dotnet/dotnet-api-docs):
///   Task&lt;ChatResponse&gt; GetResponseAsync(IEnumerable&lt;ChatMessage&gt;, ChatOptions?, CancellationToken)
///   IAsyncEnumerable&lt;ChatResponseUpdate&gt; GetStreamingResponseAsync(IEnumerable&lt;ChatMessage&gt;, ChatOptions?, CancellationToken)
///   object? GetService(Type serviceType, object? serviceKey = null)   ← NOT generic
///   ChatClientMetadata Metadata { get; }
/// </summary>
public class AnthropicChatClient : IChatClient
{
    private readonly HttpClient _http;
    private readonly string _model;
    private const string BaseUrl = "https://api.anthropic.com/v1/messages";

    public ChatClientMetadata Metadata =>
        new("Anthropic", new Uri("https://api.anthropic.com"), _model);

    public AnthropicChatClient(string apiKey, string model = "claude-sonnet-4-20250514")
    {
        _model = model;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var (system, msgs) = ConvertMessages(messages.ToList());
        var body = JsonSerializer.Serialize(new
        {
            model = _model,
            max_tokens = options?.MaxOutputTokens ?? 8096,
            system,
            messages = msgs
        });

        var resp = await _http.PostAsync(BaseUrl,
            new StringContent(body, Encoding.UTF8, "application/json"), cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Claude error {resp.StatusCode}: {json}");

        using var doc = JsonDocument.Parse(json);
        var text = doc.RootElement
            .GetProperty("content")[0]
            .GetProperty("text").GetString() ?? "";

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text));
    }

    // CS1626 fix: yield cannot be inside try/catch.
    // Collect all tokens in a regular Task first, then yield them outside try/catch.
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var tokens = await CollectTokensAsync(messages, options, cancellationToken);
        foreach (var token in tokens)
            yield return new ChatResponseUpdate(ChatRole.Assistant, token);
    }

    private async Task<List<string>> CollectTokensAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken ct)
    {
        var (system, msgs) = ConvertMessages(messages.ToList());
        var body = JsonSerializer.Serialize(new
        {
            model = _model,
            max_tokens = options?.MaxOutputTokens ?? 8096,
            system,
            messages = msgs,
            stream = true
        });

        var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        var tokens = new List<string>();
        try
        {
            using var resp = await _http.SendAsync(req,
                HttpCompletionOption.ResponseHeadersRead, ct);
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null) break;
                if (!line.StartsWith("data: ")) continue;
                var data = line["data: ".Length..];
                if (data == "[DONE]") break;
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("type", out var t)
                        && t.GetString() == "content_block_delta"
                        && root.TryGetProperty("delta", out var d)
                        && d.TryGetProperty("text", out var txt))
                    {
                        var token = txt.GetString() ?? "";
                        if (!string.IsNullOrEmpty(token)) tokens.Add(token);
                    }
                }
                catch { /* skip malformed SSE line */ }
            }
        }
        catch (Exception ex) { tokens.Add($"\n[Claude error: {ex.Message}]"); }

        return tokens;
    }

    private static (string system, object[] messages) ConvertMessages(List<ChatMessage> messages)
    {
        var system = string.Join("\n", messages
            .Where(m => m.Role == ChatRole.System).Select(m => m.Text ?? ""));
        var converted = messages
            .Where(m => m.Role != ChatRole.System)
            .Select(m => new
            {
                role = m.Role == ChatRole.Assistant ? "assistant" : "user",
                content = m.Text ?? ""
            }).ToArray<object>();
        return (system, converted);
    }

    // CONFIRMED correct signature: object? GetService(Type serviceType, object? serviceKey)
    // Source: dotnet/dotnet-api-docs IChatClient.xml + NuGet Gallery sample code
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceKey is null && serviceType == typeof(ChatClientMetadata)) return Metadata;
        if (serviceKey is null && serviceType.IsInstanceOfType(this)) return this;
        return null;
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// Google Gemini adapter for IChatClient.
/// Same confirmed signatures applied.
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
        _apiKey = apiKey; _model = model;
        _http = new HttpClient();
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var (sys, contents) = ConvertMessages(messages.ToList());
        var body = JsonSerializer.Serialize(new
        {
            system_instruction = sys,
            contents,
            generationConfig = new { maxOutputTokens = options?.MaxOutputTokens ?? 8096 }
        });

        var url = $"{BaseUrl}/{_model}:generateContent?key={_apiKey}";
        var resp = await _http.PostAsync(url,
            new StringContent(body, Encoding.UTF8, "application/json"), cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Gemini error {resp.StatusCode}: {json}");

        using var doc = JsonDocument.Parse(json);
        var text = doc.RootElement
            .GetProperty("candidates")[0].GetProperty("content")
            .GetProperty("parts")[0].GetProperty("text").GetString() ?? "";

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var tokens = await CollectTokensAsync(messages, options, cancellationToken);
        foreach (var token in tokens)
            yield return new ChatResponseUpdate(ChatRole.Assistant, token);
    }

    private async Task<List<string>> CollectTokensAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken ct)
    {
        var (sys, contents) = ConvertMessages(messages.ToList());
        var body = JsonSerializer.Serialize(new { system_instruction = sys, contents });
        var url = $"{BaseUrl}/{_model}:streamGenerateContent?alt=sse&key={_apiKey}";
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        var tokens = new List<string>();
        try
        {
            using var resp = await _http.SendAsync(req,
                HttpCompletionOption.ResponseHeadersRead, ct);
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null) break;
                if (!line.StartsWith("data: ")) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line["data: ".Length..]);
                    var text = doc.RootElement
                        .GetProperty("candidates")[0].GetProperty("content")
                        .GetProperty("parts")[0].GetProperty("text").GetString() ?? "";
                    if (!string.IsNullOrEmpty(text)) tokens.Add(text);
                }
                catch { /* skip malformed chunk */ }
            }
        }
        catch (Exception ex) { tokens.Add($"\n[Gemini error: {ex.Message}]"); }

        return tokens;
    }

    private static (object sys, object[] contents) ConvertMessages(List<ChatMessage> messages)
    {
        var sysText = string.Join("\n", messages
            .Where(m => m.Role == ChatRole.System).Select(m => m.Text ?? ""));
        var sys = new { parts = new[] { new { text = sysText } } };
        var contents = messages
            .Where(m => m.Role != ChatRole.System)
            .Select(m => new
            {
                role = m.Role == ChatRole.Assistant ? "model" : "user",
                parts = new[] { new { text = m.Text ?? "" } }
            }).ToArray<object>();
        return (sys, contents);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceKey is null && serviceType == typeof(ChatClientMetadata)) return Metadata;
        if (serviceKey is null && serviceType.IsInstanceOfType(this)) return this;
        return null;
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// Groq adapter for IChatClient — 100% FREE, no credit card required.
/// Uses OpenAI-compatible REST API at api.groq.com/openai/v1
/// Model: llama-3.3-70b-versatile
/// Free limits: 500,000 tokens/day · 6,000 tokens/minute
/// Sign up at: https://console.groq.com
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
        CancellationToken cancellationToken = default)
    {
        var body = JsonSerializer.Serialize(new
        {
            model = _model,
            max_tokens = options?.MaxOutputTokens ?? 8096,
            messages = ConvertMessages(messages)
        });

        var resp = await _http.PostAsync(BaseUrl,
            new StringContent(body, Encoding.UTF8, "application/json"), cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Groq error {resp.StatusCode}: {json}");

        using var doc = JsonDocument.Parse(json);
        var text = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content").GetString() ?? "";

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var tokens = await CollectTokensAsync(messages, options, cancellationToken);
        foreach (var token in tokens)
            yield return new ChatResponseUpdate(ChatRole.Assistant, token);
    }

    private async Task<List<string>> CollectTokensAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            model = _model,
            max_tokens = options?.MaxOutputTokens ?? 8096,
            messages = ConvertMessages(messages),
            stream = true
        });

        var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        var tokens = new List<string>();
        try
        {
            using var resp = await _http.SendAsync(req,
                HttpCompletionOption.ResponseHeadersRead, ct);
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null) break;
                if (!line.StartsWith("data: ")) continue;
                var data = line["data: ".Length..];
                if (data == "[DONE]") break;
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    var delta = doc.RootElement
                        .GetProperty("choices")[0]
                        .GetProperty("delta");
                    if (delta.TryGetProperty("content", out var c))
                    {
                        var token = c.GetString() ?? "";
                        if (!string.IsNullOrEmpty(token)) tokens.Add(token);
                    }
                }
                catch { /* skip malformed SSE line */ }
            }
        }
        catch (Exception ex) { tokens.Add($"\n[Groq error: {ex.Message}]"); }

        return tokens;
    }

    private static object[] ConvertMessages(IEnumerable<ChatMessage> messages) =>
        messages.Select(m => new
        {
            role = m.Role == ChatRole.System ? "system"
                    : m.Role == ChatRole.Assistant ? "assistant"
                    : "user",
            content = m.Text ?? ""
        }).ToArray<object>();

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceKey is null && serviceType == typeof(ChatClientMetadata)) return Metadata;
        if (serviceKey is null && serviceType.IsInstanceOfType(this)) return this;
        return null;
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// GitHub Models adapter for IChatClient — 100% FREE, no credit card needed.
/// Uses your EXISTING GitHub PAT (same token used for repo/PR operations).
/// Endpoint: https://models.github.ai/inference  (OpenAI-compatible format)
///
/// Available free models (prototype tier):
///   openai/gpt-4o            — GPT-4o (150 req/day)
///   openai/gpt-4o-mini       — GPT-4o Mini (faster, 150 req/day)
///   meta/llama-3.3-70b-instruct — Llama 3.3 70B (150 req/day)
///   mistral-ai/mistral-large  — Mistral Large (150 req/day)
///
/// Get token: github.com/settings/tokens → Generate new token (classic)
///   → check "models:read" scope (or no scope needed with beta option)
/// Same PAT you already have in GitHub:Token works here!
/// </summary>
public class GitHubModelsChatClient : IChatClient
{
    private readonly HttpClient _http;
    private readonly string _model;
    private const string BaseUrl = "https://models.github.ai/inference/chat/completions";

    public ChatClientMetadata Metadata =>
        new("GitHubModels", new Uri("https://models.github.ai"), _model);

    public GitHubModelsChatClient(string githubPat, string model = "openai/gpt-4o-mini")
    {
        _model = model;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {githubPat}");
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var body = JsonSerializer.Serialize(new
        {
            model = _model,
            max_tokens = options?.MaxOutputTokens ?? 4096,
            messages = ConvertMessages(messages)
        });

        var resp = await _http.PostAsync(BaseUrl,
            new StringContent(body, Encoding.UTF8, "application/json"), cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"GitHub Models error {resp.StatusCode}: {json}");

        using var doc = JsonDocument.Parse(json);
        var text = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content").GetString() ?? "";

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var tokens = await CollectTokensAsync(messages, options, cancellationToken);
        foreach (var token in tokens)
            yield return new ChatResponseUpdate(ChatRole.Assistant, token);
    }

    private async Task<List<string>> CollectTokensAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            model = _model,
            max_tokens = options?.MaxOutputTokens ?? 4096,
            messages = ConvertMessages(messages),
            stream = true
        });

        var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        var tokens = new List<string>();
        try
        {
            using var resp = await _http.SendAsync(req,
                HttpCompletionOption.ResponseHeadersRead, ct);
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null) break;
                if (!line.StartsWith("data: ")) continue;
                var data = line["data: ".Length..];
                if (data == "[DONE]") break;
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    var delta = doc.RootElement
                        .GetProperty("choices")[0]
                        .GetProperty("delta");
                    if (delta.TryGetProperty("content", out var c))
                    {
                        var token = c.GetString() ?? "";
                        if (!string.IsNullOrEmpty(token)) tokens.Add(token);
                    }
                }
                catch { /* skip malformed SSE line */ }
            }
        }
        catch (Exception ex) { tokens.Add($"\n[GitHub Models error: {ex.Message}]"); }

        return tokens;
    }

    private static object[] ConvertMessages(IEnumerable<ChatMessage> messages) =>
        messages.Select(m => new
        {
            role = m.Role == ChatRole.System ? "system"
                    : m.Role == ChatRole.Assistant ? "assistant"
                    : "user",
            content = m.Text ?? ""
        }).ToArray<object>();

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceKey is null && serviceType == typeof(ChatClientMetadata)) return Metadata;
        if (serviceKey is null && serviceType.IsInstanceOfType(this)) return this;
        return null;
    }

    public void Dispose() => _http.Dispose();
}