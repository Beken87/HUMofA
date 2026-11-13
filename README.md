# HUMofA (Humanize of AI) 🤖✨

[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)
[![C#](https://img.shields.io/badge/C%23-14.0-blue.svg)](https://learn.microsoft.com/en-us/dotnet/csharp/)
[![Python 3.14](https://img.shields.io/badge/Python-3.14-yellow.svg)](https://www.python.org/)
[![Tests](https://img.shields.io/badge/Tests-574%20Passed%20(100%25)-brightgreen.svg)]()
[![Platform](https://img.shields.io/badge/Platform-Windows%20x64-0078D6.svg)]()
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

**HUMofA** is a production-grade, enterprise-ready autonomous PC assistant and multi-agent operating platform built in **C# .NET 10** with a high-performance **Python 3.14 JSON-RPC 2.0** bridge.

It seamlessly unifies omnichannel remote gateways (Telegram, Discord, Slack, WhatsApp, Signal, Email, Interactive CLI) with humanized local desktop control, Set-of-Mark visual perception, long-term memory engine, and autonomous sub-agent swarms.

---

## 🌟 Key Features

### 1. 🖥️ Humanized Computer Control
- **Win32 Native Precision:** Direct P/Invoke integration with `user32.dll` and `gdi32.dll`.
- **Natural Physics:** Mouse trajectories generated via Cubic Bézier curves governed by **Fitts's Law** with Gaussian micro-tremors.
- **Log-Normal Keystrokes:** Human-like typing rhythms and variable inter-key latencies.
- **Set-of-Mark (SOM) Perception:** UI Automation tree inspection via COM `IUIAutomation` for precise visual grounding and coordinate target identification.
- **Sub-10ms Panic Kill-Switch:** Low-level OS hook that instantly halts all automation if human intervention is detected.

### 2. 🤖 Autonomous Sub-Agents & Swarms
- **Dynamic Persona Delegation:** Spawn, monitor, switch, and delete sub-agents on the fly (`/subagent create`, `/subagent spawn`, `/subagent delete`).
- **Interactive Kanban Board:** Real-time ASCII status tracking across `Pending`, `InProgress`, `Blocked`, and `Done` states.
- **Multi-Model Orchestration:** Pin individual agents to distinct AI models based on cognitive strength and cost.

### 3. 🧠 Unified Memory & Skills Hub
- **Triple-Store Memory:** Persistent structured memory (`SOUL.md`, `MEMORY.md`, `USER.md`).
- **FTS5 Full-Text Search:** High-speed SQLite BM25 index for semantic and lexical memory retrieval.
- **SkillGuard Security:** AST analysis of external Python scripts to prevent malicious code execution.
- **Macro Synthesizer:** Watch & Learn recording to convert human desktop demonstrations into automated agent skills.

### 4. 🌐 OmniChannel Gateway & Ghost Typing
- **Multi-Channel Adapters:** Connect via Telegram, Discord, Slack, WhatsApp, Signal, Email, or WebSockets.
- **Central Event Bus:** Non-blocking `System.Threading.Channels` character streaming.
- **Smart Approval:** 3-tier risk-based confirmation (Green, Yellow, Red) for critical actions.

### 5. 🎬 Automated Social Media Video Commentary Pipeline
- **Instagram Scout & Harvester:** Automated scraping of top-performing Reels based on view metrics and viral engagement.
- **Humor Scoring Engine:** Algorithmic curation of the funniest comments.
- **Full Video Studio:** High-res custom comment bubble rendering (Pillow), Turkish neural text-to-speech (`edge-tts`), background music ducking, and vertical Reels rendering via `FFmpeg`.

---

## 🏗️ Architecture Overview

```text
C:\Users\beken\Desktop\Files\BofA\
├── src/
│   └── HUMofA/
│       ├── ComputerUse/      # Win32 P/Invoke, Bezier curves, SOM tree, Panic Switch
│       ├── Gateway/          # CentralEventBus, GhostTyping, SmartApproval, Adapters
│       ├── Memory/           # UnifiedMemoryStore, SQLite FTS5 BM25, Watch & Learn
│       ├── Skills/           # SkillManager, SkillsHubGitHub, AST SkillGuard
│       ├── SubAgents/        # SubAgentOrchestrator, PythonRpcBridge, KanbanTracker
│       ├── Backends/         # Local, Docker hardened, SSH, Singularity, Modal
│       ├── Agent/            # Multi-Model Router, Persona Config, Autonomy Tiers
│       ├── Desktop/          # Decoupled Kestrel API, WebSockets, Native GUI
│       └── Cli/              # Interactive ANSI Terminal, Progressive Markdown
├── scripts/
│   ├── auto_comment_video_editor.py   # Automated comment bubble & TTS video rendering
│   └── instagram_reels_scraper.py     # Instagram Reels & humor comment extractor
├── tests/
│   └── HUMofA.Tests/         # 574 Passing Unit & Integration Tests (100% Pass)
└── HUMofA.sln
```

---

## 🚀 Getting Started

### Prerequisites
- **[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)**
- **[Python 3.12+](https://www.python.org/)** (Recommended: Python 3.14)
- **[FFmpeg](https://ffmpeg.org/)** (Added to system `PATH` for video editing features)

### Installation & Run

1. **Clone the repository:**
   ```bash
   git clone https://github.com/USERNAME/HUMofA.git
   cd HUMofA
   ```

2. **Configure Environment:**
   Copy the example environment file:
   ```bash
   copy .env.example .env
   ```
   Add your API keys (Gemini, OpenAI, Claude, OpenRouter, etc.).

3. **Build the Solution:**
   ```bash
   dotnet build HUMofA.sln -c Release
   ```

4. **Run All Tests:**
   ```bash
   dotnet test HUMofA.sln -c Release --no-build
   ```

5. **Start HUMofA CLI:**
   ```bash
   ./hofa.cmd
   ```
   Or via PowerShell:
   ```powershell
   ./hofa.ps1
   ```

---

## 💻 CLI Quick Reference

```text
/help                           - Show command reference
/model [name|list]              - Switch or list AI models (gemini, gpt-4o, claude, ollama)
/subagent list                  - Display registered sub-agents and Kanban tasks
/subagent create <id> <name>    - Register a new specialized sub-agent
/subagent delete <id>           - Delete an existing sub-agent
/subagent spawn <role> <task>   - Delegate a task to an autonomous sub-agent
/think [on|off|deep]            - Toggle visible thought and action traces
/diff [file]                    - Inspect file changes and rollbacks
/memory [query|add]             - Query or add long-term memory
/doctor                         - Run comprehensive system and API diagnostics
```

---

## 🧪 Testing

The codebase includes comprehensive test suites covering unit logic, concurrent state machines, P/Invoke mocks, and end-to-end flows:

```bash
Passed! - Failed: 0, Passed: 574, Skipped: 0, Total: 574, Duration: 12 s
```

---

## 📄 License

Distributed under the **MIT License**. See `LICENSE` for more information.
