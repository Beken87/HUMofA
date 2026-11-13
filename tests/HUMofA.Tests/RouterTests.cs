using FluentAssertions;
using HUMofA.Agent;
using HUMofA.Tools;
using Xunit;

namespace HUMofA.Tests;

public class RouterTests
{
    private sealed class ThrowingProviderClient(string providerId) : IModelProviderClient
    {
        public string ProviderId => providerId;
        public Task<string> GenerateAsync(string prompt, string model, CancellationToken ct = default) =>
            throw new HttpRequestException($"Simulated {providerId} 503 outage");
    }

    [Fact]
    public async Task MultiModelRouter_SupportsAllSixProviders_AndFailsOverGracefully()
    {
        MultiModelRouter.BuiltInProviders.Should().HaveCount(6);
        MultiModelRouter.BuiltInProviders.Select(p => p.ProviderId).Should().Contain([
            "gemini",
            "openai",
            "anthropic",
            "openrouter",
            "nous",
            "ollama"
        ]);

        var router = new MultiModelRouter();
        router.RegisterProviderClient(new ThrowingProviderClient("gemini"));

        var (response, decision) = await router.RouteAndCompleteAsync(
            "Explain Fitts's Law Bezier trajectories",
            preferredModel: "gemini-2.5-flash");

        decision.SelectedProviderId.Should().Be("openai");
        decision.AttemptedProviders.Should().Contain(["gemini", "openai"]);
        response.Should().Contain("Explain Fitts's Law Bezier trajectories");
    }

    [Fact]
    public async Task MultimodalToolset_PerformsWebSearch_BrowserCdp_Vision_ImageGen_TtsAndStt()
    {
        var toolset = new MultimodalToolset();

        // 1. Web Search
        var results = await toolset.WebSearchAsync("NousResearch hermes-agent", maxResults: 3);
        results.Should().HaveCount(3);
        results[0].Title.Should().Contain("NousResearch hermes-agent");

        // 2. CDP Browser Automation
        var cdp = await toolset.NavigateAndExtractCdpAsync("https://github.com/NousResearch/hermes-agent");
        cdp.TargetUrl.Should().Be("https://github.com/NousResearch/hermes-agent");
        cdp.CdpWirePayloadJson.Should().Contain("Page.navigate");

        // 3. Image Generation (verify valid PNG signature)
        var imageArtifact = await toolset.GenerateImageAsync("Cyberpunk autonomous assistant dashboard", 160, 120);
        imageArtifact.PngBytes.Length.Should().BeGreaterThan(100);
        // PNG magic header: 89 50 4E 47
        imageArtifact.PngBytes[0].Should().Be(0x89);
        imageArtifact.PngBytes[1].Should().Be(0x50);
        imageArtifact.PngBytes[2].Should().Be(0x4E);
        imageArtifact.PngBytes[3].Should().Be(0x47);

        // 4. Vision & SOM Screen Analysis
        var vision = await toolset.AnalyzeScreenAsync("Locate the Send Button");
        vision.DetectedInteractiveElements.Should().BeGreaterThanOrEqualTo(2);
        vision.Summary.Should().NotBeNullOrWhiteSpace();

        // 5. TTS Synthesis (verify RIFF/WAVE header) & STT Transcription
        var speech = await toolset.SynthesizeSpeechAsync("Merhaba HUMofA sistemi aktif");
        speech.WavPcmBytes.Length.Should().BeGreaterThan(44);
        speech.WavPcmBytes[0].Should().Be((byte)'R');
        speech.WavPcmBytes[1].Should().Be((byte)'I');
        speech.WavPcmBytes[2].Should().Be((byte)'F');
        speech.WavPcmBytes[3].Should().Be((byte)'F');

        var transcript = await toolset.TranscribeAudioAsync(speech.WavPcmBytes);
        transcript.Should().Contain("STT Transcribed");
    }
}
