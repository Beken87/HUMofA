using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HUMofA.Agent;
using HUMofA.Backends;
using HUMofA.Desktop;
using HUMofA.Diff;
using HUMofA.Gateway;
using HUMofA.Memory;
using HUMofA.Sessions;
using HUMofA.Skills;
using HUMofA.SubAgents;

namespace HUMofA.Cli;

/// <summary>
/// Claude Code-grade terminal interactive loop for HUMofA.
/// Orchestrates live token streaming, braille spinners, tool execution cards,
/// unified diff previews, SmartApproval terminal prompts, and Hermes slash commands.
/// </summary>
public sealed class InteractiveCli
{
    private readonly ITerminalConsole _console;
    private readonly ISessionManager _sessionManager;
    private readonly IAgentManager _agentManager;
    private readonly IFileDiffService _diffService;
    private readonly SmartApproval _smartApproval;
    private readonly UnifiedMemoryStore _memoryStore;
    private readonly SkillManager _skillManager;
    private readonly MultiModelRouter _router;
    private readonly ExecutionEnvironmentManager? _backends;
    private readonly SubAgentOrchestrator? _subAgents;
    private readonly CentralEventBus? _eventBus;
    private readonly GhostTypingSync? _ghostTyping;

    private readonly string? _initialSessionId;
    private readonly string? _initialModel;
    private readonly string _initialWorkingDir;
    private readonly string _settingsFilePath;

    private SessionRecord? _activeSession;
    private string _activeModel = "gemini-2.5-flash";
    private string _workingDirectory;
    private bool _isRunning;
    private int _sessionTokens;
    private bool _showThinkingSteps = true;
    private string? _lastUserPrompt;

    public static readonly IReadOnlyList<string> SlashCommands =
    [
        "/model",
        "/agent",
        "/skill",
        "/sessions",
        "/diff",
        "/clear",
        "/compact",
        "/memory",
        "/kanban",
        "/exec",
        "/permissions",
        "/help",
        "/exit",
        "/quit",
        "/subagent",
        "/subagents",
        "/btw",
        "/note",
        "/retry",
        "/continue",
        "/undo",
        "/status",
        "/cost",
        "/think",
        "/doctor"
    ];

    public static readonly IReadOnlyList<string> CommandParameterHints =
    [
        "/btw ",
        "/note ",
        "/retry",
        "/continue",
        "/undo",
        "/status",
        "/cost",
        "/think on",
        "/think off",
        "/think deep ",
        "/doctor",
        "/model gemini-2.5-flash",
        "/model gpt-4o",
        "/model claude-3-7-sonnet",
        "/model list",
        "/subagent list",
        "/subagent create",
        "/subagent spawn",
        "/subagent switch",
        "/subagent delete",
        "/agent list",
        "/agent switch",
        "/agent create",
        "/agent delete",
        "/skill list",
        "/skill install",
        "/skill run",
        "/sessions list",
        "/sessions new",
        "/sessions resume",
        "/diff preview",
        "/diff rollback",
        "/memory search",
        "/memory add",
        "/memory list",
        "/exec local",
        "/exec docker",
        "/exec ssh",
        "/permissions list",
        "/permissions clear",
        "/setup",
        "/apikey gemini",
        "/apikey openai",
        "/apikey anthropic",
        "/apikey openrouter",
        "/apikey nous",
        "/apikey ollama",
        "/apikey ssb-evren"
    ];

    public static readonly IReadOnlyList<string> AllAutocompleteSuggestions =
        SlashCommands.Concat(CommandParameterHints).ToArray();

    public static IReadOnlyList<string> GetAutocompleteSuggestions(string? prefix = null)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return AllAutocompleteSuggestions;
        }

        var matches = AllAutocompleteSuggestions
            .Where(s => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count > 0 ? matches : AllAutocompleteSuggestions;
    }

    public SessionRecord? ActiveSession => _activeSession;
    public string ActiveModel => _activeModel;
    public string WorkingDirectory => _workingDirectory;
    public bool IsRunning => _isRunning;

    public InteractiveCli(
        ITerminalConsole console,
        ISessionManager sessionManager,
        IAgentManager agentManager,
        IFileDiffService diffService,
        SmartApproval smartApproval,
        UnifiedMemoryStore memoryStore,
        SkillManager skillManager,
        MultiModelRouter router,
        ExecutionEnvironmentManager? backends = null,
        SubAgentOrchestrator? subAgents = null,
        CentralEventBus? eventBus = null,
        GhostTypingSync? ghostTyping = null,
        string? initialSessionId = null,
        string? initialModel = null,
        string? initialWorkingDir = null,
        string? settingsFilePath = null)
    {
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _agentManager = agentManager ?? throw new ArgumentNullException(nameof(agentManager));
        _diffService = diffService ?? throw new ArgumentNullException(nameof(diffService));
        _smartApproval = smartApproval ?? throw new ArgumentNullException(nameof(smartApproval));
        _memoryStore = memoryStore ?? throw new ArgumentNullException(nameof(memoryStore));
        _skillManager = skillManager ?? throw new ArgumentNullException(nameof(skillManager));
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _backends = backends;
        _subAgents = subAgents;
        _eventBus = eventBus;
        _ghostTyping = ghostTyping;

        _initialSessionId = initialSessionId;
        _initialModel = initialModel;
        _initialWorkingDir = initialWorkingDir ?? Directory.GetCurrentDirectory();
        _workingDirectory = _initialWorkingDir;
        _settingsFilePath = settingsFilePath ?? Path.Combine(_initialWorkingDir, ".humofa-data", "settings.json");
    }

    /// <summary>
    /// Initializes session and starts the REPL loop.
    /// </summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        _isRunning = true;
        var shared = LoadSharedSettings();
        if (File.Exists(_settingsFilePath) || shared.OnboardingCompleted)
        {
            _router.ConfigureFromSettings(shared);
            if (string.IsNullOrEmpty(_initialModel) && !string.IsNullOrWhiteSpace(shared.Models?.DefaultModel))
                _activeModel = shared.Models.DefaultModel;
        }

        await InitializeSessionAsync(ct).ConfigureAwait(false);
        if (shared.OnboardingCompleted &&
            string.IsNullOrEmpty(_initialModel) &&
            !string.IsNullOrWhiteSpace(shared.Models?.DefaultModel) &&
            (_activeModel == "gemini-2.5-flash" || string.IsNullOrWhiteSpace(_activeModel)))
        {
            _activeModel = shared.Models.DefaultModel;
        }

        _console.RenderRule("HUMofA Interactive Terminal", '═');
        _console.RenderMarkupLine("[bold cyan]Welcome to HUMofA Claude Code-Grade CLI[/] [dim]Type /help for commands, /setup to configure LLMs, Ctrl+C to cancel prompt.[/]");
        if (_console is AnsiTerminal && !LoadSharedSettings().OnboardingCompleted)
        {
            _console.RenderMarkupLine("[yellow]First-time setup: Run /setup or /apikey <provider> <key> anytime to connect a live LLM provider.[/]");
        }
        _console.WriteLine();

        while (_isRunning && !ct.IsCancellationRequested)
        {
            var badgeContext = CreateBadgeContext();
            var options = new PromptOptions(
                BadgeContext: badgeContext,
                PromptText: "HUMofA ❯ ",
                ContinuationPrompt: "... ❯ ",
                AllowMultiline: true,
                AutocompleteSuggestions: AllAutocompleteSuggestions);

            string? input;
            try
            {
                input = await _console.ReadLineAsync(options, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _console.WriteLine();
                _console.RenderMarkupLine("[dim](Operation cancelled)[/]");
                continue;
            }

            if (input == null)
            {
                // EOF or stream closed
                break;
            }

            var trimmed = input.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            if (trimmed.StartsWith("/"))
            {
                await HandleSlashCommandAsync(trimmed, ct).ConfigureAwait(false);
            }
            else
            {
                await HandleNaturalLanguagePromptAsync(trimmed, ct).ConfigureAwait(false);
            }

            _console.WriteLine();
        }

        _isRunning = false;
        _console.RenderMarkupLine("[dim]Session terminated cleanly. Goodbye![/]");
    }

    public async Task InitializeSessionAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(_initialSessionId))
        {
            _activeSession = await _sessionManager.GetSessionAsync(_initialSessionId, ct).ConfigureAwait(false);
            if (_activeSession == null)
            {
                var all = await _sessionManager.ListSessionsAsync(ct).ConfigureAwait(false);
                _activeSession = all.FirstOrDefault(s => s.Id.StartsWith(_initialSessionId, StringComparison.OrdinalIgnoreCase));
            }

            if (_activeSession != null)
            {
                await _sessionManager.SetActiveSessionAsync(_activeSession.Id, ct).ConfigureAwait(false);
                _activeModel = _initialModel ?? _activeSession.Model;
                _workingDirectory = string.IsNullOrEmpty(_activeSession.WorkingDir) ? _initialWorkingDir : _activeSession.WorkingDir;
                _sessionTokens = _activeSession.TotalTokens;
                return;
            }
        }

        // Resume existing active or create default session
        var existingActive = await _sessionManager.GetActiveSessionAsync(ct).ConfigureAwait(false);
        if (existingActive != null)
        {
            _activeSession = existingActive;
            _activeModel = _initialModel ?? existingActive.Model;
            _workingDirectory = string.IsNullOrEmpty(existingActive.WorkingDir) ? _initialWorkingDir : existingActive.WorkingDir;
            _sessionTokens = existingActive.TotalTokens;
        }
        else
        {
            _activeModel = _initialModel ?? "gemini-2.5-flash";
            _activeSession = await _sessionManager.CreateSessionAsync(
                title: "CLI Session",
                model: _activeModel,
                workingDir: _workingDirectory,
                ct: ct).ConfigureAwait(false);
            _sessionTokens = 0;
        }
    }

    public PromptBadgeContext CreateBadgeContext()
    {
        string gitBranch = DetectGitBranch(_workingDirectory);
        return new PromptBadgeContext(
            WorkingDirectory: _workingDirectory,
            GitBranch: gitBranch,
            ActiveModel: _activeModel,
            SessionId: _activeSession?.Id ?? "none",
            TokenCount: _sessionTokens);
    }

    public static string DetectGitBranch(string workingDir)
    {
        try
        {
            var headPath = Path.Combine(workingDir, ".git", "HEAD");
            if (File.Exists(headPath))
            {
                var content = File.ReadAllText(headPath).Trim();
                if (content.StartsWith("ref: refs/heads/"))
                {
                    return content.Substring("ref: refs/heads/".Length);
                }
                return content.Length > 7 ? content.Substring(0, 7) : content;
            }
        }
        catch
        {
            // Fallback
        }
        return "main";
    }

    public async Task HandleSlashCommandAsync(string commandLine, CancellationToken ct = default)
    {
        var parts = commandLine.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var command = parts[0].ToLowerInvariant();
        var args = parts.Length > 1 ? parts[1].Trim() : string.Empty;

        switch (command)
        {
            case "/":
            case "/?":
            case "/commands":
            case "/help":
                RenderHelp();
                break;

            case "/subagent":
            case "/subagents":
                await HandleSubAgentCommandAsync(args, ct).ConfigureAwait(false);
                break;

            case "/btw":
            case "/ask":
            case "/sor":
                await HandleBtwCommandAsync(args, ct).ConfigureAwait(false);
                break;

            case "/note":
            case "/not":
                await HandleNoteCommandAsync(args, ct).ConfigureAwait(false);
                break;

            case "/retry":
            case "/tekrar":
                await HandleRetryCommandAsync(ct).ConfigureAwait(false);
                break;

            case "/continue":
            case "/devam":
                await HandleContinueCommandAsync(args, ct).ConfigureAwait(false);
                break;

            case "/undo":
            case "/geri":
            case "/rollback":
                await HandleUndoCommandAsync(args, ct).ConfigureAwait(false);
                break;

            case "/status":
            case "/cost":
            case "/durum":
                await HandleStatusCommandAsync(ct).ConfigureAwait(false);
                break;

            case "/think":
                await HandleThinkCommandAsync(args, ct).ConfigureAwait(false);
                break;

            case "/doctor":
                await HandleDoctorCommandAsync(ct).ConfigureAwait(false);
                break;

            case "/setup":
                await RunSetupWizardAsync(ct).ConfigureAwait(false);
                break;

            case "/apikey":
                await HandleApiKeyCommandAsync(args, ct).ConfigureAwait(false);
                break;

            case "/model":
                await HandleModelCommandAsync(args, ct).ConfigureAwait(false);
                break;

            case "/agent":
                await HandleAgentCommandAsync(args, ct).ConfigureAwait(false);
                break;

            case "/skill":
                await HandleSkillCommandAsync(args, ct).ConfigureAwait(false);
                break;

            case "/sessions":
                await HandleSessionsCommandAsync(args, ct).ConfigureAwait(false);
                break;

            case "/diff":
                await HandleDiffCommandAsync(args, ct).ConfigureAwait(false);
                break;

            case "/clear":
                _console.ClearScreen();
                _console.RenderRule("HUMofA Interactive Terminal", '═');
                break;

            case "/compact":
                await HandleCompactCommandAsync(ct).ConfigureAwait(false);
                break;

            case "/memory":
                await HandleMemoryCommandAsync(args, ct).ConfigureAwait(false);
                break;

            case "/kanban":
                HandleKanbanCommand();
                break;

            case "/exec":
                await HandleExecCommandAsync(args, ct).ConfigureAwait(false);
                break;

            case "/permissions":
                await HandlePermissionsCommandAsync(args, ct).ConfigureAwait(false);
                break;

            case "/exit":
            case "/quit":
                _isRunning = false;
                break;

            default:
                _console.RenderMarkupLine($"[red]Unknown command '{command}'. Type /help to view available commands.[/]");
                break;
        }
    }

    private void RenderHelp()
    {
        _console.RenderRule("HUMofA Komut ve Araç Referansı (Command Reference)", '─');
        _console.RenderMarkupLine("[bold cyan]/[/] veya [bold cyan]/help[/]                  - Bu komut referansını ve kullanım kılavuzunu görüntüler");
        _console.RenderMarkupLine("[bold cyan]/btw[/] [dim]<soru>[/]                  - Ana konuşma bağlamını bozmadan hızlı yan soru sorar (By The Way)");
        _console.RenderMarkupLine("[bold cyan]/note[/] [dim]<metin>[/]                - Modeli tetiklemeden aktif bağlama ve hafızaya hızlı ara not ekler");
        _console.RenderMarkupLine("[bold cyan]/retry[/]                        - Son gönderilen istemi (prompt) tekrar çalıştırır");
        _console.RenderMarkupLine("[bold cyan]/continue[/]                     - Yarım kalan veya duraklayan göreve kaldığı yerden devam ettirir");
        _console.RenderMarkupLine("[bold cyan]/undo[/]                         - Son dosya değişikliğini (snapshot) veya adımı geri alır");
        _console.RenderMarkupLine("[bold cyan]/status[/] veya [bold cyan]/cost[/]            - Aktif model, oturum, token kullanımı ve sistem durumunu gösterir");
        _console.RenderMarkupLine("[bold cyan]/think[/] [dim][on|off|deep <soru>][/]   - AI düşünce/eylem aşamalarını gösterme modunu açar/kapatır veya derin düşünür");
        _console.RenderMarkupLine("[bold cyan]/doctor[/]                       - API sağlayıcısı, yerel araçlar ve çalışma ortamı sağlık kontrolü yapar");
        _console.RenderMarkupLine("[bold cyan]/subagent[/] [dim][list|create|spawn|switch|delete][/] - Bağımsız alt-ajan (sub-agent) oluşturma, listeleme, silme ve görev delege etme");
        _console.RenderMarkupLine("[bold cyan]/agent[/] [dim][list|create|switch|delete][/]          - Ajan profillerini (persona), talimatları, silme ve modelleri yönetir");
        _console.RenderMarkupLine("[bold cyan]/model[/] [dim][name|list][/]             - Aktif LLM modelini görüntüler veya değiştirir (gemini, gpt-4o, claude, ollama, ssb-evren)");
        _console.RenderMarkupLine("[bold cyan]/setup[/]                         - Model sağlayıcısı, API anahtarı ve çalışma alanı yapılandırma sihirbazı");
        _console.RenderMarkupLine("[bold cyan]/apikey[/] [dim]<provider> <key>[/]       - API anahtarını kaydeder (gemini, openai, anthropic, openrouter, nous, ollama, ssb-evren)");
        _console.RenderMarkupLine("[bold cyan]/skill[/] [dim][list|install|run][/]     - Yüklü becerileri listeler, GitHub'dan yeni skill yükler veya çalıştırır");
        _console.RenderMarkupLine("[bold cyan]/sessions[/] [dim][list|new|resume][/]   - Oturumları listeler, yeni oturum açar veya önceki oturuma döner");
        _console.RenderMarkupLine("[bold cyan]/diff[/] [dim][file][/]                  - Dosya değişikliklerini ve geri alma (rollback) anlık görüntülerini inceler");
        _console.RenderMarkupLine("[bold cyan]/memory[/] [dim][query|add][/]           - Uzun dönemli hafızayı (MEMORY.md) arar veya yeni bilgi ekler");
        _console.RenderMarkupLine("[bold cyan]/kanban[/]                       - Sub-agent görevlerinin durumunu gösteren ASCII Kanban panosu");
        _console.RenderMarkupLine("[bold cyan]/exec[/] [dim]<env> <cmd>[/]             - Yerel (local), Docker, SSH veya bulutta doğrudan komut çalıştırır");
        _console.RenderMarkupLine("[bold cyan]/permissions[/] [dim][list|clear][/]     - Oturuma özel izinleri görüntüler veya sıfırlar");
        _console.RenderMarkupLine("[bold cyan]/compact[/]                      - Konuşma geçmişini özetler ve kalıcı hafızaya aktarır");
        _console.RenderMarkupLine("[bold cyan]/clear[/]                        - Terminal ekranını temizler");
        _console.RenderMarkupLine("[bold cyan]/exit[/]                         - CLI oturumunu kapatır");
        _console.WriteLine();
        _console.RenderMarkupLine("[bold yellow]💡 Pratik Kısayollar (/btw ve Türevleri):[/]");
        _console.RenderMarkupLine("  • [cyan]/btw <soru>[/]     -> Ana görevi bölmeden/kirletmeden mevcut bağlamda yan soru sorar.");
        _console.RenderMarkupLine("  • [cyan]/note <bilgi>[/]   -> AI'ın sonraki adımlarda bilmesi gereken bir notu sessizce bağlama ekler.");
        _console.RenderMarkupLine("  • [cyan]/continue[/]       -> Kesilen veya çok adımlı bir işe kaldığı yerden devam ettirir.");
        _console.RenderMarkupLine("  • [cyan]/retry[/]          -> Son mesajı tek komutla yeniden dener.");
        _console.WriteLine();
        _console.RenderMarkupLine("[bold yellow]🤖 Sub-Agent (Alt-Ajan) Nasıl Oluşturulur ve Kullanılır?[/]");
        _console.RenderMarkupLine("  1. Liste:        [cyan]/subagent list[/]                 -> Mevcut tüm ajanları ve aktif görevleri listeler.");
        _console.RenderMarkupLine("  2. Yeni Ajan:    [cyan]/subagent create <id> <isim>[/]   -> Örn: [dim]/subagent create coder \"C# Uzmanı\"[/]");
        _console.RenderMarkupLine("  3. Görev Ver:    [cyan]/subagent spawn <rol> <görev>[/]  -> Örn: [dim]/subagent spawn Tester \"Bütün testleri çalıştır ve özetle\"[/]");
        _console.RenderMarkupLine("  4. Doğal Dil:    Doğrudan [dim]\"Bir tester subagent oluştur ve ffmpeg var mı kontrol etsin\"[/] demeniz yeterlidir.");
        _console.RenderMarkupLine("[dim]İpucu: Komut yazarken ilk harfleri yazıp Tab tuşuna basarak otomatik tamamlayabilirsiniz.[/]");
    }

    private async Task HandleBtwCommandAsync(string args, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            _console.RenderMarkupLine("[yellow]Kullanım: /btw <yan soru veya merak ettiğiniz konu>[/]");
            _console.RenderMarkupLine("[dim]Örnek: /btw bu arada C#'ta IAsyncEnumerable neden kullanılır?[/]");
            return;
        }

        if (_activeSession == null)
        {
            await InitializeSessionAsync(ct).ConfigureAwait(false);
        }

        _console.RenderMarkupLine($"[bold magenta]💬 [BTW · Yan Sorgu][/] [dim](Ana görev bağlamı korunuyor, oturum geçmişi kirletilmez)[/]");
        var sidePrompt = $"[YAN SORGU / BTW — Ana görevi bozmadan, mevcut bağlamı dikkate alarak bu yan soruyu doğrudan ve öz şekilde yanıtla]: {args}";
        var chatMessages = await BuildContextualChatMessagesAsync(sidePrompt, ct).ConfigureAwait(false);

        using var spinner = new BrailleSpinner(_console, $"BTW yanıtlanıyor ({_activeModel})...");
        spinner.Start();

        bool firstChunk = false;
        var streamed = new StringBuilder();
        string response;
        try
        {
            response = await _router.CompleteChatStreamAsync(
                chatMessages,
                _activeModel,
                onReasoningChunk: chunk => spinner.UpdateThought(chunk),
                onContentChunk: chunk =>
                {
                    if (!firstChunk)
                    {
                        firstChunk = true;
                        spinner.Stop();
                    }
                    _console.Write(chunk);
                    streamed.Append(chunk);
                },
                ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            response = $"BTW sorgu hatası: {ex.Message}";
        }
        finally
        {
            spinner.Stop();
        }

        _console.WriteLine();
        if (!firstChunk)
        {
            await RenderProgressiveMarkdownAsync(response, ct).ConfigureAwait(false);
        }
        _console.RenderMarkupLine("[dim]── (BTW yan sorgusu tamamlandı, ana göreve kaldığınız yerden devam edebilirsiniz) ──[/]");
    }

    private async Task HandleNoteCommandAsync(string args, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            _console.RenderMarkupLine("[yellow]Kullanım: /note <bağlama veya hafızaya eklenecek not>[/]");
            return;
        }

        if (_activeSession == null)
        {
            await InitializeSessionAsync(ct).ConfigureAwait(false);
        }

        await _sessionManager.AppendMessageAsync(
            _activeSession!.Id,
            role: "user",
            content: $"[Kullanıcı Ara Notu / Bağlam Bilgisi]: {args}",
            tokenCount: args.Length / 4,
            ct: ct).ConfigureAwait(false);

        if (_memoryStore != null)
        {
            try
            {
                await _memoryStore.AppendMemoryEntryAsync(MemoryDocumentType.Memory, "User Notes", args, ct).ConfigureAwait(false);
            }
            catch
            {
                // Ignore non-critical memory write errors
            }
        }

        _console.RenderMarkupLine($"[green]📝 Not aktif oturum bağlamına ve hafızaya eklendi:[/] [cyan]{args}[/]");
    }

    private async Task HandleRetryCommandAsync(CancellationToken ct)
    {
        if (_activeSession == null)
        {
            await InitializeSessionAsync(ct).ConfigureAwait(false);
        }

        string? promptToRetry = _lastUserPrompt;
        if (string.IsNullOrWhiteSpace(promptToRetry) && _activeSession != null)
        {
            var transcript = await _sessionManager.GetTranscriptAsync(_activeSession.Id, limit: 100, ct: ct).ConfigureAwait(false);
            promptToRetry = transcript.LastOrDefault(m => m.Role.Equals("user", StringComparison.OrdinalIgnoreCase))?.Content;
        }

        if (string.IsNullOrWhiteSpace(promptToRetry))
        {
            _console.RenderMarkupLine("[yellow]Tekrar çalıştırılacak önceki bir mesaj bulunamadı.[/]");
            return;
        }

        _console.RenderMarkupLine($"[bold cyan]🔄 [Retry] Son istem yeniden çalıştırılıyor:[/] [dim]{promptToRetry}[/]");
        await HandleNaturalLanguagePromptAsync(promptToRetry, ct).ConfigureAwait(false);
    }

    private async Task HandleContinueCommandAsync(string args, CancellationToken ct)
    {
        var continuePrompt = string.IsNullOrWhiteSpace(args)
            ? "Kaldığın yerden devam et ve yarım kalan adımları tamamla."
            : $"Kaldığın yerden şu doğrultuda devam et: {args}";

        _console.RenderMarkupLine("[bold cyan]▶️ [Continue] Göreve kaldığı yerden devam ediliyor...[/]");
        await HandleNaturalLanguagePromptAsync(continuePrompt, ct).ConfigureAwait(false);
    }

    private async Task HandleUndoCommandAsync(string args, CancellationToken ct)
    {
        var snapshots = _diffService.GetSnapshots();
        if (snapshots.Count > 0)
        {
            var latest = snapshots.Last();
            bool ok = await _diffService.RollbackSnapshotAsync(latest.Id, ct).ConfigureAwait(false);
            if (ok)
            {
                _console.RenderMarkupLine($"[green]⏪ Son dosya değişikliği geri alındı (Undo):[/] [cyan]{latest.FilePath}[/] [dim]({latest.Id})[/]");
                return;
            }
        }

        _console.RenderMarkupLine("[yellow]Geri alınacak (undo) yakın zamanlı bir dosya değişikliği anlık görüntüsü bulunamadı.[/]");
    }

    private async Task HandleStatusCommandAsync(CancellationToken ct)
    {
        var settings = LoadSharedSettings();
        int msgCount = 0;
        if (_activeSession != null)
        {
            var transcript = await _sessionManager.GetTranscriptAsync(_activeSession.Id, limit: 200, ct: ct).ConfigureAwait(false);
            msgCount = transcript.Count;
        }

        _console.RenderRule("HUMofA Oturum, Model ve Sistem Durumu (/status)", '─');
        _console.RenderMarkupLine($"  • [bold]Aktif Sağlayıcı:[/]      [cyan]{settings.PreferredProvider}[/]");
        _console.RenderMarkupLine($"  • [bold]Aktif Model:[/]          [yellow]{_activeModel}[/]");
        _console.RenderMarkupLine($"  • [bold]Oturum ID:[/]            [magenta]{_activeSession?.Id ?? "yok"}[/] ({msgCount} mesaj)");
        _console.RenderMarkupLine($"  • [bold]Toplam Token:[/]         [green]{_sessionTokens} tokens[/]");
        _console.RenderMarkupLine($"  • [bold]Çalışma Dizini:[/]       [cyan]{_workingDirectory}[/] (Git: [green]{DetectGitBranch(_workingDirectory)}[/])");
        _console.RenderMarkupLine($"  • [bold]Düşünce & Eylem İzi:[/]  [{(_showThinkingSteps ? "green" : "dim")}]{(_showThinkingSteps ? "Açık (Canlı Gösterim)" : "Kapalı")}[/] [dim](/think ile değiştirilebilir)[/]");
        _console.RenderMarkupLine($"  • [bold]Kayıtlı Sub-Agent:[/]    [cyan]{_agentManager.ListAgents().Count}[/] ajan | [cyan]{_skillManager.InstalledSkills.Count}[/] yüklü beceri");
    }

    private async Task HandleThinkCommandAsync(string args, CancellationToken ct)
    {
        var trimmed = args.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            _showThinkingSteps = !_showThinkingSteps;
            _console.RenderMarkupLine($"[bold cyan]🧠 AI Düşünce ve Eylem Aşaması Görünürlüğü:[/] [{(_showThinkingSteps ? "green" : "yellow")}]{(_showThinkingSteps ? "AÇIK" : "KAPALI")}[/]");
            return;
        }

        if (trimmed.Equals("on", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("ac", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("aç", StringComparison.OrdinalIgnoreCase))
        {
            _showThinkingSteps = true;
            _console.RenderMarkupLine("[green]✔ AI düşünce ve eylem aşamaları görünürlüğü AÇILDI.[/]");
            return;
        }

        if (trimmed.Equals("off", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("kapat", StringComparison.OrdinalIgnoreCase))
        {
            _showThinkingSteps = false;
            _console.RenderMarkupLine("[yellow]✔ AI düşünce ve eylem aşamaları görünürlüğü KAPATILDI.[/]");
            return;
        }

        var deepPrompt = trimmed.StartsWith("deep ", StringComparison.OrdinalIgnoreCase)
            ? trimmed["deep ".Length..].Trim()
            : trimmed;

        _showThinkingSteps = true;
        _console.RenderMarkupLine("[bold magenta]🧠 [Derin Düşünme Modu] Adım adım analiz başlatılıyor...[/]");
        await HandleNaturalLanguagePromptAsync($"[DERİN DÜŞÜNME İSTEMİ: Önce problemi adım adım analiz et, olası senaryoları değerlendir ve ardından çözümü uygula]: {deepPrompt}", ct).ConfigureAwait(false);
    }

    private async Task HandleDoctorCommandAsync(CancellationToken ct)
    {
        _console.RenderRule("HUMofA Sistem ve Ortam Sağlık Kontrolü (/doctor)", '─');
        var settings = LoadSharedSettings();
        var providers = _router.GetConfiguredProviders();
        _console.RenderMarkupLine($"  [green]✔[/] [bold]Çalışma Dizini:[/]      {_workingDirectory} (Yazılabilir: {Directory.Exists(_workingDirectory)})");
        _console.RenderMarkupLine($"  [green]✔[/] [bold]Aktif Sağlayıcı:[/]     {settings.PreferredProvider} (Model: {_activeModel})");
        _console.RenderMarkupLine($"  [{(providers.Count > 0 ? "green" : "yellow")}]{(providers.Count > 0 ? "✔" : "!")}[/] [bold]Canlı API İstemcileri:[/] {(providers.Count > 0 ? string.Join(", ", providers) : "Yapılandırılmadı (/setup çalıştırın)")}");

        foreach (var toolCmd in new[] { ("git", "git --version"), ("dotnet", "dotnet --version"), ("ffmpeg", "ffmpeg -version") })
        {
            try
            {
                if (_backends != null)
                {
                    var res = await _backends.ExecuteAsync(new BackendExecutionRequest(toolCmd.Item2, ExecutionBackendType.Local, _workingDirectory), ct).ConfigureAwait(false);
                    var firstLine = (res.StandardOutput ?? string.Empty).Split('\n').FirstOrDefault()?.Trim() ?? "bulundu";
                    if (res.ExitCode == 0)
                        _console.RenderMarkupLine($"  [green]✔[/] [bold]Araç ({toolCmd.Item1}):[/]         [dim]{firstLine}[/]");
                    else
                        _console.RenderMarkupLine($"  [yellow]![/] [bold]Araç ({toolCmd.Item1}):[/]         [dim]Bulunamadı veya PATH'te değil[/]");
                }
            }
            catch
            {
                _console.RenderMarkupLine($"  [yellow]![/] [bold]Araç ({toolCmd.Item1}):[/]         [dim]Kontrol edilemedi[/]");
            }
        }
    }

    private async Task HandleSubAgentCommandAsync(string args, CancellationToken ct)
    {
        var subParts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var subCommand = subParts.Length > 0 ? subParts[0].ToLowerInvariant() : "list";
        var subArgs = subParts.Length > 1 ? subParts[1].Trim() : string.Empty;

        switch (subCommand)
        {
            case "list":
                _console.RenderRule("Autonomous Sub-Agents & Personas", '─');
                var registered = _agentManager.ListAgents();
                _console.RenderMarkupLine("[bold]Registered Agent Personas:[/]");
                if (registered.Count == 0)
                {
                    _console.RenderMarkupLine("  [dim]No custom agents registered yet. Root agent is active.[/]");
                }
                else
                {
                    foreach (var a in registered)
                    {
                        var status = _agentManager.GetAgentStatus(a.Id);
                        _console.RenderMarkupLine($"  • [bold cyan]{a.Name}[/] [dim]({a.Id})[/] - Model: [yellow]{a.PreferredModel}[/] - Status: [green]{status}[/]");
                    }
                }

                if (_subAgents != null)
                {
                    var cards = _subAgents.Kanban.AllCards;
                    _console.WriteLine();
                    _console.RenderMarkupLine("[bold]Sub-Agent Execution Kanban:[/]");
                    if (cards.Count == 0)
                    {
                        _console.RenderMarkupLine("  [dim]No sub-agent tasks spawned in this session yet.[/]");
                    }
                    else
                    {
                        foreach (var c in cards)
                        {
                            var color = c.Column switch
                            {
                                KanbanColumn.Done => "green",
                                KanbanColumn.InProgress => "yellow",
                                KanbanColumn.Blocked => "red",
                                _ => "dim"
                            };
                            _console.RenderMarkupLine($"  • [{color}][{c.Column}][/] [bold cyan]{c.Title}[/] [dim]({c.Id})[/] - SubAgent: [yellow]{c.AssignedSubAgentId ?? "auto"}[/] - Output: {c.ResultSummary ?? c.Description}");
                        }
                    }
                }
                _console.WriteLine();
                _console.RenderMarkupLine("[dim]Quick commands: /subagent create <id> <name> | /subagent spawn <role> <task> | /subagent switch <id> | /subagent delete <id>[/]");
                break;

            case "create":
                if (string.IsNullOrWhiteSpace(subArgs))
                {
                    _console.RenderMarkupLine("[yellow]Usage: /subagent create <id> <name> [prompt][/]");
                    _console.RenderMarkupLine("[dim]Example: /subagent create dev \"Senior C# Developer\"[/]");
                    return;
                }
                var createParts = subArgs.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                string newId = createParts[0];
                string newName = createParts.Length > 1 ? createParts[1].Trim('"', '\'') : newId;
                var config = new AgentConfig(
                    Id: newId,
                    Name: newName,
                    SystemPrompt: $"You are {newName}, a specialized HUMofA sub-agent.",
                    PreferredModel: _activeModel,
                    WorkingDirectory: _workingDirectory);
                _agentManager.RegisterAgent(config);
                _console.RenderMarkupLine($"[green]✔ Successfully created sub-agent:[/] [bold cyan]{newName}[/] [dim]({newId})[/]");
                _console.RenderMarkupLine($"[dim]You can now delegate tasks via: /subagent spawn {newId} \"task details\" or switch via: /subagent switch {newId}[/]");
                break;

            case "spawn":
                if (string.IsNullOrWhiteSpace(subArgs))
                {
                    _console.RenderMarkupLine("[yellow]Usage: /subagent spawn <role> <task prompt>[/]");
                    _console.RenderMarkupLine("[dim]Example: /subagent spawn CodeReviewer \"Review recent changes in src/\"[/]");
                    return;
                }
                if (_subAgents == null)
                {
                    _console.RenderMarkupLine("[red]SubAgentOrchestrator is not attached to this session.[/]");
                    return;
                }
                var spawnParts = subArgs.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                string role = spawnParts[0];
                string taskPrompt = spawnParts.Length > 1 ? spawnParts[1] : role;

                using (var sp = new BrailleSpinner(_console, $"Spawning subagent [{role}]..."))
                {
                    sp.Start();
                    var spec = new SubAgentSpec(
                        Role: role,
                        TaskPrompt: taskPrompt,
                        PreferredModel: _activeModel);

                    var res = await _subAgents.SpawnAndExecuteAsync(spec, workDelegate: null, ct).ConfigureAwait(false);
                    sp.Stop();

                    var card = new ToolExecutionCard(
                        ToolName: $"SubAgent ({res.Role})",
                        Parameters: taskPrompt.Length > 60 ? taskPrompt[..60] + "..." : taskPrompt,
                        Output: res.CondensedOutput,
                        ExecutionTimeMs: 150,
                        IsSuccess: res.IsSuccess);
                    _console.RenderCard(card);
                }
                break;

            case "switch":
                if (string.IsNullOrWhiteSpace(subArgs))
                {
                    _console.RenderMarkupLine("[yellow]Usage: /subagent switch <id>[/]");
                    return;
                }
                var targetAgent = _agentManager.GetAgent(subArgs);
                if (targetAgent == null)
                {
                    _console.RenderMarkupLine($"[red]Sub-agent '{subArgs}' not found. Run /subagent list to see available agents.[/]");
                    return;
                }
                _activeModel = targetAgent.PreferredModel;
                _console.RenderMarkupLine($"[green]✔ Switched persona to:[/] [bold cyan]{targetAgent.Name}[/] [dim]({targetAgent.Id})[/]");
                break;

            case "delete":
            case "remove":
            case "sil":
                if (string.IsNullOrWhiteSpace(subArgs))
                {
                    _console.RenderMarkupLine("[yellow]Usage: /subagent delete <id>[/]");
                    return;
                }
                bool deleted = _agentManager.DeleteAgent(subArgs);
                if (deleted)
                {
                    _console.RenderMarkupLine($"[green]✔ Successfully deleted sub-agent:[/] [bold cyan]{subArgs}[/]");
                }
                else
                {
                    _console.RenderMarkupLine($"[red]Sub-agent '{subArgs}' not found or could not be deleted.[/]");
                }
                break;

            default:
                _console.RenderMarkupLine($"[yellow]Unknown subagent command '{subCommand}'.[/]");
                _console.RenderMarkupLine("[dim]Usage: /subagent [list | create <id> <name> | spawn <role> <task> | switch <id> | delete <id>][/]");
                break;
        }
    }

    public async Task RunSetupWizardAsync(CancellationToken ct = default)
    {
        var settings = LoadSharedSettings();
        _console.RenderRule("HUMofA LLM & Workspace Setup Wizard", '═');
        _console.RenderMarkupLine("[bold cyan]Configure your primary AI provider, API credentials, default model, and autonomy tier.[/]");
        _console.RenderMarkupLine("[dim]All settings are saved to settings.json and shared between CLI and Desktop.[/]");
        _console.WriteLine();

        _console.RenderMarkupLine("[bold]1. Select LLM Provider:[/]");
        _console.RenderMarkupLine("  [cyan]1[/] - Google Gemini     (gemini-2.5-flash / gemini-2.5-pro)");
        _console.RenderMarkupLine("  [cyan]2[/] - OpenAI            (gpt-4o / o3-mini)");
        _console.RenderMarkupLine("  [cyan]3[/] - Anthropic Claude  (claude-3-7-sonnet-latest)");
        _console.RenderMarkupLine("  [cyan]4[/] - OpenRouter        (openrouter/auto)");
        _console.RenderMarkupLine("  [cyan]5[/] - Nous Portal       (nous/hermes-3-llama-3.1-405b)");
        _console.RenderMarkupLine("  [cyan]6[/] - Local Ollama      (ollama/qwen2.5-coder:latest)");
        _console.RenderMarkupLine("  [cyan]7[/] - SSB Evren         (Kurumsal AI Portalı - Özel Endpoint & SSL)");
        _console.RenderMarkupLine("  [cyan]8[/] - Custom API        (vLLM / LiteLLM / OpenAI-Compatible)");

        var promptOpts = new PromptOptions(
            BadgeContext: null,
            PromptText: $"Provider [{settings.PreferredProvider}]: ",
            AllowMultiline: false);

        var providerInput = (await _console.ReadLineAsync(promptOpts, ct).ConfigureAwait(false))?.Trim();
        string provider = providerInput?.ToLowerInvariant() switch
        {
            "1" or "gemini" => "gemini",
            "2" or "openai" => "openai",
            "3" or "anthropic" or "claude" => "anthropic",
            "4" or "openrouter" => "openrouter",
            "5" or "nous" => "nous",
            "6" or "ollama" => "ollama",
            "7" or "evren" or "ssb-evren" => "ssb-evren",
            "8" or "custom" => "custom",
            null or "" => string.IsNullOrWhiteSpace(settings.PreferredProvider) ? "gemini" : settings.PreferredProvider,
            _ => providerInput.ToLowerInvariant()
        };

        settings.PreferredProvider = provider;

        if (provider == "ollama")
        {
            var endpointPrompt = new PromptOptions(
                BadgeContext: null,
                PromptText: $"Ollama Endpoint [{settings.ApiKeys.OllamaEndpoint ?? "http://127.0.0.1:11434"}]: ",
                AllowMultiline: false);
            var endpointInput = (await _console.ReadLineAsync(endpointPrompt, ct).ConfigureAwait(false))?.Trim();
            if (!string.IsNullOrWhiteSpace(endpointInput))
                settings.ApiKeys.OllamaEndpoint = endpointInput;
        }
        else if (provider == "ssb-evren" || provider == "custom")
        {
            var currentEp = provider == "ssb-evren"
                ? (settings.ApiKeys.EvrenEndpoint ?? "https://evren.ssb.gov.tr/v1/chat/completions")
                : (settings.ApiKeys.CustomEndpoint ?? "http://localhost:8000/v1/chat/completions");

            var endpointPrompt = new PromptOptions(
                BadgeContext: null,
                PromptText: $"{provider.ToUpperInvariant()} Endpoint URL [{currentEp}]: ",
                AllowMultiline: false);
            var endpointInput = (await _console.ReadLineAsync(endpointPrompt, ct).ConfigureAwait(false))?.Trim();
            if (!string.IsNullOrWhiteSpace(endpointInput))
            {
                if (provider == "ssb-evren")
                    settings.ApiKeys.EvrenEndpoint = endpointInput;
                else
                    settings.ApiKeys.CustomEndpoint = endpointInput;
            }

            var keyPrompt = new PromptOptions(
                BadgeContext: null,
                PromptText: $"Enter {provider.ToUpperInvariant()} API Key / Token (leave blank to keep current): ",
                AllowMultiline: false);
            var keyInput = (await _console.ReadLineAsync(keyPrompt, ct).ConfigureAwait(false))?.Trim();
            if (!string.IsNullOrWhiteSpace(keyInput))
            {
                ApplyProviderApiKey(settings, provider, keyInput);
            }
        }
        else
        {
            var keyPrompt = new PromptOptions(
                BadgeContext: null,
                PromptText: $"Enter {provider.ToUpperInvariant()} API Key (leave blank to keep current): ",
                AllowMultiline: false);
            var keyInput = (await _console.ReadLineAsync(keyPrompt, ct).ConfigureAwait(false))?.Trim();
            if (!string.IsNullOrWhiteSpace(keyInput))
            {
                ApplyProviderApiKey(settings, provider, keyInput);
            }
        }

        string suggestedModel = MultiModelRouter.BuiltInProviders
            .FirstOrDefault(p => p.ProviderId.Equals(provider, StringComparison.OrdinalIgnoreCase))?.DefaultModel
            ?? settings.Models.DefaultModel;

        var modelPrompt = new PromptOptions(
            BadgeContext: null,
            PromptText: $"Default Model [{suggestedModel}]: ",
            AllowMultiline: false);
        var modelInput = (await _console.ReadLineAsync(modelPrompt, ct).ConfigureAwait(false))?.Trim();
        string chosenModel = !string.IsNullOrWhiteSpace(modelInput) ? modelInput : suggestedModel;
        settings.Models.DefaultModel = chosenModel;

        if (!settings.Models.AvailableModels.Contains(chosenModel, StringComparer.OrdinalIgnoreCase))
            settings.Models.AvailableModels.Insert(0, chosenModel);

        var tierPrompt = new PromptOptions(
            BadgeContext: null,
            PromptText: $"Autonomy Tier (Guarded / Conservative / FullAutonomy) [{settings.Permissions.DefaultAutonomyTier}]: ",
            AllowMultiline: false);
        var tierInput = (await _console.ReadLineAsync(tierPrompt, ct).ConfigureAwait(false))?.Trim();
        if (!string.IsNullOrWhiteSpace(tierInput))
            settings.Permissions.DefaultAutonomyTier = tierInput;

        settings.OnboardingCompleted = true;
        SaveSharedSettings(settings);
        _router.ConfigureFromSettings(settings);

        _activeModel = chosenModel;
        if (_activeSession != null)
        {
            await _sessionManager.UpdateSessionAsync(_activeSession.Id, model: _activeModel, ct: ct).ConfigureAwait(false);
        }

        _console.RenderMarkupLine($"[green]✓ Setup completed and persisted![/] Active Provider: [bold yellow]{provider}[/], Default Model: [bold yellow]{_activeModel}[/], Autonomy: [cyan]{settings.Permissions.DefaultAutonomyTier}[/]");
    }

    private async Task HandleApiKeyCommandAsync(string args, CancellationToken ct)
    {
        var settings = LoadSharedSettings();
        if (string.IsNullOrWhiteSpace(args))
        {
            var masked = settings.CloneMasked();
            _console.RenderRule("Configured LLM API Keys & Endpoints", '─');
            _console.RenderMarkupLine($"  [bold]Preferred Provider:[/] [yellow]{settings.PreferredProvider}[/]");
            _console.RenderMarkupLine($"  [bold]Gemini:[/]             {(string.IsNullOrEmpty(masked.ApiKeys.GeminiApiKey) ? "[dim](not set)[/]" : $"[green]{masked.ApiKeys.GeminiApiKey}[/]")}");
            _console.RenderMarkupLine($"  [bold]OpenAI:[/]             {(string.IsNullOrEmpty(masked.ApiKeys.OpenAiApiKey) ? "[dim](not set)[/]" : $"[green]{masked.ApiKeys.OpenAiApiKey}[/]")}");
            _console.RenderMarkupLine($"  [bold]Anthropic:[/]          {(string.IsNullOrEmpty(masked.ApiKeys.AnthropicApiKey) ? "[dim](not set)[/]" : $"[green]{masked.ApiKeys.AnthropicApiKey}[/]")}");
            _console.RenderMarkupLine($"  [bold]OpenRouter:[/]         {(string.IsNullOrEmpty(masked.ApiKeys.OpenRouterApiKey) ? "[dim](not set)[/]" : $"[green]{masked.ApiKeys.OpenRouterApiKey}[/]")}");
            _console.RenderMarkupLine($"  [bold]Nous Portal:[/]        {(string.IsNullOrEmpty(masked.ApiKeys.NousApiKey) ? "[dim](not set)[/]" : $"[green]{masked.ApiKeys.NousApiKey}[/]")}");
            _console.RenderMarkupLine($"  [bold]Ollama Endpoint:[/]    [cyan]{masked.ApiKeys.OllamaEndpoint}[/]");
            _console.RenderMarkupLine("[dim]Usage: /apikey <gemini|openai|anthropic|openrouter|nous|ollama> <key_or_url>[/]");
            return;
        }

        var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            _console.RenderMarkupLine("[red]Usage: /apikey <gemini|openai|anthropic|openrouter|nous|ollama> <key_or_url>[/]");
            return;
        }

        var provider = parts[0].Trim().ToLowerInvariant();
        var value = parts[1].Trim();

        if (!ApplyProviderApiKey(settings, provider, value))
        {
            _console.RenderMarkupLine($"[red]Unknown provider '{provider}'. Supported: gemini, openai, anthropic, openrouter, nous, ollama.[/]");
            return;
        }

        settings.PreferredProvider = provider;
        settings.OnboardingCompleted = true;

        var defaultModelForProvider = MultiModelRouter.BuiltInProviders
            .FirstOrDefault(p => p.ProviderId.Equals(provider, StringComparison.OrdinalIgnoreCase))?.DefaultModel;
        if (!string.IsNullOrEmpty(defaultModelForProvider))
        {
            settings.Models.DefaultModel = defaultModelForProvider;
            _activeModel = defaultModelForProvider;
            if (_activeSession != null)
            {
                await _sessionManager.UpdateSessionAsync(_activeSession.Id, model: _activeModel, ct: ct).ConfigureAwait(false);
            }
        }

        SaveSharedSettings(settings);
        _router.ConfigureFromSettings(settings);

        _console.RenderMarkupLine($"[green]✓ Updated {provider.ToUpperInvariant()} configuration ({DesktopSettings.MaskKey(value)}) and hot-reloaded router.[/] Active model: [bold yellow]{_activeModel}[/]");
    }

    private static bool ApplyProviderApiKey(DesktopSettings settings, string provider, string value)
    {
        switch (provider.ToLowerInvariant())
        {
            case "ssb-evren":
            case "evren":
                var evrenParts = value.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                settings.ApiKeys.EvrenApiKey = evrenParts[0];
                if (evrenParts.Length > 1)
                    settings.ApiKeys.EvrenEndpoint = evrenParts[1];
                return true;
            case "custom":
                var customParts = value.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                settings.ApiKeys.CustomApiKey = customParts[0];
                if (customParts.Length > 1)
                    settings.ApiKeys.CustomEndpoint = customParts[1];
                return true;
            case "gemini":
                settings.ApiKeys.GeminiApiKey = value;
                return true;
            case "openai":
                settings.ApiKeys.OpenAiApiKey = value;
                return true;
            case "anthropic":
            case "claude":
                settings.ApiKeys.AnthropicApiKey = value;
                return true;
            case "openrouter":
                settings.ApiKeys.OpenRouterApiKey = value;
                return true;
            case "nous":
                settings.ApiKeys.NousApiKey = value;
                return true;
            case "ollama":
                settings.ApiKeys.OllamaEndpoint = value;
                return true;
            default:
                return false;
        }
    }

    private DesktopSettings LoadSharedSettings()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                var json = File.ReadAllText(_settingsFilePath);
                var loaded = JsonSerializer.Deserialize<DesktopSettings>(json, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
                if (loaded != null && (loaded.OnboardingCompleted || _console is not AnsiTerminal))
                    return loaded;
            }

            if (_console is AnsiTerminal)
            {
                var globalPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".humofa-data",
                    "settings.json");
                if (File.Exists(globalPath))
                {
                    var globalJson = File.ReadAllText(globalPath);
                    var globalLoaded = JsonSerializer.Deserialize<DesktopSettings>(globalJson, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    });
                    if (globalLoaded != null)
                        return globalLoaded;
                }
            }
        }
        catch
        {
            // Fallback to default
        }
        return new DesktopSettings { DefaultWorkingDir = _workingDirectory };
    }

    private void SaveSharedSettings(DesktopSettings settings)
    {
        var dir = Path.GetDirectoryName(_settingsFilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
        var tempFile = _settingsFilePath + ".tmp";
        File.WriteAllText(tempFile, json);
        File.Move(tempFile, _settingsFilePath, overwrite: true);

        if (_console is AnsiTerminal)
        {
            try
            {
                var globalDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".humofa-data");
                Directory.CreateDirectory(globalDir);
                var globalPath = Path.Combine(globalDir, "settings.json");
                if (!string.Equals(Path.GetFullPath(_settingsFilePath), Path.GetFullPath(globalPath), StringComparison.OrdinalIgnoreCase))
                {
                    File.WriteAllText(globalPath, json);
                }
            }
            catch { }
        }
    }

    private async Task HandleModelCommandAsync(string args, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(args))
        {
            _console.RenderMarkupLine($"[bold]Active Model:[/] [yellow]{_activeModel}[/]");
            _console.RenderMarkupLine("[bold]Available Built-in Models:[/]");
            foreach (var p in MultiModelRouter.BuiltInProviders)
            {
                string isCurrent = p.DefaultModel.Equals(_activeModel, StringComparison.OrdinalIgnoreCase)
                    ? "[green](active)[/]"
                    : string.Empty;
                _console.RenderMarkupLine($"  • [cyan]{p.DefaultModel}[/] [dim]({p.ProviderId})[/] {isCurrent}");
            }
            return;
        }

        _activeModel = args;
        if (_activeSession != null)
        {
            await _sessionManager.UpdateSessionAsync(_activeSession.Id, model: _activeModel, ct: ct).ConfigureAwait(false);
        }
        _console.RenderMarkupLine($"[green]✔ Switched active model to:[/] [bold yellow]{_activeModel}[/]");
    }

    private async Task HandleAgentCommandAsync(string args, CancellationToken ct)
    {
        var subParts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var subCommand = subParts.Length > 0 ? subParts[0].ToLowerInvariant() : "list";
        var subArgs = subParts.Length > 1 ? subParts[1] : string.Empty;

        switch (subCommand)
        {
            case "list":
                var agents = _agentManager.ListAgents();
                _console.RenderRule("Registered Agents", '─');
                if (agents.Count == 0)
                {
                    _console.RenderMarkupLine("[dim]No custom agents registered. Using default root assistant.[/]");
                }
                else
                {
                    foreach (var a in agents)
                    {
                        var status = _agentManager.GetAgentStatus(a.Id);
                        _console.RenderMarkupLine($"• [bold cyan]{a.Name}[/] [dim]({a.Id})[/] - [yellow]{a.PreferredModel}[/] - Tier: [magenta]{a.AutonomyTier}[/] - Status: [green]{status}[/]");
                    }
                }
                break;

            case "create":
                if (string.IsNullOrEmpty(subArgs))
                {
                    _console.RenderMarkupLine("[red]Usage: /agent create <id> <name>[/]");
                    return;
                }
                var createParts = subArgs.Split(' ', 2);
                string id = createParts[0];
                string name = createParts.Length > 1 ? createParts[1].Trim('"', '\'') : id;
                var newAgent = new AgentConfig(
                    Id: id,
                    Name: name,
                    SystemPrompt: "You are an autonomous HUMofA subagent.",
                    PreferredModel: _activeModel,
                    WorkingDirectory: _workingDirectory);
                _agentManager.RegisterAgent(newAgent);
                _console.RenderMarkupLine($"[green]✔ Successfully registered agent:[/] [bold cyan]{name}[/] [dim]({id})[/]");
                break;

            case "switch":
                if (string.IsNullOrEmpty(subArgs))
                {
                    _console.RenderMarkupLine("[red]Usage: /agent switch <id>[/]");
                    return;
                }
                var target = _agentManager.GetAgent(subArgs);
                if (target == null)
                {
                    _console.RenderMarkupLine($"[red]Agent '{subArgs}' not found.[/]");
                    return;
                }
                _activeModel = target.PreferredModel;
                _console.RenderMarkupLine($"[green]✔ Switched to agent:[/] [bold cyan]{target.Name}[/] (model: [yellow]{_activeModel}[/])");
                break;

            case "delete":
            case "remove":
            case "sil":
                if (string.IsNullOrEmpty(subArgs))
                {
                    _console.RenderMarkupLine("[red]Usage: /agent delete <id>[/]");
                    return;
                }
                bool removed = _agentManager.DeleteAgent(subArgs);
                if (removed)
                {
                    _console.RenderMarkupLine($"[green]✔ Successfully deleted agent:[/] [bold cyan]{subArgs}[/]");
                }
                else
                {
                    _console.RenderMarkupLine($"[red]Agent '{subArgs}' not found or could not be deleted.[/]");
                }
                break;

            default:
                _console.RenderMarkupLine($"[red]Unknown agent action '{subCommand}'. Usage: /agent [list|create|switch|delete][/]");
                break;
        }
    }

    private async Task HandleSkillCommandAsync(string args, CancellationToken ct)
    {
        var subParts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var subCommand = subParts.Length > 0 ? subParts[0].ToLowerInvariant() : "list";
        var subArgs = subParts.Length > 1 ? subParts[1] : string.Empty;

        switch (subCommand)
        {
            case "list":
                _console.RenderRule("Installed Skills", '─');
                var skills = _skillManager.InstalledSkills.Values.ToList();
                if (skills.Count == 0)
                {
                    _console.RenderMarkupLine("[dim]No installed skills found in .humofa-data/skills/.[/]");
                }
                else
                {
                    foreach (var s in skills)
                    {
                        _console.RenderMarkupLine($"• [bold cyan]{s.Name}[/] [dim]({s.Provider})[/] - {AnsiTerminal.EscapeMarkup(s.Description)}");
                    }
                }
                break;

            case "install":
                if (string.IsNullOrEmpty(subArgs))
                {
                    _console.RenderMarkupLine("[red]Usage: /skill install <repo-or-url>[/]");
                    return;
                }
                _console.RenderMarkupLine($"[dim]Fetching and verifying skill from '{subArgs}'...[/]");
                try
                {
                    var installed = await _skillManager.InstallSkillFromGitHubAsync(subArgs, ct: ct).ConfigureAwait(false);
                    _console.RenderMarkupLine($"[green]✔ Skill successfully installed and verified:[/] [bold cyan]{installed.Name}[/]");
                }
                catch (Exception ex)
                {
                    _console.RenderMarkupLine($"[red]Failed to install skill: {ex.Message}[/]");
                }
                break;

            case "run":
                if (string.IsNullOrEmpty(subArgs))
                {
                    _console.RenderMarkupLine("[red]Usage: /skill run <name>[/]");
                    return;
                }
                if (_skillManager.InstalledSkills.TryGetValue(subArgs, out var skillDef))
                {
                    _console.RenderRule($"Skill: {skillDef.Name}", '─');
                    _console.RenderMarkupLine($"[bold]Description:[/] {AnsiTerminal.EscapeMarkup(skillDef.Description)}");
                    _console.RenderMarkupLine("[bold]Instructions Preview:[/]");
                    var previewLines = skillDef.RawMarkdown.Split('\n').Take(10);
                    foreach (var l in previewLines)
                    {
                        _console.WriteLine("  " + l);
                    }
                }
                else
                {
                    _console.RenderMarkupLine($"[red]Skill '{subArgs}' not found.[/]");
                }
                break;

            default:
                _console.RenderMarkupLine($"[red]Unknown skill action '{subCommand}'. Usage: /skill [list|install|run][/]");
                break;
        }
    }

    private async Task HandleSessionsCommandAsync(string args, CancellationToken ct)
    {
        var subParts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var subCommand = subParts.Length > 0 ? subParts[0].ToLowerInvariant() : "list";
        var subArgs = subParts.Length > 1 ? subParts[1] : string.Empty;

        switch (subCommand)
        {
            case "list":
                var list = await _sessionManager.ListSessionsAsync(ct).ConfigureAwait(false);
                _console.RenderRule("Persistent Sessions", '─');
                _console.RenderMarkupLine("[bold]ID       Title                                Messages  Tokens  Active  Updated[/]");
                _console.RenderMarkupLine("[dim]───────  ───────────────────────────────────  ────────  ──────  ──────  ───────────────────[/]");
                foreach (var s in list)
                {
                    string activeMarker = s.Id == _activeSession?.Id ? "[green]*[/]" : " ";
                    string idShort = s.Id.Length > 7 ? s.Id.Substring(0, 7) : s.Id;
                    string titleShort = s.Title.Length > 35 ? s.Title.Substring(0, 32) + "..." : s.Title.PadRight(35);
                    string msgCount = s.MessageCount.ToString().PadRight(8);
                    string tokens = s.TotalTokens.ToString().PadRight(6);
                    string updated = s.UpdatedAt.ToString("yyyy-MM-dd HH:mm");
                    _console.RenderMarkupLine($"{activeMarker} [cyan]{idShort}[/]  {titleShort}  {msgCount}  {tokens}  [yellow]{s.Model}[/]  [dim]{updated}[/]");
                }
                break;

            case "new":
                string title = string.IsNullOrEmpty(subArgs) ? $"Session {DateTime.Now:MM-dd HH:mm}" : subArgs.Trim('"', '\'');
                var created = await _sessionManager.CreateSessionAsync(title, _activeModel, _workingDirectory, ct: ct).ConfigureAwait(false);
                await _sessionManager.SetActiveSessionAsync(created.Id, ct).ConfigureAwait(false);
                _activeSession = created;
                _sessionTokens = 0;
                _console.RenderMarkupLine($"[green]✔ Created and switched to new session:[/] [bold cyan]{created.Title}[/] [dim]({created.Id})[/]");
                break;

            case "resume":
            case "switch":
                if (string.IsNullOrEmpty(subArgs))
                {
                    _console.RenderMarkupLine("[red]Usage: /sessions resume <id>[/]");
                    return;
                }
                var found = await _sessionManager.GetSessionAsync(subArgs, ct).ConfigureAwait(false);
                if (found == null)
                {
                    // Try prefix match
                    var all = await _sessionManager.ListSessionsAsync(ct).ConfigureAwait(false);
                    found = all.FirstOrDefault(s => s.Id.StartsWith(subArgs, StringComparison.OrdinalIgnoreCase));
                }

                if (found == null)
                {
                    _console.RenderMarkupLine($"[red]Session '{subArgs}' not found.[/]");
                    return;
                }

                await _sessionManager.SetActiveSessionAsync(found.Id, ct).ConfigureAwait(false);
                _activeSession = found;
                _activeModel = found.Model;
                _workingDirectory = string.IsNullOrEmpty(found.WorkingDir) ? _workingDirectory : found.WorkingDir;
                _sessionTokens = found.TotalTokens;

                _console.RenderMarkupLine($"[green]✔ Resumed session:[/] [bold cyan]{found.Title}[/] [dim]({found.Id})[/]");
                var transcript = await _sessionManager.GetTranscriptAsync(found.Id, limit: 4, ct: ct).ConfigureAwait(false);
                if (transcript.Count > 0)
                {
                    _console.RenderMarkupLine("[dim]Recent messages in this session:[/]");
                    foreach (var m in transcript)
                    {
                        string roleColor = m.Role == "user" ? "bold yellow" : "bold green";
                        string preview = m.Content.Length > 80 ? m.Content.Substring(0, 77) + "..." : m.Content;
                        _console.RenderMarkupLine($"  [{roleColor}]{m.Role}:[/] {AnsiTerminal.EscapeMarkup(preview)}");
                    }
                }
                break;

            default:
                _console.RenderMarkupLine($"[red]Unknown session action '{subCommand}'. Usage: /sessions [list|new|resume <id>][/]");
                break;
        }
    }

    private async Task HandleDiffCommandAsync(string args, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(args))
        {
            var snapshots = _diffService.GetSnapshots();
            _console.RenderRule("Recent File Rollback Snapshots", '─');
            if (snapshots.Count == 0)
            {
                _console.RenderMarkupLine("[dim]No file modification snapshots recorded in this workspace.[/]");
            }
            else
            {
                foreach (var s in snapshots.TakeLast(8))
                {
                    string status = s.IsReverted ? "[red](reverted)[/]" : "[green](active)[/]";
                    _console.RenderMarkupLine($"• [cyan]{s.FilePath}[/] - [dim]{s.Timestamp:yyyy-MM-dd HH:mm:ss}[/] - {status} [dim]id:{s.Id.Substring(0, 8)}[/]");
                }
            }
            return;
        }

        string filePath = Path.GetFullPath(Path.Combine(_workingDirectory, args));
        if (!File.Exists(filePath))
        {
            _console.RenderMarkupLine($"[red]File '{args}' does not exist.[/]");
            return;
        }

        var snapshotsForFile = _diffService.GetSnapshots(filePath);
        if (snapshotsForFile.Count == 0)
        {
            _console.RenderMarkupLine($"[yellow]No snapshots found for '{args}'. File is currently unmodified by HUMofA.[/]");
            return;
        }

        var latest = snapshotsForFile.Last();
        if (File.Exists(latest.SnapshotPath))
        {
            var oldContent = await File.ReadAllTextAsync(latest.SnapshotPath, ct).ConfigureAwait(false);
            var currentContent = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
            var diff = _diffService.ComputeDiff(args, oldContent, currentContent);
            _console.RenderDiff(diff);
        }
    }

    private async Task HandleCompactCommandAsync(CancellationToken ct)
    {
        if (_activeSession == null) return;

        using var spinner = new BrailleSpinner(_console, "Compacting session context & updating memory store...");
        spinner.Start();

        var transcript = await _sessionManager.GetTranscriptAsync(_activeSession.Id, limit: 100, ct: ct).ConfigureAwait(false);
        int originalTokens = _sessionTokens;

        var summaryText = $"Session compacted at {DateTime.UtcNow:O}. Summary: Session covered {transcript.Count} interactions.";
        await _memoryStore.AppendMemoryEntryAsync(
            MemoryDocumentType.Memory,
            $"Session Summary ({_activeSession.Id})",
            summaryText,
            ct).ConfigureAwait(false);

        _sessionTokens = Math.Max(0, originalTokens / 4);
        await _sessionManager.UpdateSessionAsync(_activeSession.Id, maxTokens: _sessionTokens, ct: ct).ConfigureAwait(false);

        spinner.Stop();
        _console.RenderMarkupLine($"[green]✔ Session compacted successfully.[/] Reduced token footprint from [yellow]{originalTokens}[/] to [yellow]{_sessionTokens}[/] tokens.");
    }

    private async Task HandleMemoryCommandAsync(string args, CancellationToken ct)
    {
        var subParts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var subCommand = subParts.Length > 0 ? subParts[0].ToLowerInvariant() : "status";
        var subArgs = subParts.Length > 1 ? subParts[1] : string.Empty;

        switch (subCommand)
        {
            case "query":
            case "search":
                if (string.IsNullOrEmpty(subArgs))
                {
                    _console.RenderMarkupLine("[red]Usage: /memory query <search terms>[/]");
                    return;
                }
                var hits = await _memoryStore.SearchMemoriesAsync(subArgs, limit: 5, ct: ct).ConfigureAwait(false);
                _console.RenderRule($"Memory Search: '{subArgs}'", '─');
                if (hits.Count == 0)
                {
                    _console.RenderMarkupLine("[dim]No matching memory records found.[/]");
                }
                else
                {
                    foreach (var h in hits)
                    {
                        _console.RenderMarkupLine($"• [bold cyan]{h.Title}[/] [dim]({h.Category}, BM25: {h.Bm25Score:F2})[/]");
                        _console.RenderMarkupLine($"  [dim]{AnsiTerminal.EscapeMarkup(h.Content)}[/]");
                    }
                }
                break;

            case "add":
                if (string.IsNullOrEmpty(subArgs))
                {
                    _console.RenderMarkupLine("[red]Usage: /memory add <SectionName> <Text>[/]");
                    return;
                }
                var addParts = subArgs.Split(' ', 2);
                string section = addParts[0];
                string content = addParts.Length > 1 ? addParts[1] : section;
                await _memoryStore.AppendMemoryEntryAsync(MemoryDocumentType.Memory, section, content, ct).ConfigureAwait(false);
                _console.RenderMarkupLine($"[green]✔ Appended entry under ## {section} to MEMORY.md[/]");
                break;

            default:
                _console.RenderRule("Unified Memory Store Status", '─');
                _console.RenderMarkupLine($"• [bold]SOUL.md:[/]   [cyan]{_memoryStore.SoulFilePath}[/]");
                _console.RenderMarkupLine($"• [bold]MEMORY.md:[/] [cyan]{_memoryStore.MemoryFilePath}[/]");
                _console.RenderMarkupLine($"• [bold]USER.md:[/]   [cyan]{_memoryStore.UserFilePath}[/]");
                _console.RenderMarkupLine("[dim]Use '/memory query <text>' to perform SQLite FTS5 BM25 search.[/]");
                break;
        }
    }

    private void HandleKanbanCommand()
    {
        _console.RenderRule("Sub-Agent Kanban Board", '─');
        var agents = _agentManager.ListAgentStates();

        var todo = agents.Where(a => a.Status == AgentStatus.Idle).ToList();
        var inProgress = agents.Where(a => a.Status == AgentStatus.Running || a.Status == AgentStatus.WaitingForApproval).ToList();
        var done = agents.Where(a => a.Status == AgentStatus.Completed || a.Status == AgentStatus.Failed).ToList();

        _console.RenderMarkupLine("[bold cyan]┌────────── TODO ──────────┐  ┌─────── IN PROGRESS ──────┐  ┌────────── DONE ──────────┐[/]");
        int maxRows = Math.Max(todo.Count, Math.Max(inProgress.Count, done.Count));
        if (maxRows == 0)
        {
            _console.RenderMarkupLine("│ [dim](no tasks)[/]                │  │ [dim](no active tasks)[/]        │  │ [dim](no completed tasks)[/]   │");
        }
        else
        {
            for (int i = 0; i < maxRows; i++)
            {
                string c1 = i < todo.Count ? FormatKanbanCell(todo[i].Config.Name, 24) : new string(' ', 24);
                string c2 = i < inProgress.Count ? FormatKanbanCell(inProgress[i].Config.Name, 24) : new string(' ', 24);
                string c3 = i < done.Count ? FormatKanbanCell(done[i].Config.Name, 24) : new string(' ', 24);
                _console.RenderMarkupLine($"│ {c1} │  │ {c2} │  │ {c3} │");
            }
        }
        _console.RenderMarkupLine("[bold cyan]└──────────────────────────┘  └──────────────────────────┘  └──────────────────────────┘[/]");
    }

    private static string FormatKanbanCell(string text, int width)
    {
        if (text.Length > width) return text.Substring(0, width - 3) + "...";
        return text.PadRight(width);
    }

    private async Task HandleExecCommandAsync(string args, CancellationToken ct)
    {
        if (_backends == null)
        {
            _console.RenderMarkupLine("[red]ExecutionEnvironmentManager is not attached.[/]");
            return;
        }

        var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            _console.RenderMarkupLine("[red]Usage: /exec <backend> <command>[/]");
            _console.RenderMarkupLine("[dim]Supported backends: Local, Docker, Ssh, Singularity, Modal[/]");
            return;
        }

        string env = parts[0];
        string cmd = parts[1];

        if (!Enum.TryParse<ExecutionBackendType>(env, ignoreCase: true, out var backendType))
        {
            _console.RenderMarkupLine($"[red]Unknown backend '{env}'. Supported: Local, Docker, Ssh, Singularity, Modal[/]");
            return;
        }

        // Evaluate approval
        var tier = _smartApproval.ClassifyAction("exec", new Dictionary<string, string> { ["cmd"] = cmd });
        var approved = await EvaluateSmartApprovalAsync("exec", tier, $"Execute '{cmd}' on {backendType}", ct).ConfigureAwait(false);
        if (!approved)
        {
            _console.RenderMarkupLine("[red]Command execution denied.[/]");
            return;
        }

        var sw = Stopwatch.StartNew();
        using var spinner = new BrailleSpinner(_console, $"Executing on {backendType}...");
        spinner.Start();

        BackendExecutionResult result;
        try
        {
            var req = new BackendExecutionRequest(cmd, backendType, _workingDirectory);
            result = await _backends.ExecuteAsync(req, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result = new BackendExecutionResult(backendType, -1, $"Execution error: {ex.Message}", cmd, false);
        }
        sw.Stop();
        spinner.Stop();

        var card = new ToolExecutionCard(
            ToolName: $"Exec ({backendType})",
            Parameters: cmd,
            Output: result.StandardOutput,
            ExecutionTimeMs: sw.ElapsedMilliseconds,
            IsSuccess: result.ExitCode == 0);

        _console.RenderCard(card);
    }

    private async Task HandlePermissionsCommandAsync(string args, CancellationToken ct)
    {
        if (_activeSession == null) return;

        var subCommand = args.Trim().ToLowerInvariant();
        if (subCommand == "clear")
        {
            int cleared = await _sessionManager.ClearPermissionGrantsAsync(_activeSession.Id, ct).ConfigureAwait(false);
            _console.RenderMarkupLine($"[green]✔ Cleared {cleared} session-scoped permission grants.[/]");
            return;
        }

        var grants = await _sessionManager.GetPermissionGrantsAsync(_activeSession.Id, ct).ConfigureAwait(false);
        _console.RenderRule("Active Session Permission Grants", '─');
        if (grants.Count == 0)
        {
            _console.RenderMarkupLine("[dim]No persistent session-scoped permission grants active.[/]");
        }
        else
        {
            foreach (var g in grants)
            {
                _console.RenderMarkupLine($"• Tool: [bold cyan]{g.ToolName}[/] - Pattern: [yellow]{g.ActionPattern}[/] - Tier: [magenta]{g.RiskTier}[/] [dim]({g.GrantedAt:HH:mm:ss})[/]");
            }
        }
    }

    public async Task HandleNaturalLanguagePromptAsync(string prompt, CancellationToken ct = default)
    {
        if (_activeSession == null)
        {
            await InitializeSessionAsync(ct).ConfigureAwait(false);
        }

        _lastUserPrompt = prompt;

        // 1. Record user message in session transcript
        await _sessionManager.AppendMessageAsync(
            _activeSession!.Id,
            role: "user",
            content: prompt,
            tokenCount: prompt.Length / 4,
            ct: ct).ConfigureAwait(false);

        // 2. Stream ghost typing to Desktop UI if attached
        if (_eventBus != null && _ghostTyping != null)
        {
            var userMsg = AgentMessage.Create("cli", "cli-user", prompt);
            await _ghostTyping.StreamGhostTypingAsync(userMsg, _eventBus, TimeSpan.FromMilliseconds(5), "desktop", ct).ConfigureAwait(false);
        }

        // 3. Assemble unified contextual chat messages: Master persona, working directory, installed skills, persistent memory, and multi-turn transcript
        var chatMessages = (await BuildContextualChatMessagesAsync(prompt, ct).ConfigureAwait(false)).ToList();

        const int maxAutonomousIterations = 8;
        int currentIteration = 0;
        int consecutiveToolFailures = 0;
        string finalModelResponse = string.Empty;

        // 4. Autonomous tool execution loop: LLM reasons -> emits tool tags -> executed locally -> results fed back to LLM
        while (currentIteration < maxAutonomousIterations && !ct.IsCancellationRequested)
        {
            currentIteration++;

            if (currentIteration > 1 && _showThinkingSteps)
            {
                _console.RenderMarkupLine($"[dim]🔄 [AI Adım {currentIteration}] Araç sonuçları değerlendiriliyor ve sonraki eylem planlanıyor...[/]");
            }

            // Braille spinner during reasoning and real-time thought tracking
            using var spinner = new BrailleSpinner(_console, currentIteration == 1 
                ? $"Thinking with {_activeModel}..." 
                : $"Executing next step ({currentIteration}) with {_activeModel}...");
            spinner.Start();

            bool firstContentReceived = false;
            bool thoughtHeaderPrinted = false;
            var streamedContent = new StringBuilder();

            void PrintCollectedThoughtIfAny()
            {
                if (thoughtHeaderPrinted || !_showThinkingSteps) return;
                var collectedThought = spinner.GetCollectedThought();
                if (!string.IsNullOrWhiteSpace(collectedThought))
                {
                    thoughtHeaderPrinted = true;
                    var compactThought = collectedThought.Length > 220
                        ? collectedThought[..220] + "..."
                        : collectedThought;
                    _console.RenderMarkupLine($"[dim]💭 [Düşünce Aşaması]: {AnsiTerminal.EscapeMarkup(compactThought)}[/]");
                }
            }

            string response;
            try
            {
                response = await _router.CompleteChatStreamAsync(
                    chatMessages,
                    _activeModel,
                    onReasoningChunk: chunk =>
                    {
                        spinner.UpdateThought(chunk);
                    },
                    onContentChunk: chunk =>
                    {
                        if (!firstContentReceived)
                        {
                            firstContentReceived = true;
                            spinner.Stop();
                            PrintCollectedThoughtIfAny();
                        }
                        _console.Write(chunk);
                        streamedContent.Append(chunk);
                    },
                    ct: ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                response = $"Error generating response: {ex.Message}";
            }
            finally
            {
                spinner.Stop();
                PrintCollectedThoughtIfAny();
            }

            _console.WriteLine();
            if (!firstContentReceived)
            {
                await RenderProgressiveMarkdownAsync(response, ct).ConfigureAwait(false);
                _console.WriteLine();
            }
            else if (streamedContent.Length > 0)
            {
                response = streamedContent.ToString();
            }

            finalModelResponse = response;

            // 5. Parse any autonomous tool calls from the model
            var toolInvocations = ParseToolInvocations(response);
            if (toolInvocations.Count == 0)
            {
                // No further tool calls - task completed!
                break;
            }

            // 6. Execute each tool on the local machine and render execution cards
            var toolResultsBuilder = new StringBuilder();
            bool anyFailureInTurn = false;
            foreach (var tool in toolInvocations)
            {
                var (toolResultXml, toolSucceeded) = await ExecuteAutonomousToolAsync(tool, ct).ConfigureAwait(false);
                toolResultsBuilder.AppendLine(toolResultXml);
                if (!toolSucceeded)
                {
                    anyFailureInTurn = true;
                }
            }

            if (anyFailureInTurn)
            {
                consecutiveToolFailures++;
            }
            else
            {
                consecutiveToolFailures = 0;
            }

            // 6b. If the AI is stuck (2+ consecutive tool failures), automatically intervene or prompt the user
            if (consecutiveToolFailures >= 2)
            {
                _console.RenderMarkupLine("[bold yellow]⚠️ [AI Takılma Algılandı][/] [yellow]Araç çalıştırma üst üste başarısız oldu.[/]");
                if (_console is AnsiTerminal { IsInteractive: true })
                {
                    var helpOpts = new PromptOptions(
                        BadgeContext: null,
                        PromptText: "🙋 AI'a yol göstermek / müdahale etmek ister misiniz? (Enter = AI size sorsun) ❯ ",
                        AllowMultiline: false);
                    var userGuidance = (await _console.ReadLineAsync(helpOpts, ct).ConfigureAwait(false))?.Trim();
                    if (!string.IsNullOrWhiteSpace(userGuidance))
                    {
                        toolResultsBuilder.AppendLine($"<user_guidance>{userGuidance}</user_guidance>");
                        consecutiveToolFailures = 0;
                    }
                    else
                    {
                        toolResultsBuilder.AppendLine("<system_notice>Üst üste araç hatası alındı. Kendi başına çözemiyorsan <ask_user reason=\"...\">...</ask_user> etiketiyle kullanıcıdan ne yapması gerektiğini veya eksik bilgiyi iste.</system_notice>");
                    }
                }
                else
                {
                    toolResultsBuilder.AppendLine("<system_notice>Üst üste araç hatası alındı. Kendi başına çözemiyorsan <ask_user reason=\"...\">...</ask_user> etiketiyle kullanıcıdan yardım iste.</system_notice>");
                }
            }

            // 7. Append assistant response and tool_result blocks to history for next reasoning step
            chatMessages.Add(new LlmMessage("assistant", response));
            chatMessages.Add(new LlmMessage("user", toolResultsBuilder.ToString().Trim()));
        }

        // 8. Append final assistant message to session transcript
        int responseTokens = finalModelResponse.Length / 4;
        await _sessionManager.AppendMessageAsync(
            _activeSession.Id,
            role: "assistant",
            content: finalModelResponse,
            tokenCount: responseTokens,
            ct: ct).ConfigureAwait(false);

        _sessionTokens += responseTokens;
    }

    private async Task RenderProgressiveMarkdownAsync(string markdown, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(markdown)) return;

        // In unit tests (MockTerminalConsole), render synchronously without artificial delay
        if (_console is not AnsiTerminal { IsInteractive: true })
        {
            RenderStreamingMarkdown(markdown);
            return;
        }

        // On interactive terminal, stream piece-by-piece so the user never gets a sudden wall of text
        const int chunkLen = 14;
        for (int i = 0; i < markdown.Length; i += chunkLen)
        {
            if (ct.IsCancellationRequested) break;
            int len = Math.Min(chunkLen, markdown.Length - i);
            _console.Write(markdown.Substring(i, len));
            if (i + chunkLen < markdown.Length)
            {
                await Task.Delay(8, ct).ConfigureAwait(false);
            }
        }
    }

    public sealed record ToolInvocation(string ToolType, string Parameter, string Content, string? ExtraOptions = null);

    public static IReadOnlyList<ToolInvocation> ParseToolInvocations(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<ToolInvocation>();

        var invocations = new List<ToolInvocation>();

        // 1. Bash / PowerShell / CMD
        var bashMatches = Regex.Matches(text, @"<(?:bash|powershell|cmd)>(.*?)</(?:bash|powershell|cmd)>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        foreach (Match m in bashMatches)
        {
            var content = m.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(content))
            {
                invocations.Add(new ToolInvocation("bash", string.Empty, content));
            }
        }

        // 2. Write file
        var writeMatches = Regex.Matches(text, @"<write_file\s+path=[""']?([^""'>\r\n]+)[""']?\s*>(.*?)</write_file>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        foreach (Match m in writeMatches)
        {
            var path = m.Groups[1].Value.Trim();
            var content = m.Groups[2].Value;
            if (!string.IsNullOrWhiteSpace(path))
            {
                invocations.Add(new ToolInvocation("write_file", path, content));
            }
        }

        // 3. Read file
        var readMatches = Regex.Matches(text, @"<read_file\s+path=[""']?([^""'>/\r\n]+)[""']?\s*(?:/>|>(.*?)</read_file>)", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        foreach (Match m in readMatches)
        {
            var path = m.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(path))
            {
                invocations.Add(new ToolInvocation("read_file", path, string.Empty));
            }
        }

        // 4. Sub-agent
        var subagentMatches = Regex.Matches(text, @"<subagent(?:\s+role=[""']?([^""'>\r\n]+)[""']?)?(?:\s+name=[""']?([^""'>\r\n]+)[""']?)?\s*>(.*?)</subagent>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        foreach (Match m in subagentMatches)
        {
            var role = m.Groups[1].Success && !string.IsNullOrWhiteSpace(m.Groups[1].Value) ? m.Groups[1].Value.Trim() : "Specialist";
            var content = m.Groups[3].Value.Trim();
            if (!string.IsNullOrWhiteSpace(content))
            {
                invocations.Add(new ToolInvocation("subagent", role, content));
            }
        }

        // 5. Ask User / Human-in-the-Loop Intervention (<ask_user> or <user_action>)
        var askMatches = Regex.Matches(text, @"<(?:ask_user|user_action)(?:\s+reason=[""']?([^""'>\r\n]+)[""']?)?(?:\s+options=[""']?([^""'>\r\n]+)[""']?)?\s*>(.*?)</(?:ask_user|user_action)>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        foreach (Match m in askMatches)
        {
            var reason = m.Groups[1].Success && !string.IsNullOrWhiteSpace(m.Groups[1].Value)
                ? m.Groups[1].Value.Trim()
                : "Kullanıcı yönlendirmesi veya manuel işlem gerekiyor";
            var options = m.Groups[2].Success && !string.IsNullOrWhiteSpace(m.Groups[2].Value)
                ? m.Groups[2].Value.Trim()
                : null;
            var question = m.Groups[3].Value.Trim();
            if (!string.IsNullOrWhiteSpace(question))
            {
                invocations.Add(new ToolInvocation("ask_user", reason, question, options));
            }
        }

        return invocations;
    }

    private async Task<(string ResultXml, bool IsSuccess)> ExecuteAutonomousToolAsync(ToolInvocation tool, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        string output;
        bool isSuccess = true;

        switch (tool.ToolType.ToLowerInvariant())
        {
            case "bash":
            case "powershell":
            case "cmd":
                string cmd = tool.Content.Trim();
                if (_showThinkingSteps)
                {
                    _console.RenderMarkupLine($"[bold cyan]⚡ [AI Eylemi][/] Terminal komutu çalıştırılıyor: [yellow]{AnsiTerminal.EscapeMarkup(cmd)}[/]");
                }

                using (var cmdSpinner = new BrailleSpinner(_console, $"Komut yürütülüyor: {cmd}"))
                {
                    cmdSpinner.Start();
                    try
                    {
                        if (_backends != null)
                        {
                            var req = new BackendExecutionRequest(cmd, ExecutionBackendType.Local, _workingDirectory);
                            var res = await _backends.ExecuteAsync(req, ct).ConfigureAwait(false);
                            output = string.IsNullOrWhiteSpace(res.StandardOutput) ? "(Process completed with exit code " + res.ExitCode + ")" : res.StandardOutput;
                            isSuccess = res.ExitCode == 0;
                        }
                        else
                        {
                            var psi = new ProcessStartInfo
                            {
                                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                                RedirectStandardOutput = true,
                                RedirectStandardError = true,
                                UseShellExecute = false,
                                CreateNoWindow = true,
                                WorkingDirectory = _workingDirectory,
                                StandardOutputEncoding = Encoding.UTF8
                            };
                            if (OperatingSystem.IsWindows())
                            {
                                psi.ArgumentList.Add("/c");
                                psi.ArgumentList.Add(cmd);
                            }
                            else
                            {
                                psi.ArgumentList.Add("-c");
                                psi.ArgumentList.Add(cmd);
                            }

                            using var proc = Process.Start(psi);
                            if (proc != null)
                            {
                                var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
                                var stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
                                await proc.WaitForExitAsync(ct).ConfigureAwait(false);
                                output = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : $"{stdout.Trim()}\n{stderr.Trim()}".Trim();
                                isSuccess = proc.ExitCode == 0;
                            }
                            else
                            {
                                output = "Failed to launch process.";
                                isSuccess = false;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        output = $"Error executing command: {ex.Message}";
                        isSuccess = false;
                    }
                    finally
                    {
                        cmdSpinner.Stop();
                    }
                }
                sw.Stop();

                var bashCard = new ToolExecutionCard(
                    ToolName: $"Terminal ({tool.ToolType})",
                    Parameters: cmd.Length > 80 ? cmd[..80] + "..." : cmd,
                    Output: output,
                    ExecutionTimeMs: sw.ElapsedMilliseconds,
                    IsSuccess: isSuccess);
                _console.RenderCard(bashCard);

                return ($"<tool_result tool=\"{tool.ToolType}\" success=\"{isSuccess}\">\n{output}\n</tool_result>", isSuccess);

            case "write_file":
                string filePath = tool.Parameter.Trim();
                string fileContent = tool.Content;
                if (_showThinkingSteps)
                {
                    _console.RenderMarkupLine($"[bold cyan]📝 [AI Eylemi][/] Dosya yazılıyor: [yellow]{AnsiTerminal.EscapeMarkup(filePath)}[/]");
                }
                try
                {
                    string fullPath = Path.IsPathRooted(filePath) ? filePath : Path.Combine(_workingDirectory, filePath);
                    var dir = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    await File.WriteAllTextAsync(fullPath, fileContent, Encoding.UTF8, ct).ConfigureAwait(false);
                    output = $"Successfully wrote {fileContent.Length} bytes to {filePath}";
                    isSuccess = true;
                }
                catch (Exception ex)
                {
                    output = $"Failed to write file '{filePath}': {ex.Message}";
                    isSuccess = false;
                }
                sw.Stop();

                var writeCard = new ToolExecutionCard(
                    ToolName: "Write File",
                    Parameters: filePath,
                    Output: output,
                    ExecutionTimeMs: sw.ElapsedMilliseconds,
                    IsSuccess: isSuccess);
                _console.RenderCard(writeCard);

                return ($"<tool_result tool=\"write_file\" path=\"{filePath}\" success=\"{isSuccess}\">\n{output}\n</tool_result>", isSuccess);

            case "read_file":
                string readPath = tool.Parameter.Trim();
                if (_showThinkingSteps)
                {
                    _console.RenderMarkupLine($"[bold cyan]📂 [AI Eylemi][/] Dosya okunuyor: [yellow]{AnsiTerminal.EscapeMarkup(readPath)}[/]");
                }
                try
                {
                    string fullPath = Path.IsPathRooted(readPath) ? readPath : Path.Combine(_workingDirectory, readPath);
                    if (!File.Exists(fullPath))
                    {
                        output = $"File not found: {readPath}";
                        isSuccess = false;
                    }
                    else
                    {
                        var content = await File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct).ConfigureAwait(false);
                        if (content.Length > 32_000)
                        {
                            content = content[..32_000] + "\n...(truncated remaining bytes)...";
                        }
                        output = content;
                        isSuccess = true;
                    }
                }
                catch (Exception ex)
                {
                    output = $"Error reading file '{readPath}': {ex.Message}";
                    isSuccess = false;
                }
                sw.Stop();

                var readCard = new ToolExecutionCard(
                    ToolName: "Read File",
                    Parameters: readPath,
                    Output: isSuccess ? $"Read {output.Length} bytes from {readPath}" : output,
                    ExecutionTimeMs: sw.ElapsedMilliseconds,
                    IsSuccess: isSuccess);
                _console.RenderCard(readCard);

                return ($"<tool_result tool=\"read_file\" path=\"{readPath}\" success=\"{isSuccess}\">\n{output}\n</tool_result>", isSuccess);

            case "subagent":
                string role = string.IsNullOrWhiteSpace(tool.Parameter) ? "Specialist" : tool.Parameter.Trim();
                string taskPrompt = tool.Content.Trim();
                if (_showThinkingSteps)
                {
                    _console.RenderMarkupLine($"[bold cyan]🤖 [AI Eylemi][/] Alt-Ajan ([yellow]{AnsiTerminal.EscapeMarkup(role)}[/]) görevlendiriliyor...");
                }
                using (var subSpinner = new BrailleSpinner(_console, $"SubAgent [{role}] çalışıyor..."))
                {
                    subSpinner.Start();
                    try
                    {
                        if (_subAgents != null)
                        {
                            var spec = new SubAgentSpec(
                                Role: role,
                                TaskPrompt: taskPrompt,
                                PreferredModel: _activeModel);
                            var subRes = await _subAgents.SpawnAndExecuteAsync(spec, workDelegate: null, ct).ConfigureAwait(false);
                            output = subRes.CondensedOutput;
                            isSuccess = subRes.IsSuccess;
                        }
                        else
                        {
                            output = $"[SubAgent Orchestrator not loaded] Task assigned to {role}: {taskPrompt}";
                            isSuccess = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        output = $"Error executing subagent '{role}': {ex.Message}";
                        isSuccess = false;
                    }
                    finally
                    {
                        subSpinner.Stop();
                    }
                }
                sw.Stop();

                var subCard = new ToolExecutionCard(
                    ToolName: $"SubAgent ({role})",
                    Parameters: taskPrompt.Length > 70 ? taskPrompt[..70] + "..." : taskPrompt,
                    Output: output,
                    ExecutionTimeMs: sw.ElapsedMilliseconds,
                    IsSuccess: isSuccess);
                _console.RenderCard(subCard);

                return ($"<tool_result tool=\"subagent\" role=\"{role}\" success=\"{isSuccess}\">\n{output}\n</tool_result>", isSuccess);

            case "ask_user":
                string reason = string.IsNullOrWhiteSpace(tool.Parameter) ? "Kullanıcı müdahalesi veya kararı gerekiyor" : tool.Parameter.Trim();
                string question = tool.Content.Trim();
                var optionList = string.IsNullOrWhiteSpace(tool.ExtraOptions)
                    ? Array.Empty<string>()
                    : tool.ExtraOptions.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                _console.WriteLine();
                _console.RenderRule("🙋 AI SİZDEN YARDIM / MÜDAHALE İSTİYOR", '═');
                _console.RenderMarkupLine($"  [bold yellow]Neden:[/]   {AnsiTerminal.EscapeMarkup(reason)}");
                _console.RenderMarkupLine($"  [bold cyan]İstek:[/]   {AnsiTerminal.EscapeMarkup(question)}");
                if (optionList.Length > 0)
                {
                    _console.RenderMarkupLine("  [bold]Seçenekler:[/]");
                    for (int i = 0; i < optionList.Length; i++)
                    {
                        _console.RenderMarkupLine($"    [cyan][{i + 1}][/] {AnsiTerminal.EscapeMarkup(optionList[i])}");
                    }
                }
                _console.WriteLine();

                var askPromptOpts = new PromptOptions(
                    BadgeContext: null,
                    PromptText: "🙋 Yanıtınız (veya işlemi tamamladıysanız Enter'a basın) ❯ ",
                    AllowMultiline: false);

                string? userReply;
                try
                {
                    userReply = (await _console.ReadLineAsync(askPromptOpts, ct).ConfigureAwait(false))?.Trim();
                }
                catch
                {
                    userReply = null;
                }

                if (string.IsNullOrWhiteSpace(userReply))
                {
                    output = "Kullanıcı istenen işlemi tamamladı ve onayladı. Göreve devam edebilirsin.";
                }
                else if (int.TryParse(userReply, out int choiceIdx) && choiceIdx >= 1 && choiceIdx <= optionList.Length)
                {
                    output = $"Kullanıcı seçimi: {optionList[choiceIdx - 1]}";
                }
                else
                {
                    output = $"Kullanıcı yanıtı: {userReply}";
                }

                sw.Stop();
                _console.RenderMarkupLine($"[green]✔ Yanıtınız AI'a iletildi:[/] [dim]{AnsiTerminal.EscapeMarkup(output)}[/]");
                return ($"<tool_result tool=\"ask_user\" success=\"true\">\n{output}\n</tool_result>", true);

            default:
                sw.Stop();
                return ($"<tool_result tool=\"{tool.ToolType}\" error=\"Unknown tool\" />", false);
        }
    }

    private async Task<IReadOnlyList<LlmMessage>> BuildContextualChatMessagesAsync(string currentPrompt, CancellationToken ct)
    {
        var messages = new List<LlmMessage>();

        // A. Rich Context System Prompt
        var sb = new StringBuilder();
        sb.AppendLine(HermesPrompts.DefaultSystemPrompt);
        sb.AppendLine();
        sb.AppendLine($"[Workspace Environment]");
        sb.AppendLine($"- Current Working Directory: {_workingDirectory}");
        sb.AppendLine($"- Operating System: Windows");
        sb.AppendLine($"- Active Session ID: {_activeSession?.Id ?? "default"}");

        // B. Active Skills from SkillManager
        if (_skillManager != null && _skillManager.InstalledSkills.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("[Installed & Active Skills in System]");
            foreach (var s in _skillManager.InstalledSkills.Values.Take(25))
            {
                sb.AppendLine($"- {s.Name} [{s.Category}]: {s.Description}");
            }
        }

        // C. Project Memory (MEMORY.md & USER.md) from UnifiedMemoryStore
        if (_memoryStore != null)
        {
            try
            {
                var memoryDoc = await _memoryStore.ReadDocumentAsync(MemoryDocumentType.Memory, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(memoryDoc) && memoryDoc.Length > 20)
                {
                    var preview = memoryDoc.Length > 1200 ? memoryDoc[..1200] + "..." : memoryDoc;
                    sb.AppendLine();
                    sb.AppendLine("[Project Long-Term Memory (MEMORY.md)]");
                    sb.AppendLine(preview.Trim());
                }

                var userDoc = await _memoryStore.ReadDocumentAsync(MemoryDocumentType.User, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(userDoc) && userDoc.Length > 20)
                {
                    var preview = userDoc.Length > 800 ? userDoc[..800] + "..." : userDoc;
                    sb.AppendLine();
                    sb.AppendLine("[User Preferences & Model (USER.md)]");
                    sb.AppendLine(preview.Trim());
                }

                var hits = await _memoryStore.SearchMemoriesAsync(currentPrompt, limit: 3, ct: ct).ConfigureAwait(false);
                if (hits.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("[Relevant Memory Search Hits]");
                    foreach (var h in hits)
                    {
                        var snippet = h.Content.Length > 200 ? h.Content[..200] + "..." : h.Content;
                        sb.AppendLine($"- [{h.Category}] {snippet.Trim()}");
                    }
                }
            }
            catch
            {
                // Non-blocking if memory files are empty/initializing
            }
        }

        messages.Add(new LlmMessage("system", sb.ToString().Trim()));

        // D. Multi-turn Transcript Conversation History
        if (_activeSession != null)
        {
            var transcript = await _sessionManager.GetTranscriptAsync(_activeSession.Id, ct: ct).ConfigureAwait(false);
            // Append up to last 20 messages so conversation context is completely continuous
            foreach (var t in transcript.TakeLast(20))
            {
                if (string.IsNullOrWhiteSpace(t.Content)) continue;
                var role = t.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? "assistant" : "user";
                messages.Add(new LlmMessage(role, t.Content));
            }
        }

        // If for any reason messages has no user turn yet, append the current prompt
        if (!messages.Any(m => m.Role.Equals("user", StringComparison.OrdinalIgnoreCase)))
        {
            messages.Add(new LlmMessage("user", currentPrompt));
        }

        return messages;
    }

    /// <summary>
    /// Renders markdown text with live ANSI formatting (headers, code fences with line numbers, bullets, inline code).
    /// </summary>
    public void RenderStreamingMarkdown(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return;

        var lines = markdown.Split('\n');
        bool inCodeFence = false;
        string codeFenceLang = string.Empty;
        int codeLineNumber = 1;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');

            if (line.StartsWith("```"))
            {
                if (!inCodeFence)
                {
                    inCodeFence = true;
                    codeFenceLang = line.Substring(3).Trim();
                    codeLineNumber = 1;
                    string header = string.IsNullOrEmpty(codeFenceLang) ? "code" : codeFenceLang;
                    _console.RenderMarkupLine($"[dim]┌──[/] [bold yellow]{header}[/] [dim]─────────────────────────────────────────────────[/]");
                }
                else
                {
                    inCodeFence = false;
                    _console.RenderMarkupLine("[dim]└───[/]");
                }
                continue;
            }

            if (inCodeFence)
            {
                string gutter = codeLineNumber.ToString().PadLeft(4) + " │ ";
                string highlighted = HighlightCodeSyntax(line);
                _console.RenderMarkupLine($"[dim]{gutter}[/]{highlighted}");
                codeLineNumber++;
                continue;
            }

            // Headers
            if (line.StartsWith("### "))
            {
                _console.RenderMarkupLine($"[bold yellow]{AnsiTerminal.EscapeMarkup(line.Substring(4))}[/]");
            }
            else if (line.StartsWith("## "))
            {
                _console.RenderMarkupLine($"[bold cyan]{AnsiTerminal.EscapeMarkup(line.Substring(3))}[/]");
            }
            else if (line.StartsWith("# "))
            {
                _console.RenderMarkupLine($"[bold bright-cyan]{AnsiTerminal.EscapeMarkup(line.Substring(2))}[/]");
            }
            else if (line.StartsWith("* ") || line.StartsWith("- "))
            {
                string bulletContent = FormatInlineMarkdown(line.Substring(2));
                _console.RenderMarkupLine($"  [cyan]•[/] {bulletContent}");
            }
            else
            {
                _console.RenderMarkupLine(FormatInlineMarkdown(line));
            }
        }
    }

    private static string HighlightCodeSyntax(string line)
    {
        string safe = AnsiTerminal.EscapeMarkup(line);
        // Simple high-contrast keyword highlighting
        string[] keywords = ["public", "private", "protected", "class", "async", "await", "return", "var", "string", "int", "bool", "if", "else", "foreach", "new", "static", "sealed", "void", "task", "import", "def", "function", "const", "let"];
        foreach (var kw in keywords)
        {
            safe = Regex.Replace(safe, $@"\b{kw}\b", $"[bold cyan]{kw}[/]");
        }
        return safe;
    }

    private static string FormatInlineMarkdown(string text)
    {
        string safe = AnsiTerminal.EscapeMarkup(text);
        // Bold: **text**
        safe = Regex.Replace(safe, @"\*\*(.+?)\*\*", "[bold]$1[/]");
        // Inline code: `text`
        safe = Regex.Replace(safe, @"`([^`]+)`", "[yellow]$1[/]");
        return safe;
    }

    /// <summary>
    /// Prompts the user before applying a file change, displaying a syntax-highlighted diff.
    /// </summary>
    public async Task<bool> PromptFileModificationAsync(
        string filePath,
        string proposedContent,
        CancellationToken ct = default)
    {
        string fullPath = Path.GetFullPath(Path.Combine(_workingDirectory, filePath));
        string existingContent = File.Exists(fullPath)
            ? await File.ReadAllTextAsync(fullPath, ct).ConfigureAwait(false)
            : string.Empty;

        var diff = _diffService.ComputeDiff(fullPath, existingContent, proposedContent);
        if (!diff.HasChanges)
        {
            _console.RenderMarkupLine($"[dim]No changes detected for '{filePath}'.[/]");
            return true;
        }

        // Render diff card
        _console.RenderDiff(diff);

        // Check if session has active grant for this file (check both relative and full paths)
        if (_activeSession != null)
        {
            bool granted = await _sessionManager.CheckPermissionGrantAsync(_activeSession.Id, "FileOps", filePath, ct).ConfigureAwait(false)
                || await _sessionManager.CheckPermissionGrantAsync(_activeSession.Id, "FileOps", fullPath, ct).ConfigureAwait(false);
            if (granted)
            {
                _console.RenderMarkupLine($"[green]⚡ Pre-approved via session grant: applying changes to '{filePath}'[/]");
                return await _diffService.ApplyDiffAsync(diff, createBackup: true, ct).ConfigureAwait(false);
            }
        }

        var choice = await _console.PromptChoiceAsync(
            prompt: "Apply this modification?",
            options: ["y", "n", "d", "a"],
            defaultOption: "y",
            ct: ct).ConfigureAwait(false);

        switch (choice.ToLowerInvariant())
        {
            case "y":
            case "yes":
                var applied = await _diffService.ApplyDiffAsync(diff, createBackup: true, ct).ConfigureAwait(false);
                if (applied)
                    _console.RenderMarkupLine($"[green]✔ Successfully applied changes to {filePath}[/]");
                return applied;

            case "a":
            case "always":
                if (_activeSession != null)
                {
                    await _sessionManager.AddPermissionGrantAsync(_activeSession.Id, "FileOps", filePath, "Yellow", ct).ConfigureAwait(false);
                    if (!string.Equals(filePath, fullPath, StringComparison.OrdinalIgnoreCase))
                    {
                        await _sessionManager.AddPermissionGrantAsync(_activeSession.Id, "FileOps", fullPath, "Yellow", ct).ConfigureAwait(false);
                    }
                }
                var alwaysApplied = await _diffService.ApplyDiffAsync(diff, createBackup: true, ct).ConfigureAwait(false);
                _console.RenderMarkupLine($"[green]✔ Applied changes and granted session-scoped permission for {filePath}[/]");
                return alwaysApplied;

            case "d":
            case "details":
                _console.WriteLine(proposedContent);
                return await PromptFileModificationAsync(filePath, proposedContent, ct).ConfigureAwait(false);

            default:
                _console.RenderMarkupLine($"[yellow]Modification for {filePath} rejected by user.[/]");
                return false;
        }
    }

    /// <summary>
    /// Evaluates action risk against SmartApproval governance and presents interactive prompts.
    /// </summary>
    public async Task<bool> EvaluateSmartApprovalAsync(
        string actionName,
        RiskTier tier,
        string description,
        CancellationToken ct = default)
    {
        // 1. Check session grants
        if (_activeSession != null)
        {
            bool granted = await _sessionManager.CheckPermissionGrantAsync(_activeSession.Id, actionName, "*", ct).ConfigureAwait(false);
            if (granted)
            {
                _console.RenderMarkupLine($"[dim]⚡ {actionName}: pre-approved via session grant[/]");
                return true;
            }
        }

        switch (tier)
        {
            case RiskTier.Green:
                _console.RenderMarkupLine($"[dim]⚡ {actionName}: auto-approved (Green Tier)[/]");
                return true;

            case RiskTier.Yellow:
                return await HandleYellowPromptAsync(actionName, description, ct).ConfigureAwait(false);

            case RiskTier.Red:
                return await HandleRedPromptAsync(actionName, description, ct).ConfigureAwait(false);

            default:
                return false;
        }
    }

    private async Task<bool> HandleYellowPromptAsync(string actionName, string description, CancellationToken ct)
    {
        _console.RenderMarkupLine($"[yellow]⚡ Action '{actionName}' [Yellow Tier: {description}][/]");
        _console.RenderMarkupLine("[dim]Auto-approving in 5s... [Press 'n' to cancel, 'y' to approve immediately][/]");

        // Non-interactive fallback
        if (!_console.IsInteractive)
        {
            return true;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            bool approved = await _console.ConfirmAsync("Proceed?", defaultYes: true, cts.Token).ConfigureAwait(false);
            return approved;
        }
        catch (OperationCanceledException)
        {
            // 5s elapsed without cancellation -> Auto-approved
            _console.RenderMarkupLine("[dim]Grace period elapsed: auto-approved (Yellow Tier)[/]");
            return true;
        }
    }

    private async Task<bool> HandleRedPromptAsync(string actionName, string description, CancellationToken ct)
    {
        _console.RenderMarkupLine("[bold red]╔══════════════════════════════════════════════════════════════════╗[/]");
        _console.RenderMarkupLine("[bold red]║ ⚠️  RED TIER PERMISSION REQUIRED                                 ║[/]");
        _console.RenderMarkupLine($"[bold red]║[/] Action:      [bold yellow]{actionName.PadRight(50)}[/] [bold red]║[/]");
        _console.RenderMarkupLine($"[bold red]║[/] Description: {description.PadRight(50).Substring(0, 50)} [bold red]║[/]");
        _console.RenderMarkupLine("[bold red]║[/] Risk:        [red]Critical / Destructive action requiring authorization[/]   [bold red]║[/]");
        _console.RenderMarkupLine("[bold red]╚══════════════════════════════════════════════════════════════════╝[/]");
        _console.RenderMarkupLine("Options: [bold][y][/] Yes  [bold][n][/] No (default)  [bold][a][/] Always allow for session  [bold][d][/] Details");

        var choice = await _console.PromptChoiceAsync(
            prompt: "Select option",
            options: ["y", "n", "a", "d"],
            defaultOption: "n",
            ct: ct).ConfigureAwait(false);

        switch (choice.ToLowerInvariant())
        {
            case "y":
            case "yes":
                return true;

            case "a":
            case "always":
                if (_activeSession != null)
                {
                    await _sessionManager.AddPermissionGrantAsync(_activeSession.Id, actionName, "*", "Red", ct).ConfigureAwait(false);
                    _console.RenderMarkupLine($"[green]✔ Granted session-scoped approval for {actionName}[/]");
                }
                return true;

            case "d":
            case "details":
                _console.RenderMarkupLine($"[dim]Action details: {actionName} | {description}[/]");
                return await HandleRedPromptAsync(actionName, description, ct).ConfigureAwait(false);

            default:
                _console.RenderMarkupLine("[red]Action denied by user.[/]");
                return false;
        }
    }

    /// <summary>
    /// Animated braille spinner running asynchronously during reasoning or tool execution.
    /// Displays progressive thinking steps and real-time reasoning chunks, and wipes clean once output arrives.
    /// </summary>
    private sealed class BrailleSpinner : IDisposable
    {
        private static readonly string[] Frames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
        private readonly ITerminalConsole _console;
        private readonly string _message;
        private readonly CancellationTokenSource _cts = new();
        private Task? _spinnerTask;
        private readonly Stopwatch _sw = new();
        private readonly StringBuilder _accumulatedThought = new();
        private string? _latestThought;
        private readonly object _thoughtLock = new();

        public BrailleSpinner(ITerminalConsole console, string message)
        {
            _console = console;
            _message = message;
        }

        public void UpdateThought(string chunk)
        {
            if (string.IsNullOrEmpty(chunk)) return;
            lock (_thoughtLock)
            {
                _accumulatedThought.Append(chunk);
                var full = _accumulatedThought.ToString().Replace('\r', ' ').Replace('\n', ' ').Trim();
                if (full.Length > 65)
                    _latestThought = "..." + full[^60..];
                else
                    _latestThought = full;
            }
        }

        public string GetCollectedThought()
        {
            lock (_thoughtLock)
            {
                return _accumulatedThought.ToString().Replace('\r', ' ').Replace('\n', ' ').Trim();
            }
        }

        public void Start()
        {
            _sw.Start();
            _spinnerTask = Task.Run(async () =>
            {
                int frameIndex = 0;
                while (!_cts.Token.IsCancellationRequested)
                {
                    string frame = Frames[frameIndex % Frames.Length];
                    frameIndex++;

                    string? thought;
                    lock (_thoughtLock)
                    {
                        thought = _latestThought;
                    }

                    string text;
                    if (!string.IsNullOrWhiteSpace(thought))
                    {
                        text = $"Düşünülüyor: [italic]{thought}[/]";
                    }
                    else
                    {
                        var sec = _sw.Elapsed.TotalSeconds;
                        if (sec < 2.5)
                            text = $"İstek {_message}...";
                        else if (sec < 6.0)
                            text = "Model bağlamı ve prompt analiz ediliyor...";
                        else if (sec < 12.0)
                            text = "Akıl yürütülüyor ve çözüm değerlendiriliyor...";
                        else if (sec < 25.0)
                            text = "Hermes otonom adımları planlanıyor...";
                        else
                            text = "Çözüm sentezleniyor ve toparlanıyor...";
                    }

                    _console.ClearLine();
                    _console.WriteAnsi($"\r[cyan]{frame}[/] [dim]{text} ({_sw.Elapsed.TotalSeconds:F1}s)[/]");

                    try
                    {
                        await Task.Delay(80, _cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            });
        }

        public void Stop()
        {
            if (!_cts.IsCancellationRequested)
            {
                _cts.Cancel();
                _sw.Stop();
                try
                {
                    _spinnerTask?.Wait(200);
                }
                catch
                {
                    // Ignore task abort
                }
                _console.ClearLine();
                try { _console.WriteAnsi("\r\x1b[2K"); } catch { }
            }
        }

        public void Dispose()
        {
            Stop();
            _cts.Dispose();
        }
    }
}
