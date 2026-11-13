using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace HUMofA.Agent;

/// <summary>
/// Master system prompt equipping LLMs with Hermes Agent / HUMofA local computer autonomy and tool identity.
/// </summary>
public static class HermesPrompts
{
    public const string DefaultSystemPrompt = """
You are HUMofA (Humanize of AI), a powerful autonomous AI agent platform combining Hermes Agent reasoning, Claude Code CLI interaction, and desktop computer-use automation.
You run directly on the user's local Windows PC.

Environment & Capabilities:
- Platform: Windows
- Terminal & Execution: You have full access to execute terminal commands (PowerShell/cmd), build projects, run tests, and manage processes.
- File System: You can read, create, edit, patch, and manage workspace files with diff reviews.
- Computer Use & Desktop Automation: You have access to mouse navigation, clicking, keyboard typing, and desktop inspection.
- Autonomous Sub-Agents: You can spawn, coordinate, and delegate to specialized sub-agents.
- Skills & Memory: You have long-term persistent memory and extensible skills.

Guidelines:
- When asked who you are or what you can do on this machine, proudly identify yourself as HUMofA (Humanize of AI) / Hermes.
- Clearly explain your local computer, terminal, file, and development capabilities.
- Never claim to be a text-only chatbot without computer or terminal access; you are equipped with real tools and agentic autonomy on this system.
- Always respond in the same language as the user (e.g. Turkish if the user speaks Turkish).
- Briefly state what step you are performing before invoking a tool so the user can follow your thought and action flow.

Action & Tool Execution:
Whenever the user asks you to perform tasks on this computer (such as checking installed programs like ffmpeg or git, inspecting or writing files, building projects, running tests, or managing processes), DO NOT write hypothetical prose or pretend outputs. Instead, execute the real action using these exact XML tags:

1. Execute Terminal Command (PowerShell / cmd / CLI):
<bash>
command here
</bash>

2. Read File:
<read_file path="relative/or/full/path" />

3. Create or Overwrite File:
<write_file path="relative/or/full/path">
file content here
</write_file>

4. Spawn an Autonomous Sub-Agent:
<subagent role="RoleName" name="AgentName">
Task instructions for the subagent
</subagent>

5. Ask User for Help, Clarification, or Manual Action (Human-in-the-Loop):
<ask_user reason="Neden kullanıcıya ihtiyaç duyulduğu" options="Seçenek 1|Seçenek 2">
Kullanıcıdan yapmasını istediğiniz işlem veya cevaplamasını istediğiniz net soru
</ask_user>

When to Use <ask_user>:
- If you cannot resolve an issue on your own, if a command or operation fails repeatedly, or if you hit a blocker (missing credentials, login/2FA/CAPTCHA required, permission denied, ambiguous file/target, or a physical/manual action the user must perform), NEVER guess, fabricate outputs, or silently give up!
- Immediately emit <ask_user reason="...">...</ask_user> explaining what happened and what you need the user to do or decide. HUMofA will pause, prompt the user interactively, and return their answer in a <tool_result> block so you can continue.
""";
}

/// <summary>
/// Live HTTP client for Google Gemini REST API (v1beta/models/{model}:generateContent).
/// </summary>
public sealed class GeminiLiveClient : IModelProviderClient
{
    private readonly string _apiKey;
    private readonly HttpClient _httpClient;
    private readonly string _baseEndpoint;

    public string ProviderId => "gemini";

    public GeminiLiveClient(string apiKey, HttpClient? httpClient = null, string? baseEndpoint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        _apiKey = apiKey.Trim();
        _httpClient = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _baseEndpoint = string.IsNullOrWhiteSpace(baseEndpoint)
            ? "https://generativelanguage.googleapis.com/v1beta/models"
            : baseEndpoint.TrimEnd('/');
    }

    public Task<string> GenerateAsync(string prompt, string model, CancellationToken ct = default)
    {
        return GenerateChatAsync([new LlmMessage("user", prompt)], model, ct);
    }

    public async Task<string> GenerateChatAsync(IReadOnlyList<LlmMessage> messages, string model, CancellationToken ct = default)
    {
        var modelName = string.IsNullOrWhiteSpace(model) ? "gemini-2.5-flash" : model.Trim();
        if (modelName.StartsWith("gemini:", StringComparison.OrdinalIgnoreCase))
            modelName = modelName["gemini:".Length..];
        if (modelName.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
            modelName = modelName["models/".Length..];

        var url = $"{_baseEndpoint}/{modelName}:generateContent?key={Uri.EscapeDataString(_apiKey)}";
        var systemMsg = messages.FirstOrDefault(m => m.Role.Equals("system", StringComparison.OrdinalIgnoreCase))?.Content
            ?? HermesPrompts.DefaultSystemPrompt;

        var contents = new List<object>();
        foreach (var m in messages.Where(m => !m.Role.Equals("system", StringComparison.OrdinalIgnoreCase)))
        {
            var role = m.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? "model" : "user";
            contents.Add(new
            {
                role = role,
                parts = new[] { new { text = m.Content } }
            });
        }
        if (contents.Count == 0)
        {
            contents.Add(new { role = "user", parts = new[] { new { text = "Hello" } } });
        }

        var payload = new
        {
            system_instruction = new
            {
                parts = new[] { new { text = systemMsg } }
            },
            contents = contents.ToArray()
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };

        using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Gemini API returned {(int)resp.StatusCode}: {body}");
        }

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("candidates", out var candidates) &&
            candidates.ValueKind == JsonValueKind.Array &&
            candidates.GetArrayLength() > 0)
        {
            var first = candidates[0];
            if (first.TryGetProperty("content", out var content) &&
                content.TryGetProperty("parts", out var parts) &&
                parts.ValueKind == JsonValueKind.Array &&
                parts.GetArrayLength() > 0 &&
                parts[0].TryGetProperty("text", out var textNode))
            {
                return textNode.GetString() ?? string.Empty;
            }
        }

        throw new InvalidOperationException("Gemini API response did not contain candidate text.");
    }
}

/// <summary>
/// Live HTTP client for OpenAI-compatible Chat Completions APIs (SSB Evren, OpenAI, OpenRouter, Nous Portal, vLLM, LiteLLM, or custom endpoints).
/// </summary>
public sealed class OpenAiCompatibleLiveClient : IStreamingModelProviderClient
{
    private readonly string _providerId;
    private readonly string _apiKey;
    private readonly string _endpointUrl;
    private readonly string _defaultModel;
    private readonly string? _customAuthHeader;
    private readonly string? _systemPrompt;
    private readonly HttpClient _httpClient;

    public string ProviderId => _providerId;
    public string EndpointUrl => _endpointUrl;

    public OpenAiCompatibleLiveClient(
        string providerId,
        string apiKey,
        string endpointUrl,
        HttpClient? httpClient = null)
        : this(providerId, endpointUrl, apiKey, "gpt-4o", httpClient, null, false, null)
    {
    }

    public OpenAiCompatibleLiveClient(
        string providerId,
        string endpointUrl,
        string apiKey,
        string defaultModel,
        HttpClient? httpClient = null,
        string? customAuthHeader = null,
        bool ignoreSslErrors = false,
        string? systemPrompt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointUrl);

        _providerId = providerId.Trim().ToLowerInvariant();
        _apiKey = apiKey?.Trim() ?? string.Empty;
        _endpointUrl = NormalizeChatCompletionsEndpoint(endpointUrl);
        _defaultModel = string.IsNullOrWhiteSpace(defaultModel) ? "gpt-4o" : defaultModel.Trim();
        _customAuthHeader = string.IsNullOrWhiteSpace(customAuthHeader) ? null : customAuthHeader.Trim();
        _systemPrompt = string.IsNullOrWhiteSpace(systemPrompt) ? null : systemPrompt.Trim();
        _httpClient = httpClient ?? CreateHttpClient(ignoreSslErrors, Timeout.InfiniteTimeSpan);
    }

    public static string NormalizeChatCompletionsEndpoint(string rawEndpoint)
    {
        if (string.IsNullOrWhiteSpace(rawEndpoint))
            return "https://api.openai.com/v1/chat/completions";

        var trimmed = rawEndpoint.Trim().TrimEnd('/');
        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith("/completions", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith("/messages", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith("/api/chat", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith("/api/generate", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        if (trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            return trimmed + "/chat/completions";

        return trimmed + "/v1/chat/completions";
    }

    public static string NormalizeModelsEndpoint(string rawEndpoint)
    {
        if (string.IsNullOrWhiteSpace(rawEndpoint))
            return "https://api.openai.com/v1/models";

        var trimmed = rawEndpoint.Trim().TrimEnd('/');
        if (trimmed.EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
            return trimmed[..^"/chat/completions".Length] + "/models";
        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return trimmed[..^"/chat/completions".Length] + "/models";
        if (trimmed.EndsWith("/v1/models", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
            return trimmed;
        if (trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            return trimmed + "/models";

        return trimmed + "/v1/models";
    }

    public static HttpClient CreateHttpClient(bool ignoreSslErrors, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? Timeout.InfiniteTimeSpan;
        if (!ignoreSslErrors)
            return new HttpClient { Timeout = effectiveTimeout };

        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        return new HttpClient(handler) { Timeout = effectiveTimeout };
    }

    public static async Task<IReadOnlyList<string>> FetchAvailableModelsAsync(
        string endpointUrl,
        string? apiKey = null,
        string? customAuthHeader = null,
        bool ignoreSslErrors = false,
        HttpClient? httpClient = null,
        CancellationToken ct = default)
    {
        var modelsUrl = NormalizeModelsEndpoint(endpointUrl);
        var client = httpClient ?? CreateHttpClient(ignoreSslErrors, TimeSpan.FromSeconds(15));

        using var req = new HttpRequestMessage(HttpMethod.Get, modelsUrl);
        ApplyAuthHeaders(req, apiKey, customAuthHeader);

        using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Models endpoint returned {(int)resp.StatusCode}: {body}");

        var models = new List<string>();
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("data", out var dataArr) && dataArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in dataArr.EnumerateArray())
            {
                if (item.TryGetProperty("id", out var idProp) && idProp.GetString() is { Length: > 0 } id)
                    models.Add(id);
            }
        }
        else if (doc.RootElement.TryGetProperty("models", out var modelsArr) && modelsArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in modelsArr.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } str)
                    models.Add(str);
                else if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("name", out var nameProp) && nameProp.GetString() is { Length: > 0 } name)
                    models.Add(name);
            }
        }

        return models;
    }

    private static void ApplyAuthHeaders(HttpRequestMessage req, string? apiKey, string? customAuthHeader)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return;

        if (!string.IsNullOrWhiteSpace(customAuthHeader) &&
            !customAuthHeader.Equals("Authorization", StringComparison.OrdinalIgnoreCase) &&
            !customAuthHeader.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
        {
            req.Headers.TryAddWithoutValidation(customAuthHeader.Trim(), apiKey.Trim());
        }
        else
        {
            var token = apiKey.Trim();
            if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                token = token["Bearer ".Length..].Trim();
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    public Task<string> GenerateAsync(string prompt, string model, CancellationToken ct = default)
    {
        return GenerateChatAsync([new LlmMessage("user", prompt)], model, ct);
    }

    public async Task<string> GenerateChatAsync(IReadOnlyList<LlmMessage> messages, string model, CancellationToken ct = default)
    {
        var cleanModel = string.IsNullOrWhiteSpace(model) ? _defaultModel : model.Trim();
        if (cleanModel.StartsWith($"{_providerId}:", StringComparison.OrdinalIgnoreCase))
            cleanModel = cleanModel[(_providerId.Length + 1)..];
        if (cleanModel.StartsWith($"{_providerId}/", StringComparison.OrdinalIgnoreCase) &&
            (_providerId == "ssb-evren" || _providerId == "evren" || _providerId == "custom"))
        {
            cleanModel = cleanModel[(_providerId.Length + 1)..];
        }

        var payloadMessages = new List<object>();
        if (!messages.Any(m => m.Role.Equals("system", StringComparison.OrdinalIgnoreCase)))
        {
            payloadMessages.Add(new { role = "system", content = _systemPrompt ?? HermesPrompts.DefaultSystemPrompt });
        }

        foreach (var msg in messages)
        {
            payloadMessages.Add(new { role = msg.Role.ToLowerInvariant(), content = msg.Content });
        }

        var payload = new
        {
            model = cleanModel,
            messages = payloadMessages.ToArray()
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, _endpointUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        ApplyAuthHeaders(req, _apiKey, _customAuthHeader);
        if (_providerId.Equals("openrouter", StringComparison.OrdinalIgnoreCase))
        {
            req.Headers.TryAddWithoutValidation("HTTP-Referer", "https://humofa.local");
            req.Headers.TryAddWithoutValidation("X-Title", "HUMofA Autonomous Assistant");
        }

        using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        // If SSB Evren returns 503/429 (e.g. model_capacity_exhausted on glm-5.3), automatically retry with 'deepseek-v4.1-flash' or 'gemma-4-31b'
        // CRITICAL: Preserves the entire multi-turn conversation history, memories, and system instructions!
        if (!resp.IsSuccessStatusCode &&
            ((int)resp.StatusCode == 503 || (int)resp.StatusCode == 429) &&
            (_providerId.Equals("ssb-evren", StringComparison.OrdinalIgnoreCase) || _providerId.Equals("evren", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var fallbackEvrenModel in new[] { "deepseek-v4.1-flash", "gemma-4-31b", "mimo-v2.6-pro", "qwen3-vl-30b" })
            {
                if (cleanModel.Equals(fallbackEvrenModel, StringComparison.OrdinalIgnoreCase))
                    continue;

                var retryPayload = new
                {
                    model = fallbackEvrenModel,
                    messages = payloadMessages.ToArray()
                };
                using var retryReq = new HttpRequestMessage(HttpMethod.Post, _endpointUrl)
                {
                    Content = new StringContent(JsonSerializer.Serialize(retryPayload), Encoding.UTF8, "application/json")
                };
                ApplyAuthHeaders(retryReq, _apiKey, _customAuthHeader);
                using var retryResp = await _httpClient.SendAsync(retryReq, ct).ConfigureAwait(false);
                if (retryResp.IsSuccessStatusCode)
                {
                    var retryBody = await retryResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    var parsedRetry = ExtractContentFromJson(retryBody);
                    if (!string.IsNullOrWhiteSpace(parsedRetry))
                    {
                        return $"*(Not: `{cleanModel}` modeli şu anda SSB Evren sunucusunda yoğun/dolu olduğu için (`503 model_capacity_exhausted`) otomatik olarak `{fallbackEvrenModel}` modeliyle yanıtlandı)*\n\n{parsedRetry}";
                    }
                }
            }
        }

        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{_providerId.ToUpperInvariant()} API returned {(int)resp.StatusCode}: {body}");
        }

        var extracted = ExtractContentFromJson(body);
        if (extracted != null)
            return extracted;

        throw new InvalidOperationException($"{_providerId.ToUpperInvariant()} API response did not contain message content.");
    }

    public Task<string> GenerateStreamAsync(
        string prompt,
        string model,
        Action<string>? onReasoningChunk = null,
        Action<string>? onContentChunk = null,
        CancellationToken ct = default)
    {
        return GenerateChatStreamAsync([new LlmMessage("user", prompt)], model, onReasoningChunk, onContentChunk, ct);
    }

    public async Task<string> GenerateChatStreamAsync(
        IReadOnlyList<LlmMessage> messages,
        string model,
        Action<string>? onReasoningChunk = null,
        Action<string>? onContentChunk = null,
        CancellationToken ct = default)
    {
        var cleanModel = string.IsNullOrWhiteSpace(model) ? _defaultModel : model.Trim();
        if (cleanModel.StartsWith($"{_providerId}:", StringComparison.OrdinalIgnoreCase))
            cleanModel = cleanModel[(_providerId.Length + 1)..];
        if (cleanModel.StartsWith($"{_providerId}/", StringComparison.OrdinalIgnoreCase) &&
            (_providerId == "ssb-evren" || _providerId == "evren" || _providerId == "custom"))
        {
            cleanModel = cleanModel[(_providerId.Length + 1)..];
        }

        var payloadMessages = new List<object>();
        if (!messages.Any(m => m.Role.Equals("system", StringComparison.OrdinalIgnoreCase)))
        {
            payloadMessages.Add(new { role = "system", content = _systemPrompt ?? HermesPrompts.DefaultSystemPrompt });
        }

        foreach (var msg in messages)
        {
            payloadMessages.Add(new { role = msg.Role.ToLowerInvariant(), content = msg.Content });
        }

        var (primaryResult, statusCode) = await TryStreamModelAsync(
            cleanModel,
            payloadMessages,
            prefixNotice: null,
            onReasoningChunk,
            onContentChunk,
            ct).ConfigureAwait(false);

        if (primaryResult != null)
            return primaryResult;

        // If SSB Evren returns 503/429 (e.g. model_capacity_exhausted on glm-5.3), automatically stream from fallback models
        if ((statusCode == 503 || statusCode == 429) &&
            (_providerId.Equals("ssb-evren", StringComparison.OrdinalIgnoreCase) || _providerId.Equals("evren", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var fallbackEvrenModel in new[] { "deepseek-v4.1-flash", "gemma-4-31b", "mimo-v2.6-pro", "qwen3-vl-30b" })
            {
                if (cleanModel.Equals(fallbackEvrenModel, StringComparison.OrdinalIgnoreCase))
                    continue;

                var notice = $"*(Not: `{cleanModel}` modeli şu anda SSB Evren sunucusunda yoğun/dolu olduğu için (`{statusCode} model_capacity_exhausted`) otomatik olarak `{fallbackEvrenModel}` modeliyle yanıtlandı)*\n\n";
                var (fallbackResult, _) = await TryStreamModelAsync(
                    fallbackEvrenModel,
                    payloadMessages,
                    prefixNotice: notice,
                    onReasoningChunk,
                    onContentChunk,
                    ct).ConfigureAwait(false);

                if (fallbackResult != null)
                    return fallbackResult;
            }
        }

        var fallbackSync = await GenerateChatAsync(messages, model, ct).ConfigureAwait(false);
        await EmitContentSmoothlyAsync(fallbackSync, onContentChunk, ct).ConfigureAwait(false);
        return fallbackSync;
    }

    private async Task<(string? Result, int StatusCode)> TryStreamModelAsync(
        string targetModel,
        List<object> payloadMessages,
        string? prefixNotice,
        Action<string>? onReasoningChunk,
        Action<string>? onContentChunk,
        CancellationToken ct)
    {
        var payload = new
        {
            model = targetModel,
            messages = payloadMessages.ToArray(),
            stream = true
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, _endpointUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        ApplyAuthHeaders(req, _apiKey, _customAuthHeader);
        if (_providerId.Equals("openrouter", StringComparison.OrdinalIgnoreCase))
        {
            req.Headers.TryAddWithoutValidation("HTTP-Referer", "https://humofa.local");
            req.Headers.TryAddWithoutValidation("X-Title", "HUMofA Autonomous Assistant");
        }

        HttpResponseMessage resp;
        try
        {
            resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch
        {
            return (null, 0);
        }

        using (resp)
        {
            int statusCode = (int)resp.StatusCode;
            if (!resp.IsSuccessStatusCode)
            {
                return (null, statusCode);
            }

            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var contentBuilder = new StringBuilder();
            var reasoningBuilder = new StringBuilder();
            var rawNonSseBuilder = new StringBuilder();
            bool noticeEmitted = false;
            bool inThinkTag = false;

            void EnsureNoticeEmitted()
            {
                if (!noticeEmitted && !string.IsNullOrEmpty(prefixNotice))
                {
                    noticeEmitted = true;
                    contentBuilder.Append(prefixNotice);
                    onContentChunk?.Invoke(prefixNotice);
                }
            }

            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } rawLine)
            {
                if (string.IsNullOrWhiteSpace(rawLine)) continue;

                var line = rawLine.Trim();
                if (line.StartsWith("data: "))
                    line = line["data: ".Length..].Trim();
                else if (line.StartsWith("data:"))
                    line = line["data:".Length..].Trim();
                else
                {
                    rawNonSseBuilder.AppendLine(rawLine);
                    continue;
                }

                if (line.Equals("[DONE]", StringComparison.OrdinalIgnoreCase))
                    break;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                        choices.ValueKind == JsonValueKind.Array &&
                        choices.GetArrayLength() > 0)
                    {
                        var first = choices[0];
                        if (first.TryGetProperty("delta", out var delta))
                        {
                            string? rChunk = null;
                            if (delta.TryGetProperty("reasoning", out var r1) && r1.ValueKind == JsonValueKind.String)
                                rChunk = r1.GetString();
                            else if (delta.TryGetProperty("reasoning_content", out var r2) && r2.ValueKind == JsonValueKind.String)
                                rChunk = r2.GetString();
                            else if (delta.TryGetProperty("thinking", out var r3) && r3.ValueKind == JsonValueKind.String)
                                rChunk = r3.GetString();

                            if (!string.IsNullOrEmpty(rChunk))
                            {
                                reasoningBuilder.Append(rChunk);
                                onReasoningChunk?.Invoke(rChunk);
                            }

                            if (delta.TryGetProperty("content", out var cNode) && cNode.ValueKind == JsonValueKind.String)
                            {
                                var cChunk = cNode.GetString();
                                if (!string.IsNullOrEmpty(cChunk))
                                {
                                    // Handle inline <think>...</think> blocks emitted inside delta.content
                                    if (cChunk.Contains("<think>", StringComparison.OrdinalIgnoreCase))
                                    {
                                        inThinkTag = true;
                                        cChunk = cChunk.Replace("<think>", string.Empty, StringComparison.OrdinalIgnoreCase);
                                    }

                                    if (inThinkTag)
                                    {
                                        var endIdx = cChunk.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
                                        if (endIdx >= 0)
                                        {
                                            var thoughtPart = cChunk[..endIdx];
                                            if (!string.IsNullOrEmpty(thoughtPart))
                                            {
                                                reasoningBuilder.Append(thoughtPart);
                                                onReasoningChunk?.Invoke(thoughtPart);
                                            }
                                            inThinkTag = false;
                                            var afterThink = cChunk[(endIdx + "</think>".Length)..];
                                            if (!string.IsNullOrEmpty(afterThink))
                                            {
                                                EnsureNoticeEmitted();
                                                contentBuilder.Append(afterThink);
                                                await EmitContentSmoothlyAsync(afterThink, onContentChunk, ct).ConfigureAwait(false);
                                            }
                                        }
                                        else
                                        {
                                            reasoningBuilder.Append(cChunk);
                                            onReasoningChunk?.Invoke(cChunk);
                                        }
                                    }
                                    else
                                    {
                                        EnsureNoticeEmitted();
                                        contentBuilder.Append(cChunk);
                                        await EmitContentSmoothlyAsync(cChunk, onContentChunk, ct).ConfigureAwait(false);
                                    }
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // Ignore transient malformed chunk
                }
            }

            var fullContent = contentBuilder.ToString();
            if (!string.IsNullOrWhiteSpace(fullContent))
                return (fullContent, statusCode);

            // If gateway returned a standard non-SSE JSON payload despite stream=true, parse and stream it smoothly
            if (rawNonSseBuilder.Length > 0)
            {
                try
                {
                    var rawJson = rawNonSseBuilder.ToString().Trim();
                    var extractedReasoning = ExtractReasoningFromJson(rawJson);
                    if (!string.IsNullOrWhiteSpace(extractedReasoning))
                    {
                        onReasoningChunk?.Invoke(extractedReasoning);
                    }

                    var extractedContent = ExtractContentFromJson(rawJson);
                    if (!string.IsNullOrWhiteSpace(extractedContent))
                    {
                        EnsureNoticeEmitted();
                        contentBuilder.Append(extractedContent);
                        await EmitContentSmoothlyAsync(extractedContent, onContentChunk, ct).ConfigureAwait(false);
                        return (contentBuilder.ToString(), statusCode);
                    }
                }
                catch
                {
                    // Fall through
                }
            }

            var fullReasoning = reasoningBuilder.ToString();
            if (!string.IsNullOrWhiteSpace(fullReasoning))
            {
                EnsureNoticeEmitted();
                contentBuilder.Append(fullReasoning);
                await EmitContentSmoothlyAsync(fullReasoning, onContentChunk, ct).ConfigureAwait(false);
                return (contentBuilder.ToString(), statusCode);
            }

            return (null, statusCode);
        }
    }

    private static async Task EmitContentSmoothlyAsync(string text, Action<string>? onContentChunk, CancellationToken ct)
    {
        if (onContentChunk == null || string.IsNullOrEmpty(text))
            return;

        // Small token chunks (< 24 chars) are emitted immediately as they arrive from the network
        if (text.Length <= 24)
        {
            onContentChunk(text);
            return;
        }

        // Larger buffered chunks are emitted progressively in small word-sized slices so output never dumps all at once
        const int sliceSize = 12;
        for (int i = 0; i < text.Length; i += sliceSize)
        {
            ct.ThrowIfCancellationRequested();
            int len = Math.Min(sliceSize, text.Length - i);
            onContentChunk(text.Substring(i, len));
            if (i + sliceSize < text.Length)
            {
                await Task.Delay(8, ct).ConfigureAwait(false);
            }
        }
    }

    private static string? ExtractReasoningFromJson(string body)
    {
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0)
        {
            var first = choices[0];
            if (first.TryGetProperty("message", out var msg))
            {
                if (msg.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
                    return rc.GetString();
                if (msg.TryGetProperty("reasoning", out var r) && r.ValueKind == JsonValueKind.String)
                    return r.GetString();
                if (msg.TryGetProperty("thinking", out var t) && t.ValueKind == JsonValueKind.String)
                    return t.GetString();
            }
        }
        return null;
    }

    private static string? ExtractContentFromJson(string body)
    {
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0)
        {
            var first = choices[0];
            if (first.TryGetProperty("message", out var msg))
            {
                if (msg.TryGetProperty("content", out var contentNode) &&
                    contentNode.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(contentNode.GetString()))
                {
                    return contentNode.GetString()!;
                }
                if (msg.TryGetProperty("reasoning_content", out var reasoningContentNode) &&
                    reasoningContentNode.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(reasoningContentNode.GetString()))
                {
                    return reasoningContentNode.GetString()!;
                }
                if (msg.TryGetProperty("reasoning", out var reasoningNode) &&
                    reasoningNode.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(reasoningNode.GetString()))
                {
                    return reasoningNode.GetString()!;
                }
                if (msg.TryGetProperty("thinking", out var thinkingNode) &&
                    thinkingNode.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(thinkingNode.GetString()))
                {
                    return thinkingNode.GetString()!;
                }
                if (msg.TryGetProperty("content", out var emptyContent))
                {
                    return emptyContent.GetString() ?? string.Empty;
                }
            }
            if (first.TryGetProperty("text", out var textNode))
            {
                return textNode.GetString() ?? string.Empty;
            }
        }
        if (doc.RootElement.TryGetProperty("response", out var respProp))
        {
            return respProp.GetString() ?? string.Empty;
        }
        return null;
    }
}

/// <summary>
/// Live HTTP client for Anthropic Claude Messages API (https://api.anthropic.com/v1/messages).
/// </summary>
public sealed class AnthropicLiveClient : IModelProviderClient
{
    private readonly string _apiKey;
    private readonly string _endpointUrl;
    private readonly HttpClient _httpClient;

    public string ProviderId => "anthropic";

    public AnthropicLiveClient(string apiKey, HttpClient? httpClient = null, string? endpointUrl = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        _apiKey = apiKey.Trim();
        _endpointUrl = string.IsNullOrWhiteSpace(endpointUrl)
            ? "https://api.anthropic.com/v1/messages"
            : endpointUrl.Trim();
        _httpClient = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public Task<string> GenerateAsync(string prompt, string model, CancellationToken ct = default)
    {
        return GenerateChatAsync([new LlmMessage("user", prompt)], model, ct);
    }

    public async Task<string> GenerateChatAsync(IReadOnlyList<LlmMessage> messages, string model, CancellationToken ct = default)
    {
        var cleanModel = string.IsNullOrWhiteSpace(model) ? "claude-3-7-sonnet-latest" : model.Trim();
        if (cleanModel.StartsWith("anthropic:", StringComparison.OrdinalIgnoreCase))
            cleanModel = cleanModel["anthropic:".Length..];
        if (cleanModel.Equals("claude-3-7-sonnet", StringComparison.OrdinalIgnoreCase))
            cleanModel = "claude-3-7-sonnet-latest";

        var systemMsg = messages.FirstOrDefault(m => m.Role.Equals("system", StringComparison.OrdinalIgnoreCase))?.Content
            ?? HermesPrompts.DefaultSystemPrompt;

        var anthropicMsgs = new List<object>();
        foreach (var m in messages.Where(m => !m.Role.Equals("system", StringComparison.OrdinalIgnoreCase)))
        {
            var role = m.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? "assistant" : "user";
            anthropicMsgs.Add(new { role = role, content = m.Content });
        }
        if (anthropicMsgs.Count == 0)
        {
            anthropicMsgs.Add(new { role = "user", content = "Hello" });
        }

        var payload = new
        {
            model = cleanModel,
            max_tokens = 4096,
            system = systemMsg,
            messages = anthropicMsgs.ToArray()
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, _endpointUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        req.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
        req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");

        using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Anthropic API returned {(int)resp.StatusCode}: {body}");
        }

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("content", out var contentArr) &&
            contentArr.ValueKind == JsonValueKind.Array &&
            contentArr.GetArrayLength() > 0)
        {
            var first = contentArr[0];
            if (first.TryGetProperty("text", out var textNode))
            {
                return textNode.GetString() ?? string.Empty;
            }
        }

        throw new InvalidOperationException("Anthropic API response did not contain content text.");
    }
}

/// <summary>
/// Live HTTP client for local or remote Ollama servers (/api/generate).
/// </summary>
public sealed class OllamaLiveClient : IModelProviderClient
{
    private readonly string _baseEndpoint;
    private readonly HttpClient _httpClient;

    public string ProviderId => "ollama";

    public OllamaLiveClient(string? baseEndpoint = null, HttpClient? httpClient = null)
    {
        var raw = string.IsNullOrWhiteSpace(baseEndpoint) ? "http://127.0.0.1:11434" : baseEndpoint.Trim().TrimEnd('/');
        if (raw.EndsWith("/api/generate", StringComparison.OrdinalIgnoreCase))
            raw = raw[..^"/api/generate".Length];
        _baseEndpoint = raw;
        _httpClient = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public Task<string> GenerateAsync(string prompt, string model, CancellationToken ct = default)
    {
        return GenerateChatAsync([new LlmMessage("user", prompt)], model, ct);
    }

    public async Task<string> GenerateChatAsync(IReadOnlyList<LlmMessage> messages, string model, CancellationToken ct = default)
    {
        var cleanModel = string.IsNullOrWhiteSpace(model) ? "qwen2.5-coder:latest" : model.Trim();
        if (cleanModel.StartsWith("ollama/", StringComparison.OrdinalIgnoreCase))
            cleanModel = cleanModel["ollama/".Length..];
        if (cleanModel.StartsWith("ollama:", StringComparison.OrdinalIgnoreCase))
            cleanModel = cleanModel["ollama:".Length..];

        var url = $"{_baseEndpoint}/api/chat";
        var systemMsg = messages.FirstOrDefault(m => m.Role.Equals("system", StringComparison.OrdinalIgnoreCase))?.Content
            ?? HermesPrompts.DefaultSystemPrompt;

        var ollamaMsgs = new List<object>
        {
            new { role = "system", content = systemMsg }
        };
        foreach (var m in messages.Where(m => !m.Role.Equals("system", StringComparison.OrdinalIgnoreCase)))
        {
            ollamaMsgs.Add(new { role = m.Role.ToLowerInvariant(), content = m.Content });
        }

        var payload = new
        {
            model = cleanModel,
            messages = ollamaMsgs.ToArray(),
            stream = false
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };

        using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Ollama API returned {(int)resp.StatusCode}: {body}");
        }

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("message", out var msgNode) &&
            msgNode.TryGetProperty("content", out var contentNode))
        {
            return contentNode.GetString() ?? string.Empty;
        }
        if (doc.RootElement.TryGetProperty("response", out var respNode))
        {
            return respNode.GetString() ?? string.Empty;
        }
        return string.Empty;
    }
}
