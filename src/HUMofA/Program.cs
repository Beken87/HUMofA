using System.Diagnostics;
using System.IO;
using HUMofA.Agent;
using HUMofA.Backends;
using HUMofA.Cli;
using HUMofA.ComputerUse;
using HUMofA.Desktop;
using HUMofA.Desktop.Native;
using HUMofA.Diff;
using HUMofA.Gateway;
using HUMofA.Gateway.Adapters;
using HUMofA.Memory;
using HUMofA.Scheduler;
using HUMofA.Sessions;
using HUMofA.Skills;
using HUMofA.SubAgents;
using HUMofA.Tools;

namespace HUMofA;

public enum HofaStartupMode
{
    Cli,
    Desktop,
    Setup,
    Sessions,
    WebServer,
    Headless
}

public partial class Program
{
    public const string Version = "1.0.0";

    public static HofaStartupMode ResolveStartupMode(string[] args)
    {
        if (args.Contains("--headless", StringComparer.OrdinalIgnoreCase) ||
            args.Contains("headless", StringComparer.OrdinalIgnoreCase))
        {
            return HofaStartupMode.Headless;
        }

        if (args.Contains("--list-sessions", StringComparer.OrdinalIgnoreCase) ||
            args.Contains("sessions", StringComparer.OrdinalIgnoreCase))
        {
            return HofaStartupMode.Sessions;
        }

        if (args.Contains("--setup", StringComparer.OrdinalIgnoreCase) ||
            args.Contains("setup", StringComparer.OrdinalIgnoreCase))
        {
            return HofaStartupMode.Setup;
        }

        if (args.Contains("--desktop", StringComparer.OrdinalIgnoreCase) ||
            args.Contains("desktop", StringComparer.OrdinalIgnoreCase) ||
            args.Contains("gui", StringComparer.OrdinalIgnoreCase))
        {
            return HofaStartupMode.Desktop;
        }

        if (args.Contains("--no-window", StringComparer.OrdinalIgnoreCase) ||
            args.Contains("web", StringComparer.OrdinalIgnoreCase) ||
            args.Contains("--web", StringComparer.OrdinalIgnoreCase))
        {
            return HofaStartupMode.WebServer;
        }

        if (args.Contains("--cli", StringComparer.OrdinalIgnoreCase) ||
            args.Contains("cli", StringComparer.OrdinalIgnoreCase))
        {
            return HofaStartupMode.Cli;
        }

        // Default when user types `hofa` with no subcommands: open Interactive CLI directly in current directory!
        return HofaStartupMode.Cli;
    }

    public static async Task<int> Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.InputEncoding = System.Text.Encoding.UTF8;
        }
        catch { }

        Console.WriteLine($"==================================================");
        Console.WriteLine($" Welcome to HOFA / HUMofA (Humanize of AI) v{Version}");
        Console.WriteLine($" Autonomous Right-Hand PC Assistant Platform      ");
        Console.WriteLine($"==================================================");

        var mode = ResolveStartupMode(args);
        if (mode == HofaStartupMode.Headless)
        {
            Console.WriteLine("[HUMofA] Launching in --headless mode...");
            await using var appCore = new AppCore();
            await appCore.InitializeAsync(isHeadless: true);
            Console.WriteLine($"[HUMofA] Core initialized successfully (v{appCore.Version}). Exiting headless startup cleanly.");
            return 0;
        }

        string? workDir = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--workdir", StringComparison.OrdinalIgnoreCase))
            {
                workDir = args[i + 1];
            }
        }

        var workspaceDir = workDir != null
            ? Path.Combine(workDir, ".humofa-data")
            : Path.Combine(Directory.GetCurrentDirectory(), ".humofa-data");

        if (mode == HofaStartupMode.Sessions)
        {
            await using var listCore = new AppCore(workspaceDir);
            await listCore.InitializeAsync(isHeadless: true);
            var sessions = await listCore.SessionManager.ListSessionsAsync();
            Console.WriteLine("--------------------------------------------------------------------------------");
            Console.WriteLine("ID       Title                                Messages  Tokens  Active  Updated");
            Console.WriteLine("--------------------------------------------------------------------------------");
            foreach (var s in sessions)
            {
                string marker = s.IsActive ? "*" : " ";
                string id = s.Id.Length > 7 ? s.Id.Substring(0, 7) : s.Id;
                string title = s.Title.Length > 35 ? s.Title.Substring(0, 32) + "..." : s.Title.PadRight(35);
                Console.WriteLine($"{marker} {id,-7} {title} {s.MessageCount,8} {s.TotalTokens,6} {s.Model,-14} {s.UpdatedAt:yyyy-MM-dd HH:mm}");
            }
            return 0;
        }

        int port = 5050;
        string? sessionId = null;
        string? model = null;
        bool continueSession = args.Contains("--continue", StringComparer.OrdinalIgnoreCase) || args.Contains("-c", StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--port", StringComparison.OrdinalIgnoreCase) && i < args.Length - 1 &&
                int.TryParse(args[i + 1], out int parsedPort))
            {
                port = parsedPort;
            }
            else if (string.Equals(args[i], "--session", StringComparison.OrdinalIgnoreCase) && i < args.Length - 1)
            {
                sessionId = args[i + 1];
            }
            else if (string.Equals(args[i], "--model", StringComparison.OrdinalIgnoreCase) && i < args.Length - 1)
            {
                model = args[i + 1];
            }
        }

        await using var interactiveCore = new AppCore(workspaceDir);
        await interactiveCore.InitializeAsync(isHeadless: false);

        if (mode == HofaStartupMode.Setup)
        {
            var historyPath = Path.Combine(workspaceDir, "cli_history.txt");
            var terminal = new AnsiTerminal(historyPath);
            var setupCli = new InteractiveCli(
                terminal,
                interactiveCore.SessionManager,
                interactiveCore.AgentManager,
                interactiveCore.DiffService,
                interactiveCore.SmartApproval,
                interactiveCore.MemoryStore,
                interactiveCore.SkillManager,
                interactiveCore.Router,
                interactiveCore.Backends,
                interactiveCore.SubAgents,
                interactiveCore.EventBus,
                interactiveCore.GhostTyping,
                initialSessionId: sessionId,
                initialModel: model,
                initialWorkingDir: workDir ?? Directory.GetCurrentDirectory(),
                settingsFilePath: Path.Combine(workspaceDir, "settings.json"));
            await setupCli.InitializeSessionAsync();
            await setupCli.RunSetupWizardAsync();
            return 0;
        }

        if (continueSession && string.IsNullOrEmpty(sessionId))
        {
            var active = await interactiveCore.SessionManager.GetActiveSessionAsync();
            sessionId = active?.Id;
            if (sessionId == null)
            {
                var all = await interactiveCore.SessionManager.ListSessionsAsync();
                sessionId = all.OrderByDescending(s => s.UpdatedAt).FirstOrDefault()?.Id;
            }
        }

        var url = await interactiveCore.Desktop.StartAsync(port: port);
        Console.WriteLine($"[HOFA] Core initialized successfully (v{interactiveCore.Version}).");
        Console.WriteLine($"[HOFA] Background API & WebSocket Server listening at: {url}");

        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            if (mode == HofaStartupMode.Desktop)
            {
                Console.WriteLine("[HOFA] Launching Pure C# WPF Native Desktop Application...");
                await NativeDesktopLauncher.RunNativeDesktopWindowAsync(
                    interactiveCore,
                    Path.Combine(workspaceDir, "settings.json"));
                return 0;
            }

            if (mode == HofaStartupMode.Cli)
            {
                await RunInteractiveCliLoopAsync(
                    interactiveCore,
                    sessionId: sessionId,
                    model: model,
                    workDir: workDir ?? Directory.GetCurrentDirectory(),
                    ct: cts.Token);
                return 0;
            }

            Console.WriteLine($"[HOFA] Running in Web/API Server mode ({url}).");
            Console.WriteLine($"[HOFA] Commands: 'hofa' (CLI), 'hofa desktop' (Native C# UI), 'hofa setup' (Setup Wizard).");
            Console.WriteLine($"[HOFA] Press Ctrl+C to stop.");

            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("\n[HUMofA] Shutting down cleanly...");
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }

        return 0;
    }

    private static void TryLaunchNativeDesktopWindow(string url)
    {
        try
        {
            // Launch native chromeless desktop window via Edge App Mode (WebView2 shell) or default browser fallback
            var edgePsi = new ProcessStartInfo
            {
                FileName = "msedge",
                Arguments = $"--app={url} --window-size=1360,860",
                UseShellExecute = true
            };
            Process.Start(edgePsi);
            Console.WriteLine($"[HUMofA] Native Desktop App Window launched ({url}).");
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch
            {
                // Headless or no GUI session available
            }
        }
    }

    public static async Task RunInteractiveCliLoopAsync(
        AppCore core,
        ITerminalConsole? console = null,
        string? sessionId = null,
        string? model = null,
        string? workDir = null,
        CancellationToken ct = default)
    {
        var historyPath = Path.Combine(Directory.GetCurrentDirectory(), ".humofa-data", "cli_history.txt");
        var terminal = console ?? new AnsiTerminal(historyPath);

        var cli = new InteractiveCli(
            terminal,
            core.SessionManager,
            core.AgentManager,
            core.DiffService,
            core.SmartApproval,
            core.MemoryStore,
            core.SkillManager,
            core.Router,
            core.Backends,
            core.SubAgents,
            core.EventBus,
            core.GhostTyping,
            initialSessionId: sessionId,
            initialModel: model,
            initialWorkingDir: workDir ?? Directory.GetCurrentDirectory());

        await cli.RunAsync(ct).ConfigureAwait(false);
    }
}

public sealed class AppCore : IAsyncDisposable
{
    public string Version => Program.Version;
    public bool IsInitialized { get; private set; }
    public bool IsHeadless { get; private set; }

    public CentralEventBus EventBus { get; }
    public GhostTypingSync GhostTyping { get; }
    public HumanizedController Controller { get; }
    public UnifiedMemoryStore MemoryStore { get; }
    public SkillManager SkillManager { get; }
    public SubAgentOrchestrator SubAgents { get; }
    public ExecutionEnvironmentManager Backends { get; }
    public MultimodalToolset Toolset { get; }
    public MultiModelRouter Router { get; }
    public NaturalCronScheduler Scheduler { get; }
    public DesktopHost Desktop { get; }

    public ISessionManager SessionManager { get; }
    public IAgentManager AgentManager { get; }
    public IFileDiffService DiffService { get; }
    public SmartApproval SmartApproval { get; }

    public AppCore(string? workspaceDir = null)
    {
        var baseDir = workspaceDir ?? Path.Combine(Path.GetTempPath(), "humofa-core", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);

        EventBus = new CentralEventBus();
        var inputSim = new MockInputSimulator();
        var killSwitch = new PanicKillSwitch();
        Controller = new HumanizedController(inputSim, killSwitch) { FastModeForTesting = true };
        GhostTyping = new GhostTypingSync();
        MemoryStore = new UnifiedMemoryStore(Path.Combine(baseDir, "memory"));
        SkillManager = new SkillManager(Path.Combine(baseDir, "skills"), searchIndex: MemoryStore.SearchIndex);
        SubAgents = new SubAgentOrchestrator();
        Backends = new ExecutionEnvironmentManager();
        Router = new MultiModelRouter();
        Toolset = new MultimodalToolset(Router);
        Scheduler = new NaturalCronScheduler(EventBus);
        Desktop = new DesktopHost(
            EventBus,
            GhostTyping,
            Controller,
            MemoryStore,
            SkillManager,
            SubAgents,
            Backends,
            Toolset,
            Router,
            Scheduler,
            baseDir);

        var sessionsDir = Path.Combine(baseDir, "sessions");
        Directory.CreateDirectory(sessionsDir);
        SessionManager = new SessionManager(Path.Combine(sessionsDir, "sessions.db"));
        AgentManager = new AgentManager(SubAgents);
        DiffService = new FileDiffService(Path.Combine(baseDir, "backups"));
        SmartApproval = new SmartApproval(EventBus);
    }

    public async Task InitializeAsync(bool isHeadless = false)
    {
        IsHeadless = isHeadless;
        await MemoryStore.InitializeAsync();
        IsInitialized = true;
    }

    public async ValueTask DisposeAsync()
    {
        await SessionManager.DisposeAsync();
        await Desktop.DisposeAsync();
    }
}
