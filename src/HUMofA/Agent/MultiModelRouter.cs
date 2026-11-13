using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HUMofA.Desktop;

namespace HUMofA.Agent;

public sealed record ModelProviderConfig(
    string ProviderId,
    string DefaultModel,
    string EndpointUrl,
    string? ApiKeyEnvVar,
    int PriorityOrder);

public sealed record ModelRoutingDecision(
    string SelectedProviderId,
    string SelectedModel,
    string Reason,
    IReadOnlyList<string> AttemptedProviders,
    bool UsedOfflineFallback);

public sealed record LlmMessage(string Role, string Content);

public interface IModelProviderClient
{
    string ProviderId { get; }
    Task<string> GenerateAsync(string prompt, string model, CancellationToken ct = default);
    Task<string> GenerateChatAsync(IReadOnlyList<LlmMessage> messages, string model, CancellationToken ct = default)
        => GenerateAsync(messages.LastOrDefault(m => m.Role.Equals("user", StringComparison.OrdinalIgnoreCase))?.Content ?? string.Empty, model, ct);
}

public interface IStreamingModelProviderClient : IModelProviderClient
{
    Task<string> GenerateStreamAsync(
        string prompt,
        string model,
        Action<string>? onReasoningChunk = null,
        Action<string>? onContentChunk = null,
        CancellationToken ct = default);

    Task<string> GenerateChatStreamAsync(
        IReadOnlyList<LlmMessage> messages,
        string model,
        Action<string>? onReasoningChunk = null,
        Action<string>? onContentChunk = null,
        CancellationToken ct = default)
        => GenerateStreamAsync(messages.LastOrDefault(m => m.Role.Equals("user", StringComparison.OrdinalIgnoreCase))?.Content ?? string.Empty, model, onReasoningChunk, onContentChunk, ct);
}

public interface IMultiModelRouter
{
    Task<string> CompleteAsync(string prompt, string? preferredModel = null, CancellationToken ct = default);
    Task<string> CompleteStreamAsync(
        string prompt,
        string? preferredModel = null,
        Action<string>? onReasoningChunk = null,
        Action<string>? onContentChunk = null,
        CancellationToken ct = default);
    Task<string> CompleteChatStreamAsync(
        IReadOnlyList<LlmMessage> messages,
        string? preferredModel = null,
        Action<string>? onReasoningChunk = null,
        Action<string>? onContentChunk = null,
        CancellationToken ct = default);
}

/// <summary>
/// Hermes-compatible Dynamic Multi-Model Reasoning Router.
/// Routes across Gemini, OpenAI, Anthropic, OpenRouter, Nous Portal, and local Ollama models
/// based on task complexity, explicit preference, and automatic failover chains.
/// </summary>
public sealed class MultiModelRouter : IMultiModelRouter
{
    public static readonly IReadOnlyList<ModelProviderConfig> BuiltInProviders =
    [
        new("gemini", "gemini-2.5-flash", "https://generativelanguage.googleapis.com/v1beta/models", "GEMINI_API_KEY", 1),
        new("openai", "gpt-4o", "https://api.openai.com/v1/chat/completions", "OPENAI_API_KEY", 2),
        new("anthropic", "claude-3-7-sonnet-latest", "https://api.anthropic.com/v1/messages", "ANTHROPIC_API_KEY", 3),
        new("openrouter", "openrouter/auto", "https://openrouter.ai/api/v1/chat/completions", "OPENROUTER_API_KEY", 4),
        new("nous", "nous/hermes-3-llama-3.1-405b", "https://inference-api.nousresearch.com/v1/chat/completions", "NOUS_API_KEY", 5),
        new("ollama", "ollama/qwen2.5-coder:latest", "http://127.0.0.1:11434/api/generate", null, 6)
    ];

    private readonly ConcurrentDictionary<string, IModelProviderClient> _customClients = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _fallbackOrder = ["gemini", "openai", "anthropic", "openrouter", "nous", "ollama"];
    private readonly object _orderLock = new();
    private string? _configuredDefaultModel;
    private string? _configuredPreferredProvider;

    public ModelRoutingDecision? LastRoutingDecision { get; private set; }

    public void RegisterProviderClient(IModelProviderClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _customClients[client.ProviderId] = client;
    }

    public bool RemoveProviderClient(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return false;
        return _customClients.TryRemove(providerId, out _);
    }

    public IReadOnlyList<string> GetConfiguredProviders() =>
        _customClients.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Dynamically configures live HTTP LLM clients from Desktop/CLI settings and environment variables.
    /// Can be called at startup and anytime settings are updated (hot-reload).
    /// </summary>
    public void ConfigureFromSettings(DesktopSettings settings, HttpMessageHandler? customHandler = null, bool useEnvironmentFallback = false)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var geminiKey = ResolveLiveKey(settings.ApiKeys?.GeminiApiKey, useEnvironmentFallback ? Environment.GetEnvironmentVariable("GEMINI_API_KEY") : null, customHandler != null);
        if (!string.IsNullOrWhiteSpace(geminiKey))
            RegisterProviderClient(new GeminiLiveClient(geminiKey, customHandler != null ? new HttpClient(customHandler) : null));
        else
            RemoveProviderClient("gemini");

        var openAiKey = ResolveLiveKey(settings.ApiKeys?.OpenAiApiKey, useEnvironmentFallback ? Environment.GetEnvironmentVariable("OPENAI_API_KEY") : null, customHandler != null);
        if (!string.IsNullOrWhiteSpace(openAiKey))
            RegisterProviderClient(new OpenAiCompatibleLiveClient("openai", "https://api.openai.com/v1/chat/completions", openAiKey, "gpt-4o", customHandler != null ? new HttpClient(customHandler) : null));
        else
            RemoveProviderClient("openai");

        var anthropicKey = ResolveLiveKey(settings.ApiKeys?.AnthropicApiKey, useEnvironmentFallback ? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") : null, customHandler != null);
        if (!string.IsNullOrWhiteSpace(anthropicKey))
            RegisterProviderClient(new AnthropicLiveClient(anthropicKey, customHandler != null ? new HttpClient(customHandler) : null));
        else
            RemoveProviderClient("anthropic");

        var openRouterKey = ResolveLiveKey(settings.ApiKeys?.OpenRouterApiKey, useEnvironmentFallback ? Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") : null, customHandler != null);
        if (!string.IsNullOrWhiteSpace(openRouterKey))
            RegisterProviderClient(new OpenAiCompatibleLiveClient("openrouter", "https://openrouter.ai/api/v1/chat/completions", openRouterKey, "openrouter/auto", customHandler != null ? new HttpClient(customHandler) : null));
        else
            RemoveProviderClient("openrouter");

        var nousKey = ResolveLiveKey(settings.ApiKeys?.NousApiKey, useEnvironmentFallback ? Environment.GetEnvironmentVariable("NOUS_API_KEY") : null, customHandler != null);
        if (!string.IsNullOrWhiteSpace(nousKey))
            RegisterProviderClient(new OpenAiCompatibleLiveClient("nous", "https://inference-api.nousresearch.com/v1/chat/completions", nousKey, "nous/hermes-3-llama-3.1-405b", customHandler != null ? new HttpClient(customHandler) : null));
        else
            RemoveProviderClient("nous");

        var evrenKey = ResolveLiveKey(settings.ApiKeys?.EvrenApiKey, useEnvironmentFallback ? Environment.GetEnvironmentVariable("EVREN_API_KEY") : null, customHandler != null);
        var evrenEndpoint = FirstNonEmpty(settings.ApiKeys?.EvrenEndpoint, "https://evren.ssb.gov.tr/v1/chat/completions")!;
        var preferred = settings.PreferredProvider?.Trim().ToLowerInvariant();
        if (preferred == "evren") preferred = "ssb-evren";

        if (!string.IsNullOrWhiteSpace(evrenKey) || (preferred == "ssb-evren" && (settings.OnboardingCompleted || customHandler != null)))
        {
            var evrenModel = !string.IsNullOrWhiteSpace(settings.Models?.DefaultModel) ? settings.Models.DefaultModel : "evren-default";
            RegisterProviderClient(new OpenAiCompatibleLiveClient(
                "ssb-evren",
                evrenEndpoint,
                evrenKey ?? string.Empty,
                evrenModel,
                customHandler != null ? new HttpClient(customHandler) : null,
                settings.ApiKeys?.EvrenCustomHeader,
                settings.ApiKeys?.EvrenIgnoreSslErrors ?? false));
        }
        else
        {
            RemoveProviderClient("ssb-evren");
        }

        var customKey = ResolveLiveKey(settings.ApiKeys?.CustomApiKey, null, customHandler != null);
        var customEndpoint = settings.ApiKeys?.CustomEndpoint;
        if (!string.IsNullOrWhiteSpace(customEndpoint) && (!string.IsNullOrWhiteSpace(customKey) || preferred == "custom"))
        {
            var customModel = !string.IsNullOrWhiteSpace(settings.Models?.DefaultModel) ? settings.Models.DefaultModel : "custom-model";
            RegisterProviderClient(new OpenAiCompatibleLiveClient(
                "custom",
                customEndpoint,
                customKey ?? string.Empty,
                customModel,
                customHandler != null ? new HttpClient(customHandler) : null,
                settings.ApiKeys?.CustomAuthHeader,
                settings.ApiKeys?.CustomIgnoreSslErrors ?? false));
        }
        else
        {
            RemoveProviderClient("custom");
        }

        var ollamaEndpoint = settings.ApiKeys?.OllamaEndpoint;
        if ((preferred == "ollama" && (settings.OnboardingCompleted || customHandler != null)) ||
            string.Equals(Environment.GetEnvironmentVariable("HUMOFA_ENABLE_LIVE_OLLAMA"), "true", StringComparison.OrdinalIgnoreCase))
        {
            RegisterProviderClient(new OllamaLiveClient(ollamaEndpoint, customHandler != null ? new HttpClient(customHandler) : null));
        }

        if (settings.OnboardingCompleted && !string.IsNullOrWhiteSpace(settings.Models?.DefaultModel))
        {
            _configuredDefaultModel = settings.Models.DefaultModel;
        }

        if (!string.IsNullOrWhiteSpace(preferred))
        {
            _configuredPreferredProvider = preferred;
            var newOrder = new List<string> { preferred };
            foreach (var p in BuiltInProviders.Select(b => b.ProviderId))
            {
                if (!newOrder.Contains(p, StringComparer.OrdinalIgnoreCase))
                    newOrder.Add(p);
            }
            SetFallbackOrder(newOrder);
        }
    }

    private static string? ResolveLiveKey(string? primary, string? fallback, bool allowMockTestKeys)
    {
        var key = FirstNonEmpty(primary, fallback);
        if (string.IsNullOrWhiteSpace(key))
            return null;
        if (!allowMockTestKeys &&
            (key.Contains("TestKey", StringComparison.OrdinalIgnoreCase) ||
             key.Contains("ChallengeKey", StringComparison.OrdinalIgnoreCase) ||
             key.Contains("TestOpenAiKey", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }
        return key;
    }

    /// <summary>
    /// Performs a live connection test against the specified provider and returns latency + status message.
    /// </summary>
    public static async Task<LlmConnectionTestResponse> TestProviderConnectionAsync(
        string providerId,
        string? apiKey,
        string? model = null,
        string? endpointUrl = null,
        HttpMessageHandler? customHandler = null,
        CancellationToken ct = default,
        string? customAuthHeader = null,
        bool ignoreSslErrors = false)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            return new LlmConnectionTestResponse(false, "unknown", string.Empty, 0, "Provider ID is required.");

        var normProvider = providerId.Trim().ToLowerInvariant();
        if (normProvider == "evren") normProvider = "ssb-evren";

        var resolvedModel = !string.IsNullOrWhiteSpace(model)
            ? model.Trim()
            : GetDefaultModelForProvider(normProvider);

        var sw = Stopwatch.StartNew();
        try
        {
            var httpClient = customHandler != null
                ? new HttpClient(customHandler)
                : OpenAiCompatibleLiveClient.CreateHttpClient(ignoreSslErrors, TimeSpan.FromSeconds(20));

            IModelProviderClient client = normProvider switch
            {
                "ssb-evren" => new OpenAiCompatibleLiveClient(
                    "ssb-evren",
                    FirstNonEmpty(endpointUrl, "https://evren.ssb.gov.tr/v1/chat/completions")!,
                    FirstNonEmpty(apiKey, Environment.GetEnvironmentVariable("EVREN_API_KEY")) ?? string.Empty,
                    resolvedModel,
                    httpClient,
                    customAuthHeader,
                    ignoreSslErrors),
                "custom" => new OpenAiCompatibleLiveClient(
                    "custom",
                    FirstNonEmpty(endpointUrl, null)
                        ?? throw new InvalidOperationException("Custom API Endpoint URL is required."),
                    apiKey ?? string.Empty,
                    resolvedModel,
                    httpClient,
                    customAuthHeader,
                    ignoreSslErrors),
                "gemini" => new GeminiLiveClient(
                    FirstNonEmpty(apiKey, Environment.GetEnvironmentVariable("GEMINI_API_KEY"))
                        ?? throw new InvalidOperationException("Gemini API key is required."),
                    httpClient),
                "openai" => new OpenAiCompatibleLiveClient(
                    "openai",
                    FirstNonEmpty(endpointUrl, "https://api.openai.com/v1/chat/completions")!,
                    FirstNonEmpty(apiKey, Environment.GetEnvironmentVariable("OPENAI_API_KEY"))
                        ?? throw new InvalidOperationException("OpenAI API key is required."),
                    resolvedModel,
                    httpClient,
                    customAuthHeader,
                    ignoreSslErrors),
                "anthropic" => new AnthropicLiveClient(
                    FirstNonEmpty(apiKey, Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"))
                        ?? throw new InvalidOperationException("Anthropic API key is required."),
                    httpClient),
                "openrouter" => new OpenAiCompatibleLiveClient(
                    "openrouter",
                    FirstNonEmpty(endpointUrl, "https://openrouter.ai/api/v1/chat/completions")!,
                    FirstNonEmpty(apiKey, Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"))
                        ?? throw new InvalidOperationException("OpenRouter API key is required."),
                    resolvedModel,
                    httpClient,
                    customAuthHeader,
                    ignoreSslErrors),
                "nous" => new OpenAiCompatibleLiveClient(
                    "nous",
                    FirstNonEmpty(endpointUrl, "https://inference-api.nousresearch.com/v1/chat/completions")!,
                    FirstNonEmpty(apiKey, Environment.GetEnvironmentVariable("NOUS_API_KEY"))
                        ?? throw new InvalidOperationException("Nous Portal API key is required."),
                    resolvedModel,
                    httpClient,
                    customAuthHeader,
                    ignoreSslErrors),
                "ollama" => new OllamaLiveClient(
                    FirstNonEmpty(endpointUrl, "http://127.0.0.1:11434"),
                    httpClient),
                _ => throw new InvalidOperationException($"Unsupported provider '{providerId}'.")
            };

            var reply = await client.GenerateAsync("Reply with the single word OK.", resolvedModel, ct).ConfigureAwait(false);
            sw.Stop();
            var preview = reply.Length <= 100 ? reply.Trim() : reply.Trim()[..100] + "...";
            return new LlmConnectionTestResponse(true, normProvider, resolvedModel, sw.ElapsedMilliseconds, $"Connected ({sw.ElapsedMilliseconds} ms): {preview}");
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new LlmConnectionTestResponse(false, normProvider, resolvedModel, sw.ElapsedMilliseconds, ex.Message);
        }
    }

    private static string? FirstNonEmpty(string? primary, string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(primary) && !primary.Contains("***", StringComparison.Ordinal))
            return primary.Trim();
        if (!string.IsNullOrWhiteSpace(fallback) && !fallback.Contains("***", StringComparison.Ordinal))
            return fallback.Trim();
        return null;
    }

    public void SetFallbackOrder(IEnumerable<string> providerIds)
    {
        ArgumentNullException.ThrowIfNull(providerIds);
        lock (_orderLock)
        {
            _fallbackOrder.Clear();
            _fallbackOrder.AddRange(providerIds);
        }
    }

    public async Task<string> CompleteAsync(string prompt, string? preferredModel = null, CancellationToken ct = default)
    {
        var (response, _) = await RouteAndCompleteAsync(prompt, preferredModel, ct).ConfigureAwait(false);
        return response;
    }

    public Task<string> CompleteStreamAsync(
        string prompt,
        string? preferredModel = null,
        Action<string>? onReasoningChunk = null,
        Action<string>? onContentChunk = null,
        CancellationToken ct = default)
    {
        return CompleteChatStreamAsync(
            [new LlmMessage("user", prompt)],
            preferredModel,
            onReasoningChunk,
            onContentChunk,
            ct);
    }

    public async Task<string> CompleteChatStreamAsync(
        IReadOnlyList<LlmMessage> messages,
        string? preferredModel = null,
        Action<string>? onReasoningChunk = null,
        Action<string>? onContentChunk = null,
        CancellationToken ct = default)
    {
        var (response, _) = await RouteAndCompleteChatStreamAsync(
            messages,
            preferredModel,
            onReasoningChunk,
            onContentChunk,
            ct).ConfigureAwait(false);
        return response;
    }

    public Task<(string Response, ModelRoutingDecision Decision)> RouteAndCompleteAsync(
        string prompt,
        string? preferredModel = null,
        CancellationToken ct = default)
    {
        return RouteAndCompleteStreamAsync(prompt, preferredModel, null, null, ct);
    }

    public Task<(string Response, ModelRoutingDecision Decision)> RouteAndCompleteStreamAsync(
        string prompt,
        string? preferredModel = null,
        Action<string>? onReasoningChunk = null,
        Action<string>? onContentChunk = null,
        CancellationToken ct = default)
    {
        return RouteAndCompleteChatStreamAsync(
            [new LlmMessage("user", prompt)],
            preferredModel,
            onReasoningChunk,
            onContentChunk,
            ct);
    }

    public async Task<(string Response, ModelRoutingDecision Decision)> RouteAndCompleteChatStreamAsync(
        IReadOnlyList<LlmMessage> messages,
        string? preferredModel = null,
        Action<string>? onReasoningChunk = null,
        Action<string>? onContentChunk = null,
        CancellationToken ct = default)
    {
        if (messages == null || messages.Count == 0)
            throw new ArgumentException("Messages collection cannot be empty.", nameof(messages));

        var lastUserPrompt = messages.LastOrDefault(m => m.Role.Equals("user", StringComparison.OrdinalIgnoreCase))?.Content
            ?? messages.Last().Content;

        var effectiveModel = preferredModel ?? _configuredDefaultModel;
        var (primaryProvider, resolvedModel, reason) = ResolveTargetModel(lastUserPrompt, effectiveModel);
        if (!string.IsNullOrWhiteSpace(_configuredPreferredProvider) &&
            _customClients.ContainsKey(_configuredPreferredProvider) &&
            !_customClients.ContainsKey(primaryProvider))
        {
            primaryProvider = _configuredPreferredProvider;
            if (!string.IsNullOrWhiteSpace(effectiveModel))
                resolvedModel = effectiveModel.Trim();
            reason = $"Configured {_configuredPreferredProvider} live provider preference";
        }
        var candidateChain = BuildCandidateChain(primaryProvider);
        var attempted = new List<string>();
        Exception? lastLiveException = null;
        bool hasConfiguredUserLiveProvider = !string.IsNullOrWhiteSpace(_configuredPreferredProvider) && _customClients.Count > 0;

        foreach (var providerId in candidateChain)
        {
            if (hasConfiguredUserLiveProvider && !_customClients.ContainsKey(providerId))
            {
                // Do not silently fall back to unconfigured offline mock providers when the user configured a real API!
                continue;
            }

            attempted.Add(providerId);
            var modelForAttempt = providerId.Equals(primaryProvider, StringComparison.OrdinalIgnoreCase)
                ? resolvedModel
                : GetDefaultModelForProvider(providerId);

            if (_customClients.TryGetValue(providerId, out var customClient))
            {
                try
                {
                    string output;
                    if (customClient is IStreamingModelProviderClient streamingClient)
                    {
                        output = await streamingClient.GenerateChatStreamAsync(
                            messages,
                            modelForAttempt,
                            onReasoningChunk,
                            onContentChunk,
                            ct).ConfigureAwait(false);
                    }
                    else
                    {
                        output = await customClient.GenerateChatAsync(messages, modelForAttempt, ct).ConfigureAwait(false);
                    }

                    if (!string.IsNullOrWhiteSpace(output))
                    {
                        var decision = new ModelRoutingDecision(providerId, modelForAttempt, reason, attempted, false);
                        LastRoutingDecision = decision;
                        return (output, decision);
                    }
                }
                catch (Exception ex)
                {
                    lastLiveException = ex;
                    // Continue to next fallback provider in chain
                    continue;
                }
            }
            else
            {
                // Check if live API key is configured; otherwise use deterministic offline mock fallback
                var cfg = BuiltInProviders.FirstOrDefault(p => p.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase));
                var apiKey = cfg?.ApiKeyEnvVar is not null ? Environment.GetEnvironmentVariable(cfg.ApiKeyEnvVar) : null;

                var simulatedResponse = $"[{providerId.ToUpperInvariant()}:{modelForAttempt}] Processed: {SummarizePrompt(lastUserPrompt)}";
                var decision = new ModelRoutingDecision(
                    SelectedProviderId: providerId,
                    SelectedModel: modelForAttempt,
                    Reason: reason,
                    AttemptedProviders: attempted,
                    UsedOfflineFallback: string.IsNullOrWhiteSpace(apiKey));
                LastRoutingDecision = decision;
                return (simulatedResponse, decision);
            }
        }

        if (hasConfiguredUserLiveProvider && lastLiveException != null)
        {
            var errDecision = new ModelRoutingDecision(
                SelectedProviderId: primaryProvider,
                SelectedModel: resolvedModel,
                Reason: $"Live API error: {lastLiveException.Message}",
                AttemptedProviders: attempted,
                UsedOfflineFallback: false);
            LastRoutingDecision = errDecision;
            return ($"⚠️ [{primaryProvider.ToUpperInvariant()}:{resolvedModel} API Hatası] {lastLiveException.Message}", errDecision);
        }

        // Ultimate safety fallback if all custom providers threw exceptions
        var fallbackDecision = new ModelRoutingDecision(
            SelectedProviderId: "ollama",
            SelectedModel: "ollama/local-fallback",
            Reason: "All primary providers failed; served by local resilience fallback",
            AttemptedProviders: attempted,
            UsedOfflineFallback: true);
        LastRoutingDecision = fallbackDecision;
        return ($"[FALLBACK:ollama/local-fallback] {SummarizePrompt(lastUserPrompt)}", fallbackDecision);
    }

    public static (string ProviderId, string ModelName, string Reason) ResolveTargetModel(string prompt, string? preferredModel)
    {
        if (!string.IsNullOrWhiteSpace(preferredModel))
        {
            var trimmed = preferredModel.Trim();
            if (trimmed.Contains(':'))
            {
                var parts = trimmed.Split(':', 2);
                return (parts[0].ToLowerInvariant(), parts[1], "Explicit provider:model specification");
            }
            if (trimmed.Contains('/'))
            {
                var prefix = trimmed.Split('/')[0].ToLowerInvariant();
                if (BuiltInProviders.Any(p => p.ProviderId.Equals(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    return (prefix, trimmed, "Explicit slash-prefixed provider specification");
                }
            }

            if (trimmed.StartsWith("evren", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("ssb-evren", StringComparison.OrdinalIgnoreCase))
                return ("ssb-evren", trimmed, "Explicit SSB Evren model preference");
            if (trimmed.StartsWith("gemini", StringComparison.OrdinalIgnoreCase))
                return ("gemini", trimmed, "Explicit Gemini model preference");
            if (trimmed.StartsWith("gpt", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("o1", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("o3", StringComparison.OrdinalIgnoreCase))
                return ("openai", trimmed, "Explicit OpenAI model preference");
            if (trimmed.StartsWith("claude", StringComparison.OrdinalIgnoreCase))
                return ("anthropic", trimmed, "Explicit Anthropic model preference");
            if (trimmed.StartsWith("hermes", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("nous", StringComparison.OrdinalIgnoreCase))
                return ("nous", trimmed, "Explicit Nous Portal model preference");
            if (trimmed.StartsWith("ollama", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("llama", StringComparison.OrdinalIgnoreCase))
                return ("ollama", trimmed, "Explicit Ollama local model preference");

            return ("openrouter", trimmed, "Explicit custom model routed via OpenRouter");
        }

        // Dynamic complexity-based multi-model routing
        if (prompt.Length > 500 ||
            prompt.Contains("architecture", StringComparison.OrdinalIgnoreCase) ||
            prompt.Contains("reason", StringComparison.OrdinalIgnoreCase) ||
            prompt.Contains("security", StringComparison.OrdinalIgnoreCase))
        {
            return ("gemini", "gemini-2.5-pro", "High-complexity reasoning task");
        }

        return ("gemini", "gemini-2.5-flash", "Low-latency interactive desktop task");
    }

    private List<string> BuildCandidateChain(string primaryProvider)
    {
        var chain = new List<string> { primaryProvider };
        lock (_orderLock)
        {
            foreach (var p in _fallbackOrder)
            {
                if (!chain.Contains(p, StringComparer.OrdinalIgnoreCase))
                    chain.Add(p);
            }
        }
        return chain;
    }

    private static string GetDefaultModelForProvider(string providerId) =>
        BuiltInProviders.FirstOrDefault(p => p.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase))?.DefaultModel
        ?? $"{providerId}-default";

    private static string SummarizePrompt(string prompt) =>
        prompt.Length <= 80 ? prompt.Trim() : prompt.Trim()[..80] + "...";
}

