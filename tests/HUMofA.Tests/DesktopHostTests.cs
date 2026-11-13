using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using HUMofA.Desktop;
using Xunit;

namespace HUMofA.Tests;

public class DesktopHostTests
{
    [Fact]
    public async Task DesktopHost_ServesUiAssets_AndAllRestApiEndpointsEndToEnd()
    {
        var tempWorkDir = Path.Combine(Path.GetTempPath(), "humofa-desktop-test-" + Guid.NewGuid().ToString("N"));
        await using var host = new DesktopHost(workspaceDir: tempWorkDir);

        var baseUrl = await host.StartAsync(port: 0);
        baseUrl.Should().StartWith("http://127.0.0.1:");

        using var client = new HttpClient { BaseAddress = new Uri(baseUrl) };

        // 1. Static UI HTML/JS/CSS
        var htmlRes = await client.GetAsync("/");
        htmlRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await htmlRes.Content.ReadAsStringAsync();
        html.Should().Contain("HUMofA");

        var jsRes = await client.GetAsync("/app.js");
        jsRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var cssRes = await client.GetAsync("/style.css");
        cssRes.StatusCode.Should().Be(HttpStatusCode.OK);

        // 2. Status API
        var statusJson = await client.GetFromJsonAsync<JsonElement>("/api/status");
        statusJson.GetProperty("status").GetString().Should().Be("online");
        statusJson.GetProperty("version").GetString().Should().Be("1.0.0");

        // 3. Ghost Typing Synchronization API (Remote -> Desktop Chat + Autonomous Action)
        var ghostResp = await client.PostAsJsonAsync("/api/ghost-type", new
        {
            Text = "Sistem durumunu kontrol et",
            SourceChannel = "telegram",
            SenderId = "user-42",
            BaseDelayMs = 1.0
        });
        ghostResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var ghostData = await ghostResp.Content.ReadFromJsonAsync<JsonElement>();
        ghostData.GetProperty("synchronized").GetBoolean().Should().BeTrue();
        ghostData.GetProperty("ghostTypedText").GetString().Should().Be("Sistem durumunu kontrol et");

        // Verify chat history contains the synchronized ghost-typed message and autonomous reply
        var chatHistory = await client.GetFromJsonAsync<JsonElement>("/api/chat");
        chatHistory.GetArrayLength().Should().BeGreaterThanOrEqualTo(2);

        // 4. Skills Hub Installation API
        var skillResp = await client.PostAsJsonAsync("/api/skills/install", new
        {
            RepoOrUrl = "anthropics/skills",
            SkillSubPath = "workflow-synthesizer/SKILL.md"
        });
        skillResp.StatusCode.Should().Be(HttpStatusCode.OK);

        // 5. Memory Append & FTS5 Search API
        var memResp = await client.PostAsJsonAsync("/api/memory", new
        {
            Kind = "Memory",
            Title = "Ghost Typing Architecture",
            Content = "Remote messages stream character by character into the desktop UI."
        });
        memResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var searchHits = await client.GetFromJsonAsync<JsonElement>("/api/memory/search?q=character");
        searchHits.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);

        // 6. Kanban API
        var kanbanResp = await client.PostAsJsonAsync("/api/kanban", new
        {
            Title = "Verify SOM Overlay Endpoint",
            Description = "Ensure PNG image is returned"
        });
        kanbanResp.StatusCode.Should().Be(HttpStatusCode.OK);

        // 7. SOM Overlay Image API
        var somResp = await client.GetAsync("/api/computer-use/som");
        somResp.StatusCode.Should().Be(HttpStatusCode.OK);
        somResp.Content.Headers.ContentType?.MediaType.Should().Be("image/png");

        // 8. Natural Cron API
        var cronResp = await client.PostAsJsonAsync("/api/cron", new
        {
            Name = "Morning Digest",
            SchedulePhrase = "daily at 09:00",
            Prompt = "Summarize overnight build logs"
        });
        cronResp.StatusCode.Should().Be(HttpStatusCode.OK);

        // 9. Panic Kill-Switch API
        var panicResp = await client.PostAsync("/api/panic-stop", null);
        panicResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var panicJson = await panicResp.Content.ReadFromJsonAsync<JsonElement>();
        panicJson.GetProperty("panicTriggered").GetBoolean().Should().BeTrue();
    }
}
