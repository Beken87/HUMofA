using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using HUMofA.Agent;
using HUMofA.Cli;
using HUMofA.Desktop;
using HUMofA.Diff;
using HUMofA.Gateway;
using HUMofA.Memory;
using HUMofA.Sessions;
using HUMofA.Skills;
using HUMofA.SubAgents;
using Xunit;

namespace HUMofA.Tests;

public sealed class SetupAndLiveLlmTests
{
    private sealed class MockLlmHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, (HttpStatusCode Status, string JsonBody)> _responder;
        public List<HttpRequestMessage> CapturedRequests { get; } = new();
        public List<string> CapturedBodies { get; } = new();

        public MockLlmHttpHandler(Func<HttpRequestMessage, (HttpStatusCode Status, string JsonBody)> responder)
        {
            _responder = responder;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedRequests.Add(request);
            var body = request.Content != null
                ? await request.Content.ReadAsStringAsync(cancellationToken)
                : string.Empty;
            CapturedBodies.Add(body);

            var (status, json) = _responder(request);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }

    [Fact]
    public async Task GeminiLiveClient_SendsValidGenerateContentRequest_AndParsesReply()
    {
        var handler = new MockLlmHttpHandler(_ => (
            HttpStatusCode.OK,
            """
            {
              "candidates": [
                {
                  "content": {
                    "parts": [
                      { "text": "Hello from real Gemini 2.5 Flash!" }
                    ]
                  }
                }
              ]
            }
            """));

        var client = new GeminiLiveClient("AIzaSyLiveKey-Gemini-123456789", new HttpClient(handler));
        var reply = await client.GenerateAsync("Explain quantum computing", "gemini-2.5-flash");

        reply.Should().Be("Hello from real Gemini 2.5 Flash!");
        handler.CapturedRequests.Should().ContainSingle();
        handler.CapturedRequests[0].RequestUri!.ToString().Should().Contain("generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent");
        handler.CapturedBodies[0].Should().Contain("Explain quantum computing");
    }

    [Fact]
    public async Task OpenAiAndOpenRouterAndNousLiveClients_SendBearerAuth_AndParseChoices()
    {
        var handler = new MockLlmHttpHandler(req =>
        {
            var host = req.RequestUri!.Host;
            return (
                HttpStatusCode.OK,
                $$"""
                {
                  "choices": [
                    {
                      "message": {
                        "role": "assistant",
                        "content": "Live response from {{host}}"
                      }
                    }
                  ]
                }
                """);
        });

        var openAi = new OpenAiCompatibleLiveClient("openai", "https://api.openai.com/v1/chat/completions", "sk-live-openai-99999999", "gpt-4o", new HttpClient(handler));
        var openRouter = new OpenAiCompatibleLiveClient("openrouter", "https://openrouter.ai/api/v1/chat/completions", "sk-or-live-88888888", "openrouter/auto", new HttpClient(handler));
        var nous = new OpenAiCompatibleLiveClient("nous", "https://inference-api.nousresearch.com/v1/chat/completions", "nous-live-77777777", "nous/hermes-3-llama-3.1-405b", new HttpClient(handler));

        (await openAi.GenerateAsync("Test OpenAI", "gpt-4o")).Should().Be("Live response from api.openai.com");
        (await openRouter.GenerateAsync("Test OpenRouter", "openrouter/auto")).Should().Be("Live response from openrouter.ai");
        (await nous.GenerateAsync("Test Nous", "nous/hermes-3-llama-3.1-405b")).Should().Be("Live response from inference-api.nousresearch.com");

        handler.CapturedRequests.Should().HaveCount(3);
        handler.CapturedRequests[0].Headers.Authorization!.Scheme.Should().Be("Bearer");
        handler.CapturedRequests[0].Headers.Authorization!.Parameter.Should().Be("sk-live-openai-99999999");
    }

    [Fact]
    public async Task AnthropicLiveClient_SendsXApiKeyAndAnthropicVersion_AndParsesContentBlocks()
    {
        var handler = new MockLlmHttpHandler(_ => (
            HttpStatusCode.OK,
            """
            {
              "content": [
                { "type": "text", "text": "Hello from Claude 3.7 Sonnet!" }
              ]
            }
            """));

        var client = new AnthropicLiveClient("sk-ant-live-1234567890", new HttpClient(handler));
        var reply = await client.GenerateAsync("Write C# code", "claude-3-7-sonnet-latest");

        reply.Should().Be("Hello from Claude 3.7 Sonnet!");
        handler.CapturedRequests.Should().ContainSingle();
        handler.CapturedRequests[0].Headers.GetValues("x-api-key").First().Should().Be("sk-ant-live-1234567890");
        handler.CapturedRequests[0].Headers.GetValues("anthropic-version").First().Should().Be("2023-06-01");
    }

    [Fact]
    public async Task OllamaLiveClient_StripsOllamaPrefix_AndParsesLocalResponse()
    {
        var handler = new MockLlmHttpHandler(_ => (
            HttpStatusCode.OK,
            """
            {
              "model": "qwen2.5-coder:latest",
              "response": "Hello from local Ollama!"
            }
            """));

        var client = new OllamaLiveClient("http://127.0.0.1:11434", new HttpClient(handler));
        var reply = await client.GenerateAsync("Local inference", "ollama/qwen2.5-coder:latest");

        reply.Should().Be("Hello from local Ollama!");
        handler.CapturedBodies[0].Should().Contain("\"model\":\"qwen2.5-coder:latest\"");
    }

    [Fact]
    public async Task MultiModelRouter_ConfigureFromSettings_HotReloadsProviders_AndCanChangeAnytime()
    {
        var handler = new MockLlmHttpHandler(req =>
        {
            if (req.RequestUri!.Host.Contains("googleapis.com"))
            {
                return (HttpStatusCode.OK, """{"candidates":[{"content":{"parts":[{"text":"Gemini Live Reply"}]}}]}""");
            }
            return (HttpStatusCode.OK, """{"choices":[{"message":{"content":"OpenAI Live Reply"}}]}""");
        });

        var router = new MultiModelRouter();

        // 1. Initial setup with Gemini
        var settings1 = new DesktopSettings
        {
            OnboardingCompleted = true,
            PreferredProvider = "gemini",
            Models = new ModelSettings { DefaultModel = "gemini-2.5-flash" },
            ApiKeys = new ApiKeySettings { GeminiApiKey = "AIzaSyRealKey123456789" }
        };
        router.ConfigureFromSettings(settings1, handler);
        router.GetConfiguredProviders().Should().Contain("gemini");

        var (reply1, dec1) = await router.RouteAndCompleteAsync("Hello");
        reply1.Should().Be("Gemini Live Reply");
        dec1.SelectedProviderId.Should().Be("gemini");
        dec1.UsedOfflineFallback.Should().BeFalse();

        // 2. Reconfigure later to OpenAI (hot-reload)
        var settings2 = new DesktopSettings
        {
            OnboardingCompleted = true,
            PreferredProvider = "openai",
            Models = new ModelSettings { DefaultModel = "gpt-4o" },
            ApiKeys = new ApiKeySettings { OpenAiApiKey = "sk-live-openai-123456789" }
        };
        router.ConfigureFromSettings(settings2, handler);
        router.GetConfiguredProviders().Should().Contain("openai");
        router.GetConfiguredProviders().Should().NotContain("gemini");

        var (reply2, dec2) = await router.RouteAndCompleteAsync("Hello again");
        reply2.Should().Be("OpenAI Live Reply");
        dec2.SelectedProviderId.Should().Be("openai");
        dec2.SelectedModel.Should().Be("gpt-4o");
    }

    [Fact]
    public async Task DesktopHost_SetupLifecycle_FirstRunStatus_CompleteSetup_AndReconfigureLater()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "humofa-setup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);

        try
        {
            await using var host = new DesktopHost(workspaceDir: workDir);
            var baseUrl = await host.StartAsync(port: 0);
            using var client = new HttpClient { BaseAddress = new Uri(baseUrl) };

            // 1. First launch: onboardingCompleted must be false
            var status1 = await client.GetFromJsonAsync<SetupStatusResponse>("/api/setup/status");
            status1.Should().NotBeNull();
            status1!.OnboardingCompleted.Should().BeFalse();

            // 2. Complete setup via Setup Wizard endpoint
            var completeReq = new SetupCompleteRequest(
                PreferredProvider: "gemini",
                DefaultModel: "gemini-2.5-pro",
                ApiKey: "AIzaSyLiveWizardKey-987654321",
                AutonomyTier: "FullAutonomy",
                Theme: "dark");

            var completeRes = await client.PostAsJsonAsync("/api/setup/complete", completeReq);
            completeRes.StatusCode.Should().Be(HttpStatusCode.OK);
            var status2 = await completeRes.Content.ReadFromJsonAsync<SetupStatusResponse>();
            status2!.OnboardingCompleted.Should().BeTrue();
            status2.PreferredProvider.Should().Be("gemini");
            status2.DefaultModel.Should().Be("gemini-2.5-pro");
            status2.ConfiguredProviders.Should().Contain("gemini");
            status2.Settings.ApiKeys.GeminiApiKey.Should().Contain("***");

            // 3. Reconfigure later to Anthropic Claude via /api/setup/complete or /api/settings
            var reconfigReq = new SetupCompleteRequest(
                PreferredProvider: "anthropic",
                DefaultModel: "claude-3-7-sonnet-latest",
                ApiKey: "sk-ant-LiveClaudeKey-123456789",
                AutonomyTier: "Conservative",
                Theme: "light");

            var reconfigRes = await client.PostAsJsonAsync("/api/setup/complete", reconfigReq);
            reconfigRes.StatusCode.Should().Be(HttpStatusCode.OK);
            var status3 = await reconfigRes.Content.ReadFromJsonAsync<SetupStatusResponse>();
            status3!.PreferredProvider.Should().Be("anthropic");
            status3.DefaultModel.Should().Be("claude-3-7-sonnet-latest");
            status3.ConfiguredProviders.Should().Contain("anthropic");
            status3.Settings.Appearance.Theme.Should().Be("light");
            status3.Settings.Permissions.DefaultAutonomyTier.Should().Be("Conservative");

            // 4. Verify UI assets expose the Setup Wizard Modal and endpoints
            var html = await client.GetStringAsync("/");
            html.Should().Contain("id=\"setup-wizard-modal\"");
            html.Should().Contain("id=\"btn-setup-wizard\"");

            var js = await client.GetStringAsync("/app.js");
            js.Should().Contain("/api/setup/status");
            js.Should().Contain("/api/setup/test-llm");
            js.Should().Contain("/api/setup/complete");
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task InteractiveCli_SetupWizardAndApiKeyCommand_PersistAndReconfigureSharedSettings()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "humofa-cli-setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var settingsPath = Path.Combine(workDir, ".humofa-data", "settings.json");

        try
        {
            var console = new MockTerminalConsole();
            await using var sessionMgr = new SessionManager(Path.Combine(workDir, "sessions.db"));
            var subAgents = new SubAgentOrchestrator();
            var agentMgr = new AgentManager(subAgents);
            var diffService = new FileDiffService(Path.Combine(workDir, "backups"));
            var approval = new SmartApproval();
            using var memory = new UnifiedMemoryStore(Path.Combine(workDir, "memory"));
            await memory.InitializeAsync();
            var skills = new SkillManager(Path.Combine(workDir, "skills"), searchIndex: memory.SearchIndex);
            var router = new MultiModelRouter();

            var cli = new InteractiveCli(
                console,
                sessionMgr,
                agentMgr,
                diffService,
                approval,
                memory,
                skills,
                router,
                initialWorkingDir: workDir,
                settingsFilePath: settingsPath);

            await cli.InitializeSessionAsync();

            // 1. Run /setup interactive wizard (select 2=openai, enter key, enter model, enter tier)
            console.QueueInput("2");
            console.QueueInput("sk-live-cli-openai-key-123456789");
            console.QueueInput("gpt-4o");
            console.QueueInput("Guarded");

            await cli.HandleSlashCommandAsync("/setup");

            cli.ActiveModel.Should().Be("gpt-4o");
            router.GetConfiguredProviders().Should().Contain("openai");
            File.Exists(settingsPath).Should().BeTrue();

            // 2. Later change provider & key on the fly via /apikey command
            await cli.HandleSlashCommandAsync("/apikey gemini AIzaSyLiveCliGeminiKey-987654321");

            cli.ActiveModel.Should().Be("gemini-2.5-flash");
            router.GetConfiguredProviders().Should().Contain("gemini");

            var savedJson = await File.ReadAllTextAsync(settingsPath);
            savedJson.Should().Contain("AIzaSyLiveCliGeminiKey-987654321");
            savedJson.Should().Contain("sk-live-cli-openai-key-123456789");
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }
    }
}
