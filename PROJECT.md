# Project: HUMofA (Humanize of AI)

## Architecture
HUMofA is a production-grade autonomous PC assistant platform built in C# (.NET 10) with a Python 3.14 JSON-RPC bridge. It bridges remote communication channels (Telegram, Discord, Slack, WhatsApp, Signal, Email, CLI) with a local desktop environment, executing humanized computer control (Bezier mouse physics governed by Fitts's Law, natural keyboard cadence, Set-of-Mark visual perception, sub-10ms panic kill-switch, smart approval) and decoupled desktop hosting.

### Key Architectural Layers:
1. **Desktop Host & Web UI Layer (`src/Desktop/`)**: Kestrel minimal API + static assets + WebSockets. Decoupled from Windows GUI message loops to enable 100% headless execution in CI while launching interactive WebView2/Browser on desktop.
2. **OmniChannel Gateway & Ghost Typing (`src/Gateway/`)**: `System.Threading.Channels` CentralEventBus streaming remote incoming messages character-by-character as ghost typing into the desktop UI, with 3-tier Smart Approval.
3. **Humanized Computer Control (`src/ComputerUse/`)**: Pure Win32 P/Invoke (`user32.dll`, `gdi32.dll`), `IInputSimulator` abstraction (Win32 vs Mock), cubic Bezier curves, Fitts's Law movement time, Gaussian micro-tremors, log-normal typing cadence, atomic low-level panic kill-switch (<10ms), and Set-of-Mark (SOM) accessibility tree extraction via COM `IUIAutomation`.
4. **Memory Engine & Skills Hub (`src/Memory/`, `src/Skills/`)**: Persistent markdown memory (`SOUL.md`, `MEMORY.md`, `USER.md`), SQLite FTS5 BM25 search, Watch & Learn macro synthesizer, 10 official GitHub taps, 35+ NousResearch categories, and SkillGuard Python AST security scanner.
5. **Sub-Agent Orchestration & Backends (`src/SubAgents/`, `src/Backends/`)**: Context-isolated sub-agents with Kanban tracking, Python 3.14 JSON-RPC 2.0 stdio bridge, and 5 execution environments (Local, Docker hardened, SSH, Singularity, Modal) with mock fallbacks.
6. **Multimodal Tools & Router (`src/Tools/`, `src/Agent/`, `src/Scheduler/`)**: CDP browser automation, web search, vision, TTS/STT, dynamic multi-model reasoning (Gemini, OpenAI, Anthropic, OpenRouter, Nous Portal, local Ollama), and NaturalCronScheduler.

## Code Layout
```
c:\Users\beken\Desktop\Files\
├── HUMofA/ (NTFS junction pointing to BofA)
│   ├── HUMofA.sln
│   ├── .env.example
│   ├── src/
│   │   └── HUMofA/
│   │       ├── HUMofA.csproj
│   │       ├── Program.cs
│   │       ├── ComputerUse/
│   │       │   ├── Win32Native.cs
│   │       │   ├── IInputSimulator.cs
│   │       │   ├── Win32InputSimulator.cs
│   │       │   ├── HumanizedController.cs
│   │       │   ├── BezierMath.cs
│   │       │   ├── PanicKillSwitch.cs
│   │       │   ├── SetOfMark.cs
│   │       │   └── UIAutomationTree.cs
│   │       ├── Gateway/
│   │       │   ├── CentralEventBus.cs
│   │       │   ├── GhostTypingSync.cs
│   │       │   ├── SmartApproval.cs
│   │       │   └── Adapters/ (Telegram, Discord, Slack, WhatsApp, Signal, Email, Cli)
│   │       ├── Memory/
│   │       │   ├── UnifiedMemoryStore.cs
│   │       │   ├── Fts5SearchIndex.cs
│   │       │   └── WatchAndLearnSynthesizer.cs
│   │       ├── Skills/
│   │       │   ├── SkillsHubGitHub.cs
│   │       │   ├── SkillManager.cs
│   │       │   └── SkillGuard.cs
│   │       ├── SubAgents/
│   │       │   ├── SubAgentOrchestrator.cs
│   │       │   ├── PythonRpcBridge.cs
│   │       │   └── KanbanTracker.cs
│   │       ├── Backends/
│   │       │   ├── ExecutionEnvironmentManager.cs
│   │       │   └── Providers/ (Local, Docker, SSH, Singularity, Modal)
│   │       ├── Tools/
│   │       │   └── MultimodalToolset.cs
│   │       ├── Agent/
│   │       │   └── MultiModelRouter.cs
│   │       ├── Scheduler/
│   │       │   └── NaturalCronScheduler.cs
│   │       └── Desktop/
│   │           ├── DesktopHost.cs
│   │           └── UI/
│   │               ├── index.html
│   │               ├── app.js
│   │               └── style.css
│   └── tests/
│       └── HUMofA.Tests/
│           ├── HUMofA.Tests.csproj
│           ├── ComputerUseTests.cs
│           ├── GatewayTests.cs
│           ├── MemoryTests.cs
│           ├── SkillsTests.cs
│           ├── SubAgentTests.cs
│           ├── BackendsTests.cs
│           ├── RouterTests.cs
│           └── DesktopHostTests.cs
```

## Feature Inventory
| # | Feature | Description | Milestone | Source |
|---|---------|-------------|-----------|--------|
| 1 | Directory & Solution Setup | NTFS junction HUMofA -> BofA, HUMofA.sln, net10.0-windows projects, .env.example | M1 | Survey |
| 2 | Pure Win32 P/Invoke & Struct Layout | Explicit 64-bit INPUT (40 bytes), MOUSEINPUT, KEYBDINPUT structs with [LibraryImport] | M2 | R7 |
| 3 | Input Simulator Abstraction | IInputSimulator with Win32 and Mock implementations for safe hardware testing | M2 | Survey |
| 4 | Bezier Curve Mouse Trajectory | Cubic Bezier curves with orthogonal chord deviations and minimum-jerk easing | M2 | R7 |
| 5 | Fitts's Law Movement Calculation | MT = a + b * log2(2D/W) movement duration with 1000Hz (1ms step) interpolation | M2 | R7 |
| 6 | Natural Gaussian Micro-Tremor | Micro-jitter injection mimicking physiological hand tremors | M2 | R7 |
| 7 | Natural Cadence Keyboard Typing | Log-normal typing delay distributions (60-140ms), typo injection and backspacing | M2 | R7 |
| 8 | Panic Kill-Switch Handler | Sub-10ms emergency cut-off triggered by physical mouse delta (>200px/s) or hotkey | M2 | R7, AC |
| 9 | Set-of-Mark (SOM) Visual Perception | Numbered marker overlays [1], [2], [3] and COM IUIAutomation tree extraction | M2 | R7 |
| 10 | 14 Computer Actions Engine | Click, right click, double click, drag, scroll, type, press key, focus window, etc. | M2 | R7 |
| 11 | Central Event Bus | System.Threading.Channels based thread-safe message exchange | M3 | R1 |
| 12 | Real-time Ghost Typing Sync | Character-by-character streaming from incoming remote messages to Desktop chat UI | M3 | R1 |
| 13 | OmniChannel Adapters | Telegram, Discord, Slack, WhatsApp, Signal, Email (IMAP/SMTP), and CLI | M3 | R1 |
| 14 | Offline Stub Fallbacks | Mock channel adapters operating gracefully without credentials in .env | M3 | R8, AC |
| 15 | Tiered Smart Approval | Green / Yellow / Red risk assessment with screenshot cards and approval actions | M3 | R7 |
| 16 | Natural Language Cron Scheduler | Natural phrasing parsing ("Every morning at 09:00") and unattended task execution | M3 | R3 |
| 17 | Unified Markdown Memory | SOUL.md, MEMORY.md, and USER.md persistence and memory import | M4 | R2 |
| 18 | SQLite FTS5 Full-Text Search | Sub-millisecond BM25 keyword matching across indexed conversations and memories | M4 | R2 |
| 19 | Watch & Learn Macro Synthesizer | Synthesis of executed action sequences into reusable SKILL.md definitions | M4 | R2 |
| 20 | GitHub Skills Hub (10 Official Taps) | openai, anthropics, huggingface, nvidia, voltagent, gstack, minimax, wandb, qdrant, pinecone | M4 | R2 |
| 21 | NousResearch & Custom Repo Support | 35+ NousResearch/hermes-agent categories and custom git/tarball repo installer | M4 | R2 |
| 22 | SkillGuard AST Security Scanner | Python AST analysis blocking forbidden primitives (os.system, eval, exfiltration) | M4 | R2 |
| 23 | Sub-Agent Context Isolation | Token-budgeted sub-agent execution with independent context tracking | M4/M5 | R4 |
| 24 | Python 3.14 JSON-RPC Bridge | JSON-RPC 2.0 stdio communication bridge with Python 3.14 runtime | M5 | R4 |
| 25 | Integrated Kanban Task Tracker | Kanban state machine (Pending, InProgress, Blocked, Done) for sub-agents | M5 | R4 |
| 26 | 5 Multi-Backend Environments | Local Windows, Docker (hardened --cap-drop=ALL), SSH, Singularity, Modal serverless | M5 | R6 |
| 27 | Multimodal Toolset | CDP browser automation, web search, vision OCR, image generation, TTS & STT | M6 | R5 |
| 28 | Dynamic Multi-Model Router | Dynamic routing across Gemini, OpenAI, Anthropic, OpenRouter, Nous Portal, Ollama | M6 | R5 |
| 29 | Desktop Host Application | Kestrel web server + static UI (chat, SOM preview, skills hub, Kanban, settings) | M6 | R8 |
| 30 | Automated Test Suite (100% Pass) | Comprehensive unit, integration, and acceptance tests executing via dotnet test | M7 | AC |

## Milestones
| # | Name | Scope | Dependencies | Status |
|---|------|-------|-------------|--------|
| M1 | Directory & Solution Foundation | NTFS junction, HUMofA.sln, HUMofA.csproj, HUMofA.Tests.csproj, .env.example, initial graphify build | none | DONE (HUMofA.sln, junction, .env.example, 4 smoke tests passed) |
| M2 | Humanized Computer Control & Win32 Native | Win32Native, IInputSimulator, Bezier curves, Fitts's Law, typing cadence, PanicKillSwitch (<10ms), SetOfMark, UIAutomationTree | M1 | DONE (Win32Native, Bezier, PanicKillSwitch, SOM, 70/70 tests pass) |
| M3 | OmniChannel Gateway, Ghost Typing & Cron | CentralEventBus, GhostTypingSync, 7 channel adapters, offline mocks, SmartApproval, NaturalCronScheduler | M1 | DONE (CentralEventBus, GhostTypingSync, 7 adapters, SmartApproval 256-bit CSPRNG, 274/274 tests pass, Clean Audit) |
| M4 | Shared Core Agent Engine, Session Persistence & Diff Engine | SQLite SessionManager (sessions.db), AgentManager, FileDiffService (Myers diff algorithm), SmartApproval risk governance | M1-M3 | DONE (336/336 tests pass, 0 errors, Clean Audit) |
| M5 | Claude Code-Grade Terminal CLI Interface | AnsiTerminal, InteractiveCli, status badges, streaming markdown tokenizer, braille spinners, tool execution cards, file diff previews (y/n/d/a), SmartApproval countdown, slash commands, autocomplete, Program.cs wiring | M4 | DONE (497/497 tests pass, 0 warnings, 0 errors, Clean Audit) |
| M6 | Production Desktop Host REST & WebSocket Backend | DesktopHost.cs expanded to 52 concrete production REST endpoints (Sessions, Tasks, Filesystem/Diff, Agents, Settings, Terminal Exec) + live WebSocket protocol (/ws/live), path traversal sandbox, concurrency locks | M4 | DONE (531/531 tests pass, 0 warnings, 0 errors, Clean Audit) |
| M7 | Production Desktop Workspace Application UI | Production UI in src/HUMofA/Desktop/UI/ (index.html, app.js, style.css): Left Sidebar (11 views), Adaptive Main Workspace (7 components), Contextual Right Panel (8 modules), Raycast Command Palette (Ctrl+K), zero placeholders | M6 | IN_PROGRESS |
| M8 | Full Verification, E2E Workflows & Final Audit | 100% pass across all 291 baseline tests + all new test suites, autonomous workflow verification, zero warnings, Clean Forensic Audit | M4-M7 | PLANNED |

## Interface Contracts

### 1. `IInputSimulator` (Computer Use Abstraction)
```csharp
public interface IInputSimulator
{
    void SendMouseMove(int x, int y);
    void SendMouseButton(MouseButton button, bool isDown);
    void SendKey(ushort virtualKey, bool isDown);
    void SendChar(char c);
    Point GetCursorPosition();
}
```

### 2. `CentralEventBus` (Gateway Event Stream)
```csharp
public record AgentMessage(string Id, string Channel, string Sender, string Content, DateTime Timestamp, bool IsGhostTyping = false);
public interface IEventBus
{
    ValueTask PublishAsync(AgentMessage message, CancellationToken ct = default);
    IAsyncEnumerable<AgentMessage> SubscribeAsync(CancellationToken ct = default);
}
```

### 3. `IPythonRpcBridge` (Sub-Agents RPC)
```csharp
public interface IPythonRpcBridge
{
    Task<JsonRpcResponse> InvokeAsync(string method, object? parameters, CancellationToken ct = default);
    Task StartAsync(string scriptPath, CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
}
```

### 4. `ISkillGuard` (Security AST Scanner)
```csharp
public record SecurityScanResult(bool IsSafe, IReadOnlyList<string> Violations);
public interface ISkillGuard
{
    Task<SecurityScanResult> ScanPythonCodeAsync(string code, CancellationToken ct = default);
    Task<SecurityScanResult> ScanSkillFileAsync(string skillPath, CancellationToken ct = default);
}
```

### 5. `IMultiModelRouter` (Model Fallback Dispatcher)
```csharp
public interface IMultiModelRouter
{
    Task<string> CompleteAsync(string prompt, string? preferredModel = null, CancellationToken ct = default);
}
```
