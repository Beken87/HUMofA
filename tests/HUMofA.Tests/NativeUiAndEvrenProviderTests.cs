using System.Net;
using System.Text;
using FluentAssertions;
using HUMofA.Agent;
using HUMofA.Desktop;
using HUMofA.Desktop.Native;
using Xunit;

namespace HUMofA.Tests;

public sealed class NativeUiAndEvrenProviderTests
{
    private sealed class MockEvrenHttpMessageHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public MockEvrenHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content != null)
            {
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }
            return _responder(request);
        }
    }

    [Fact]
    public void NormalizeChatCompletionsEndpoint_HandlesEvrenBaseAndVersionedUrls()
    {
        OpenAiCompatibleLiveClient.NormalizeChatCompletionsEndpoint("https://evren.ssb.gov.tr")
            .Should().Be("https://evren.ssb.gov.tr/v1/chat/completions");

        OpenAiCompatibleLiveClient.NormalizeChatCompletionsEndpoint("https://evren.ssb.gov.tr/api")
            .Should().Be("https://evren.ssb.gov.tr/api/v1/chat/completions");

        OpenAiCompatibleLiveClient.NormalizeChatCompletionsEndpoint("https://evren.ssb.gov.tr/v1")
            .Should().Be("https://evren.ssb.gov.tr/v1/chat/completions");

        OpenAiCompatibleLiveClient.NormalizeChatCompletionsEndpoint("https://evren.ssb.gov.tr/v1/chat/completions")
            .Should().Be("https://evren.ssb.gov.tr/v1/chat/completions");
    }

    [Fact]
    public void NormalizeModelsEndpoint_DerivesModelsUrlFromBaseOrChatUrl()
    {
        OpenAiCompatibleLiveClient.NormalizeModelsEndpoint("https://evren.ssb.gov.tr")
            .Should().Be("https://evren.ssb.gov.tr/v1/models");

        OpenAiCompatibleLiveClient.NormalizeModelsEndpoint("https://evren.ssb.gov.tr/v1/chat/completions")
            .Should().Be("https://evren.ssb.gov.tr/v1/models");
    }

    [Fact]
    public async Task OpenAiCompatibleLiveClient_SupportsSsbEvrenCustomHeaderAndModelDiscovery()
    {
        var handler = new MockEvrenHttpMessageHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
            {
                var modelsJson = """
                {
                  "data": [
                    { "id": "ssb-evren-70b-instruct" },
                    { "id": "qwen2.5-coder-32b" }
                  ]
                }
                """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(modelsJson, Encoding.UTF8, "application/json")
                };
            }

            var chatJson = """
            {
              "choices": [
                {
                  "message": { "content": "SSB Evren yanıtı başarılı." },
                  "finish_reason": "stop"
                }
              ],
              "usage": {
                "prompt_tokens": 18,
                "completion_tokens": 9
              }
            }
            """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(chatJson, Encoding.UTF8, "application/json")
            };
        });

        var httpClient = new HttpClient(handler);
        var evrenClient = new OpenAiCompatibleLiveClient(
            "ssb-evren",
            "https://evren.ssb.gov.tr",
            "evren-secret-token-123",
            "ssb-evren-70b-instruct",
            httpClient,
            customAuthHeader: "x-api-key");

        var models = await OpenAiCompatibleLiveClient.FetchAvailableModelsAsync(
            "https://evren.ssb.gov.tr",
            "evren-secret-token-123",
            customAuthHeader: "x-api-key",
            ignoreSslErrors: false,
            httpClient: httpClient);
        models.Should().Contain("ssb-evren-70b-instruct");
        models.Should().Contain("qwen2.5-coder-32b");

        var content = await evrenClient.GenerateAsync("Merhaba SSB Evren!", "ssb-evren-70b-instruct");

        content.Should().Contain("SSB Evren yanıtı başarılı.");
        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.ToString().Should().Be("https://evren.ssb.gov.tr/v1/chat/completions");
        handler.LastRequest.Headers.TryGetValues("x-api-key", out var headerVals).Should().BeTrue();
        headerVals!.First().Should().Be("evren-secret-token-123");
    }

    [Fact]
    public async Task MultiModelRouter_RoutesToSsbEvrenProviderWhenConfigured()
    {
        var router = new MultiModelRouter();
        var settings = new DesktopSettings
        {
            PreferredProvider = "ssb-evren",
            OnboardingCompleted = true,
            Models = new ModelSettings { DefaultModel = "ssb-evren-70b" },
            ApiKeys = new ApiKeySettings
            {
                EvrenEndpoint = "https://evren.ssb.gov.tr/v1/chat/completions",
                EvrenApiKey = "evren-key-999",
                EvrenCustomHeader = "Bearer"
            }
        };

        var handler = new MockEvrenHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
            {
              "choices": [{ "message": { "content": "Evren canlı cevap" }, "finish_reason": "stop" }],
              "usage": { "prompt_tokens": 10, "completion_tokens": 5 }
            }
            """, Encoding.UTF8, "application/json")
        });

        router.ConfigureFromSettings(settings, handler);
        router.GetConfiguredProviders().Should().Contain("ssb-evren");

        var (reply, decision) = await router.RouteAndCompleteAsync("Kod analizi yap");
        decision.SelectedProviderId.Should().Be("ssb-evren");
        decision.SelectedModel.Should().Be("ssb-evren-70b");
        reply.Should().Be("Evren canlı cevap");
    }

    [Fact]
    public void Program_ResolveStartupMode_MapsHofaSubcommandsCorrectly()
    {
        Program.ResolveStartupMode(Array.Empty<string>()).Should().Be(HofaStartupMode.Cli);
        Program.ResolveStartupMode(new[] { "desktop" }).Should().Be(HofaStartupMode.Desktop);
        Program.ResolveStartupMode(new[] { "--desktop" }).Should().Be(HofaStartupMode.Desktop);
        Program.ResolveStartupMode(new[] { "setup" }).Should().Be(HofaStartupMode.Setup);
        Program.ResolveStartupMode(new[] { "sessions" }).Should().Be(HofaStartupMode.Sessions);
        Program.ResolveStartupMode(new[] { "web", "5123" }).Should().Be(HofaStartupMode.WebServer);
        Program.ResolveStartupMode(new[] { "--headless" }).Should().Be(HofaStartupMode.Headless);
    }

    [Fact]
    public void NativeMainWindow_And_NativeSetupDialog_InstantiateCleanlyOnStaThread()
    {
        Exception? threadError = null;
        var staThread = new Thread(() =>
        {
            try
            {
                var tempDir = Path.Combine(Path.GetTempPath(), "hofa_native_ui_test_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);

                var core = new AppCore(tempDir);
                var settingsPath = Path.Combine(tempDir, "settings.json");

                var mainWindow = new NativeMainWindow(core, settingsPath);
                mainWindow.Title.Should().Contain("HOFA");
                mainWindow.ActiveView.Should().Be("chat");
                mainWindow.IsAdvancedMenuExpanded.Should().BeFalse();
                mainWindow.IsDevDrawerOpen.Should().BeFalse();

                mainWindow.SwitchView("skills");
                mainWindow.ActiveView.Should().Be("skills");

                mainWindow.ToggleDevDrawer();
                mainWindow.IsDevDrawerOpen.Should().BeTrue();

                var setupDialog = new NativeSetupDialog(
                    new DesktopSettings { PreferredProvider = "ssb-evren" },
                    core.Router,
                    settingsPath,
                    new NativeTheme("dark"));
                setupDialog.Title.Should().Contain("Kurulum");
                setupDialog.SelectedProviderId.Should().Be("ssb-evren");

                core.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                threadError = ex;
            }
        });

        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
        staThread.Join();

        threadError.Should().BeNull();
    }
}
