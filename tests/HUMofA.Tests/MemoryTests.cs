using FluentAssertions;
using HUMofA.Memory;
using Xunit;

namespace HUMofA.Tests;

public class MemoryTests : IDisposable
{
    private readonly string _tempDir;

    public MemoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "humofa-mem-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Ignore file lock cleanup race
        }
    }

    [Fact]
    public async Task Fts5SearchIndex_UpsertsAndRanksDocumentsByBm25()
    {
        using var index = new Fts5SearchIndex(":memory:");

        await index.UpsertDocumentAsync("doc-1", "memory", "Kubernetes Deployment Fix", "Resolved CrashLoopBackOff by increasing memory limits in deployment yaml.");
        await index.UpsertDocumentAsync("doc-2", "memory", "UI Dark Theme Tweaks", "Adjusted CSS variables for slate background and cyan accents.");
        await index.UpsertDocumentAsync("doc-3", "skill", "Kubernetes Helm Rollback", "Use helm rollback release 1 when Kubernetes pods fail health probes.");

        var count = await index.CountAsync();
        count.Should().Be(3);

        var hits = await index.SearchAsync("Kubernetes", limit: 5);
        hits.Should().HaveCount(2);
        hits.Select(h => h.Id).Should().Contain(["doc-1", "doc-3"]);
    }

    [Fact]
    public async Task Fts5SearchIndex_DeleteDocumentAsync_DeletesFromIndex()
    {
        using var index = new Fts5SearchIndex(":memory:");
        await index.UpsertDocumentAsync("doc-del", "user", "Temporary Preference", "User prefers dark terminal themes.");
        (await index.CountAsync()).Should().Be(1);

        (await index.DeleteDocumentAsync("doc-del")).Should().BeTrue();
        (await index.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task UnifiedMemoryStore_InitializesDefaultMarkdownFiles_AndPersistsEntries()
    {
        using var store = new UnifiedMemoryStore(_tempDir);
        await store.InitializeAsync();

        File.Exists(store.SoulFilePath).Should().BeTrue();
        File.Exists(store.MemoryFilePath).Should().BeTrue();
        File.Exists(store.UserFilePath).Should().BeTrue();

        await store.AppendMemoryEntryAsync(
            MemoryDocumentType.Memory,
            "PostgreSQL Connection Pooling",
            "Configured PgBouncer transaction pooling with max_client_conn=500.");

        var hits = await store.SearchMemoriesAsync("PgBouncer");
        hits.Should().NotBeEmpty();
        hits.Should().Contain(h => h.Content.Contains("PgBouncer"));
    }

    [Fact]
    public async Task UnifiedMemoryStore_ImportsExternalAgentMemories_FromCursorAndClaude()
    {
        using var store = new UnifiedMemoryStore(_tempDir);
        await store.InitializeAsync();

        var externalWorkspace = Path.Combine(_tempDir, "cursor-workspace");
        Directory.CreateDirectory(externalWorkspace);
        await File.WriteAllTextAsync(
            Path.Combine(externalWorkspace, ".cursorrules"),
            "Always use .NET 10 and C# 14 primary constructors with strict nullability.");
        await File.WriteAllTextAsync(
            Path.Combine(externalWorkspace, "CLAUDE.md"),
            "Run full xUnit verification before committing.");

        var summary = await store.ImportFromExternalAgentAsync("Cursor", externalWorkspace);
        summary.ImportedFilesCount.Should().Be(2);

        var results = await store.SearchMemoriesAsync("Cursor");
        results.Should().NotBeEmpty();
    }

    [Fact]
    public async Task WatchAndLearnSynthesizer_RecordsSequenceAndSynthesizesSkillMarkdown()
    {
        var skillsDir = Path.Combine(_tempDir, "skills");
        var synthesizer = new WatchAndLearnSynthesizer(skillsDir);

        synthesizer.StartRecording();
        synthesizer.IsRecording.Should().BeTrue();

        synthesizer.RecordAction(new RecordedDesktopAction(
            ActionType: "click",
            X: 420,
            Y: 310,
            TargetWindow: "Submit Report Dialog"));

        synthesizer.RecordAction(new RecordedDesktopAction(
            ActionType: "type",
            Text: "confirm",
            TargetWindow: "Submit Report Dialog"));

        var actions = synthesizer.StopRecording();
        synthesizer.IsRecording.Should().BeFalse();
        actions.Should().HaveCount(2);

        var skill = await synthesizer.SynthesizeSkillAsync(
            "Automate Weekly Report Submission",
            "Clicks the submit button and types confirmation",
            actions,
            triggerPhrases: ["weekly report", "submit report"]);

        skill.SkillName.Should().Be("automate-weekly-report-submission");
        File.Exists(skill.SkillFilePath).Should().BeTrue();
        skill.MarkdownContent.Should().Contain("source: watch-and-learn");
        skill.StepCount.Should().Be(2);
    }
}
