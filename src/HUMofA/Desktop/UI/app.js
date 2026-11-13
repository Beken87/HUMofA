/**
 * HUMofA Production Desktop Workspace Application Core
 * Connects to .NET 10 Kestrel Backend & WebSockets Protocol (/ws/live)
 * Full bindings to all REST endpoints, zero placeholders or dummy stubs.
 */

// ============================================================================
// State Store
// ============================================================================
const state = {
    sessions: [],
    activeSessionId: null,
    activeSession: null,
    tasks: [],
    activeTaskId: null,
    activeTaskDetail: null,
    skills: { installed: [], officialTaps: [], nousCategories: [] },
    kanbanCards: [],
    cronJobs: [],
    memoryDocs: { soul: '', memory: '', user: '' },
    activeMemoryKind: 'Memory',
    integrations: [],
    settings: null,
    status: null,
    fileTree: [],
    editorFile: { path: '', content: '', dirty: false },
    activeDiff: null,
    activeTab: 'chat',
    activeView: 'tasks',
    theme: localStorage.getItem('humofa_theme') || 'dark',
    wsConnected: false,
    smartApprovalPending: null,
    approvalTimer: null,
    telemetry: { promptTokens: 0, completionTokens: 0, totalTokens: 0, cost: 0.0 }
};

// ============================================================================
// REST API Client
// ============================================================================
const api = {
    async get(url) {
        const res = await fetch(url);
        if (!res.ok) throw new Error(`GET ${url} failed with ${res.status}`);
        return await res.json();
    },

    async post(url, data) {
        const res = await fetch(url, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: data ? JSON.stringify(data) : undefined
        });
        const json = await res.json().catch(() => ({}));
        if (!res.ok) {
            const err = new Error(json.error || `POST ${url} failed with ${res.status}`);
            err.status = res.status;
            err.data = json;
            throw err;
        }
        return json;
    },

    async patch(url, data) {
        const res = await fetch(url, {
            method: 'PATCH',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(data)
        });
        return await res.json();
    },

    async delete(url) {
        const res = await fetch(url, { method: 'DELETE' });
        return await res.json();
    }
};

// ============================================================================
// Real-time WebSocket Client (/ws/live)
// ============================================================================
let socket = null;
let reconnectTimer = null;
let heartbeatInterval = null;

function initWebSocket() {
    const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const wsUrl = `${proto}//${location.host}/ws/live`;

    updateWsStatus('connecting', 'Connecting...');

    try {
        socket = new WebSocket(wsUrl);

        socket.onopen = () => {
            state.wsConnected = true;
            updateWsStatus('connected', 'Connected');
            appendLog('info', `WebSocket connected to /ws/live`);

            // Start heartbeat ping every 20 seconds
            if (heartbeatInterval) clearInterval(heartbeatInterval);
            heartbeatInterval = setInterval(() => {
                if (socket && socket.readyState === WebSocket.OPEN) {
                    socket.send('{"type":"ping"}');
                }
            }, 20000);
        };

        socket.onmessage = (event) => {
            try {
                const frame = JSON.parse(event.data);
                handleWsFrame(frame);
            } catch (err) {
                console.warn('Failed to parse WS frame:', err);
            }
        };

        socket.onclose = () => {
            state.wsConnected = false;
            updateWsStatus('disconnected', 'Disconnected');
            appendLog('warn', 'WebSocket disconnected, scheduling reconnect...');
            if (heartbeatInterval) clearInterval(heartbeatInterval);
            scheduleReconnect();
        };

        socket.onerror = (err) => {
            console.error('WebSocket error:', err);
            socket.close();
        };
    } catch (e) {
        updateWsStatus('disconnected', 'Offline');
        scheduleReconnect();
    }
}

function scheduleReconnect() {
    if (reconnectTimer) clearTimeout(reconnectTimer);
    reconnectTimer = setTimeout(initWebSocket, 3000);
}

function updateWsStatus(status, text) {
    const dot = document.getElementById('ws-status-dot');
    const label = document.getElementById('ws-status-text');
    if (dot) dot.className = `status-dot ${status}`;
    if (label) label.textContent = text;
}

function handleWsFrame(frame) {
    const type = frame.type || frame.Type;

    switch (type) {
        case 'stream_chunk':
            handleStreamChunk(frame);
            break;
        case 'tool_started':
            handleToolStarted(frame);
            break;
        case 'tool_progress':
            handleToolProgress(frame);
            break;
        case 'tool_completed':
            handleToolCompleted(frame);
            break;
        case 'tool_failed':
            handleToolFailed(frame);
            break;
        case 'reasoning':
            handleReasoning(frame);
            break;
        case 'task_progress':
            handleTaskProgress(frame);
            break;
        case 'token_telemetry':
            handleTokenTelemetry(frame);
            break;
        case 'smart_approval_prompt':
            handleSmartApprovalPrompt(frame);
            break;
        case 'terminal_output':
            handleTerminalOutput(frame);
            break;
        case 'ghost_frame':
            handleGhostFrame(frame);
            break;
        case 'ghost_complete':
            handleGhostComplete(frame);
            break;
        case 'pong':
            // Heartbeat ACK
            break;
        default:
            appendLog('info', `WS: ${type}`);
    }
}

// ============================================================================
// WebSocket Event Handlers
// ============================================================================
function handleStreamChunk(frame) {
    const stream = document.getElementById('chat-stream-container');
    if (!stream) return;

    let chunkHolder = document.getElementById(`chunk-stream-${frame.taskId}`);
    if (!chunkHolder) {
        const msgDiv = document.createElement('div');
        msgDiv.className = 'chat-msg assistant';
        msgDiv.innerHTML = `
            <div class="chat-bubble" id="chunk-stream-${frame.taskId}"></div>
        `;
        stream.appendChild(msgDiv);
        chunkHolder = document.getElementById(`chunk-stream-${frame.taskId}`);
    }

    if (chunkHolder) {
        chunkHolder.textContent += frame.chunk;
        stream.scrollTop = stream.scrollHeight;
    }

    if (frame.isFinal) {
        refreshTasks();
    }
}

function handleToolStarted(frame) {
    const stream = document.getElementById('chat-stream-container');
    if (!stream) return;

    const cardId = `tool-card-${frame.taskId}-${frame.toolName}`;
    let card = document.getElementById(cardId);
    if (!card) {
        card = document.createElement('div');
        card.className = 'tool-card running';
        card.id = cardId;
        card.innerHTML = `
            <div class="tool-card-header" onclick="this.parentElement.classList.toggle('collapsed')">
                <span>⚙️ <strong>${escapeHtml(frame.toolName)}</strong></span>
                <span class="pill pill-yellow">Executing...</span>
            </div>
            <div class="tool-card-body">
                <div><em>Input:</em> ${escapeHtml(frame.input)}</div>
            </div>
        `;
        stream.appendChild(card);
        stream.scrollTop = stream.scrollHeight;
    }
    appendLog('info', `Tool started: ${frame.toolName}`);
}

function handleToolProgress(frame) {
    const cardId = `tool-card-${frame.taskId}-${frame.toolName}`;
    const card = document.getElementById(cardId);
    if (card) {
        const body = card.querySelector('.tool-card-body');
        if (body) {
            body.innerHTML += `<div>Progress: ${frame.progress}% — ${escapeHtml(frame.message)}</div>`;
        }
    }
}

function handleToolCompleted(frame) {
    const cardId = `tool-card-${frame.taskId}-${frame.toolName}`;
    const card = document.getElementById(cardId);
    if (card) {
        card.className = 'tool-card success';
        const header = card.querySelector('.tool-card-header');
        if (header) {
            header.innerHTML = `
                <span>✓ <strong>${escapeHtml(frame.toolName)}</strong> (${frame.durationMs}ms)</span>
                <span class="pill pill-green">Success</span>
            `;
        }
        const body = card.querySelector('.tool-card-body');
        if (body) {
            body.innerHTML += `<div><em>Output:</em> <pre>${escapeHtml(frame.output)}</pre></div>`;
        }
    }
    appendLog('info', `Tool completed: ${frame.toolName} in ${frame.durationMs}ms`);
}

function handleToolFailed(frame) {
    const cardId = `tool-card-${frame.taskId}-${frame.toolName}`;
    const card = document.getElementById(cardId);
    if (card) {
        card.className = 'tool-card failed';
        const header = card.querySelector('.tool-card-header');
        if (header) {
            header.innerHTML = `
                <span>✕ <strong>${escapeHtml(frame.toolName)}</strong></span>
                <span class="pill pill-red">Failed</span>
            `;
        }
        const body = card.querySelector('.tool-card-body');
        if (body) {
            body.innerHTML += `<div style="color:var(--red-danger)"><em>Error:</em> ${escapeHtml(frame.error)}</div>`;
        }
    }
    appendLog('error', `Tool failed: ${frame.toolName} — ${frame.error}`);
}

function handleReasoning(frame) {
    const stream = document.getElementById('chat-stream-container');
    if (!stream) return;

    let thoughtCard = document.getElementById(`reasoning-${frame.taskId}`);
    if (!thoughtCard) {
        thoughtCard = document.createElement('div');
        thoughtCard.className = 'reasoning-card';
        thoughtCard.id = `reasoning-${frame.taskId}`;
        thoughtCard.innerHTML = `
            <div class="reasoning-header" onclick="this.nextElementSibling.classList.toggle('hidden')">
                🧠 <strong>Reasoning Process</strong> <small>(click to collapse/expand)</small>
            </div>
            <div class="reasoning-content">${escapeHtml(frame.thoughts)}</div>
        `;
        stream.appendChild(thoughtCard);
    } else {
        const content = thoughtCard.querySelector('.reasoning-content');
        if (content) content.textContent += ' ' + frame.thoughts;
    }
    stream.scrollTop = stream.scrollHeight;
}

function handleTaskProgress(frame) {
    // Update live workflow tab
    const title = document.getElementById('workflow-task-title');
    const pill = document.getElementById('workflow-status-pill');
    const fill = document.getElementById('workflow-progress-fill');
    const pct = document.getElementById('workflow-progress-percent');

    if (title) title.textContent = `Task ${frame.taskId}: ${frame.currentStep}`;
    if (pill) {
        pill.textContent = frame.status;
        pill.className = `pill ${frame.status === 'Completed' ? 'pill-green' : frame.status === 'Cancelled' ? 'pill-red' : 'pill-yellow'}`;
    }
    if (fill) fill.style.width = `${frame.progressPercent}%`;
    if (pct) pct.textContent = `${frame.progressPercent}%`;

    // Update 5-Phase DAG steps visualizer
    const stepIdx = Math.min(4, Math.floor(frame.progressPercent / 20));
    for (let i = 0; i < 5; i++) {
        const stepEl = document.getElementById(`dag-step-${i}`);
        if (!stepEl) continue;
        if (i < stepIdx || frame.status === 'Completed') {
            stepEl.className = 'dag-step completed';
            stepEl.querySelector('.dag-step-status').textContent = 'Done';
        } else if (i === stepIdx && frame.status === 'Running') {
            stepEl.className = 'dag-step active';
            stepEl.querySelector('.dag-step-status').textContent = 'In Progress';
        } else {
            stepEl.className = 'dag-step pending';
            stepEl.querySelector('.dag-step-status').textContent = 'Pending';
        }
    }

    // Append to step logs
    const logContainer = document.getElementById('workflow-steps-log');
    if (logContainer) {
        const item = document.createElement('div');
        item.className = 'code-badge';
        item.textContent = `[${new Date().toLocaleTimeString()}] (${frame.progressPercent}%) ${frame.currentStep} - ${frame.message}`;
        logContainer.appendChild(item);
    }

    // Update right panel task metadata
    const metaId = document.getElementById('meta-task-id');
    const metaStatus = document.getElementById('meta-task-status');
    if (metaId) metaId.textContent = frame.taskId;
    if (metaStatus) metaStatus.textContent = `${frame.status} (${frame.progressPercent}%)`;
}

function handleTokenTelemetry(frame) {
    state.telemetry.promptTokens = frame.promptTokens;
    state.telemetry.completionTokens = frame.completionTokens;
    state.telemetry.totalTokens = frame.totalTokens;
    state.telemetry.cost = frame.estimatedCost;

    const elPrompt = document.getElementById('telemetry-prompt-tokens');
    const elComp = document.getElementById('telemetry-completion-tokens');
    const elTotal = document.getElementById('telemetry-total-tokens');
    const elCost = document.getElementById('telemetry-estimated-cost');
    const elGauge = document.getElementById('telemetry-gauge-percent');
    const elFill = document.getElementById('telemetry-gauge-fill');

    if (elPrompt) elPrompt.textContent = frame.promptTokens.toLocaleString();
    if (elComp) elComp.textContent = frame.completionTokens.toLocaleString();
    if (elTotal) elTotal.textContent = frame.totalTokens.toLocaleString();
    if (elCost) elCost.textContent = `$${frame.estimatedCost.toFixed(4)}`;

    const contextPercent = Math.min(100, (frame.totalTokens / 128000) * 100);
    if (elGauge) elGauge.textContent = `${contextPercent.toFixed(1)}%`;
    if (elFill) elFill.style.width = `${contextPercent}%`;
}

function handleSmartApprovalPrompt(frame) {
    state.smartApprovalPending = frame;
    showSmartApprovalModal(frame);
}

function handleTerminalOutput(frame) {
    const consoleEl = document.getElementById('terminal-console-output');
    if (!consoleEl) return;

    const line = document.createElement('div');
    line.className = `term-line ${frame.isError ? 'term-stderr' : 'term-stdout'}`;
    line.textContent = frame.output;
    consoleEl.appendChild(line);
    consoleEl.scrollTop = consoleEl.scrollHeight;
}

function handleGhostFrame(frame) {
    const banner = document.getElementById('ghost-typing-banner');
    const text = document.getElementById('ghost-preview-text');
    if (banner) banner.classList.remove('hidden');
    if (text) text.textContent = frame.text;
}

function handleGhostComplete(frame) {
    const banner = document.getElementById('ghost-typing-banner');
    if (banner) banner.classList.add('hidden');
    refreshChatTranscript();
}

// ============================================================================
// Core Module Loaders & Operations
// ============================================================================

async function refreshStatus() {
    try {
        const data = await api.get('/api/status');
        state.status = data;

        const pulse = document.getElementById('header-pulse-dot');
        if (pulse) {
            pulse.style.backgroundColor = data.panicTriggered ? 'var(--red-danger)' : 'var(--green-success)';
        }

        const agentHealth = document.getElementById('agent-health-label');
        const agentDot = document.getElementById('agent-health-dot');
        if (agentHealth) {
            agentHealth.textContent = data.panicTriggered ? 'PANIC STOPPED' : 'Engine Online';
        }
        if (agentDot) {
            agentDot.className = `status-indicator-dot ${data.panicTriggered ? 'error' : 'online'}`;
        }
    } catch (e) {
        console.warn('Status refresh error:', e);
    }
}

async function refreshSessions() {
    try {
        const sessions = await api.get('/api/sessions');
        state.sessions = sessions;

        // Populate header dropdown
        const select = document.getElementById('header-session-select');
        if (select) {
            select.innerHTML = sessions.map(s => `
                <option value="${s.id}" ${s.isActive ? 'selected' : ''}>
                    ${escapeHtml(s.title || s.name || s.id)} (${s.model})
                </option>
            `).join('');
            if (sessions.length === 0) {
                select.innerHTML = '<option value="">No Active Sessions</option>';
            }
        }

        // Populate sidebar sessions list
        const list = document.getElementById('sessions-list');
        if (list) {
            if (sessions.length === 0) {
                list.innerHTML = '<div class="empty-placeholder">No sessions created yet</div>';
            } else {
                list.innerHTML = sessions.map(s => `
                    <div class="sidebar-card ${s.isActive ? 'active-border' : ''}">
                        <div style="display:flex; justify-content:space-between; align-items:center;">
                            <strong>${escapeHtml(s.title || s.name || s.id)}</strong>
                            <span class="pill ${s.isActive ? 'pill-green' : 'pill-grey'}">${s.isActive ? 'Active' : 'Idle'}</span>
                        </div>
                        <div style="font-size:11px; color:var(--text-muted);">
                            Model: ${s.model} | Msgs: ${s.messageCount || 0}
                        </div>
                        <div style="display:flex; gap:6px; margin-top:4px;">
                            <button class="btn-xs btn-primary" onclick="activateSession('${s.id}')">Activate</button>
                            <button class="btn-xs btn-secondary" onclick="loadSessionDetail('${s.id}')">Transcript</button>
                            <button class="btn-xs btn-danger" onclick="deleteSession('${s.id}')">Delete</button>
                        </div>
                    </div>
                `).join('');
            }
        }
    } catch (e) {
        console.warn('Sessions refresh error:', e);
    }
}

async function createNewSession(title, model) {
    try {
        const created = await api.post('/api/sessions', { title, model });
        appendLog('info', `Created new session: ${created.id}`);
        await refreshSessions();
        await activateSession(created.id);
    } catch (e) {
        alert('Failed to create session: ' + e.message);
    }
}

async function activateSession(id) {
    try {
        await api.post(`/api/sessions/${id}/activate`);
        state.activeSessionId = id;
        appendLog('info', `Activated session ${id}`);
        await refreshSessions();
        await loadSessionDetail(id);
    } catch (e) {
        console.error('Failed to activate session:', e);
    }
}

async function deleteSession(id) {
    if (!confirm(`Delete session '${id}'?`)) return;
    try {
        await api.delete(`/api/sessions/${id}`);
        appendLog('info', `Deleted session ${id}`);
        await refreshSessions();
    } catch (e) {
        alert('Failed to delete session: ' + e.message);
    }
}

async function loadSessionDetail(id) {
    try {
        const detail = await api.get(`/api/sessions/${id}`);
        state.activeSession = detail;

        // Render history transcript
        const historyList = document.getElementById('history-list');
        if (historyList && detail.transcript) {
            historyList.innerHTML = detail.transcript.map(msg => `
                <div class="sidebar-card">
                    <div style="display:flex; justify-content:space-between; font-size:10px; color:var(--text-muted);">
                        <strong>[${msg.senderRole || msg.role}]</strong>
                        <span>${new Date(msg.timestamp).toLocaleTimeString()}</span>
                    </div>
                    <div style="font-size:12px; margin-top:4px; white-space:pre-wrap;">${escapeHtml(msg.content)}</div>
                </div>
            `).join('');
        }
    } catch (e) {
        console.error('Failed to load session details:', e);
    }
}

// Tasks Subsystem
async function refreshTasks() {
    try {
        const tasks = await api.get('/api/tasks');
        state.tasks = tasks;

        const activeList = document.getElementById('active-tasks-list');
        const historyList = document.getElementById('task-history-list');
        const countBadge = document.getElementById('tasks-count-badge');

        const active = tasks.filter(t => t.status === 'Running' || t.status === 'Pending' || t.status === 'Paused');
        const completed = tasks.filter(t => t.status === 'Completed' || t.status === 'Cancelled' || t.status === 'Failed');

        if (countBadge) countBadge.textContent = `${active.length} Active`;

        if (activeList) {
            if (active.length === 0) {
                activeList.innerHTML = '<div class="empty-placeholder">No active tasks running</div>';
            } else {
                activeList.innerHTML = active.map(t => `
                    <div class="sidebar-card">
                        <div style="display:flex; justify-content:space-between; align-items:center;">
                            <strong>${escapeHtml(t.id)}</strong>
                            <span class="pill ${t.status === 'Running' ? 'pill-yellow' : 'pill-grey'}">${t.status}</span>
                        </div>
                        <div style="font-size:11px; margin-top:2px;">${escapeHtml(t.prompt.substring(0, 60))}...</div>
                        <div class="progress-bar-track" style="margin-top:6px;">
                            <div class="progress-bar-fill" style="width: ${t.progressPercent}%"></div>
                        </div>
                        <div style="display:flex; gap:6px; margin-top:8px;">
                            ${t.status === 'Running'
                                ? `<button class="btn-xs btn-secondary" onclick="pauseTask('${t.id}')">Pause</button>`
                                : `<button class="btn-xs btn-primary" onclick="resumeTask('${t.id}')">Resume</button>`}
                            <button class="btn-xs btn-danger" onclick="cancelTask('${t.id}')">Cancel</button>
                        </div>
                    </div>
                `).join('');
            }
        }

        if (historyList) {
            if (completed.length === 0) {
                historyList.innerHTML = '<div class="empty-placeholder">No completed tasks yet</div>';
            } else {
                historyList.innerHTML = completed.map(t => `
                    <div class="sidebar-card">
                        <div style="display:flex; justify-content:space-between; align-items:center;">
                            <strong>${escapeHtml(t.id)}</strong>
                            <span class="pill ${t.status === 'Completed' ? 'pill-green' : 'pill-red'}">${t.status}</span>
                        </div>
                        <div style="font-size:11px; color:var(--text-muted); margin-top:2px;">
                            Tokens: ${t.tokensConsumed} | Completed: ${t.completedAt ? new Date(t.completedAt).toLocaleTimeString() : '—'}
                        </div>
                        <div style="font-size:11px; margin-top:4px;">${escapeHtml(t.prompt.substring(0, 70))}...</div>
                    </div>
                `).join('');
            }
        }
    } catch (e) {
        console.warn('Tasks refresh error:', e);
    }
}

async function dispatchTask(prompt, model, autonomyTier) {
    try {
        const payload = {
            prompt,
            model,
            sessionId: state.activeSessionId,
            toolPermissions: ['terminal', 'filesystem', 'git', 'web', 'cdp', 'subagents']
        };
        const task = await api.post('/api/tasks', payload);
        appendLog('info', `Dispatched autonomous task: ${task.id}`);
        state.activeTaskId = task.id;

        // Switch to Workflow Tab to observe live DAG
        switchWorkspaceTab('workflow');
        await refreshTasks();
    } catch (e) {
        alert('Failed to dispatch task: ' + e.message);
    }
}

async function pauseTask(id) {
    try {
        await api.post(`/api/tasks/${id}/pause`);
        await refreshTasks();
    } catch (e) {
        console.error('Failed to pause task:', e);
    }
}

async function resumeTask(id) {
    try {
        await api.post(`/api/tasks/${id}/resume`);
        await refreshTasks();
    } catch (e) {
        console.error('Failed to resume task:', e);
    }
}

async function cancelTask(id) {
    try {
        await api.post(`/api/tasks/${id}/cancel`);
        await refreshTasks();
    } catch (e) {
        console.error('Failed to cancel task:', e);
    }
}

// File Explorer Subsystem
async function refreshFileTree(dirPath = '') {
    try {
        const res = await api.get(`/api/fs/tree?path=${encodeURIComponent(dirPath)}`);
        state.fileTree = res.items || [];

        const currentPath = document.getElementById('workspace-current-path');
        if (currentPath) currentPath.textContent = res.root || 'Workspace Root';

        const treeContainer = document.getElementById('fs-tree-container');
        if (treeContainer) {
            treeContainer.innerHTML = renderTreeNodes(res.items || []);
        }
    } catch (e) {
        console.warn('File tree refresh error:', e);
    }
}

function renderTreeNodes(nodes) {
    if (!nodes || nodes.length === 0) return '<div class="empty-placeholder">Empty directory</div>';

    return nodes.map(n => {
        const isDir = n.isDirectory;
        const icon = isDir ? '📁' : '📄';
        const gitStatusBadge = n.gitStatus && n.gitStatus !== 'unmodified'
            ? `<span class="pill pill-yellow" style="font-size:9px;">${n.gitStatus}</span>`
            : '';

        if (isDir) {
            return `
                <div class="tree-node" onclick="this.nextElementSibling.classList.toggle('hidden')">
                    <span>${icon} <strong>${escapeHtml(n.name)}</strong></span>
                </div>
                <div class="tree-children" style="padding-left:14px;">
                    ${renderTreeNodes(n.children)}
                </div>
            `;
        } else {
            return `
                <div class="tree-node ${n.gitStatus === 'modified' ? 'file-modified' : ''}" onclick="loadFileToEditor('${escapeHtml(n.path)}')">
                    <span>${icon} ${escapeHtml(n.name)}</span>
                    ${gitStatusBadge}
                </div>
            `;
        }
    }).join('');
}

async function loadFileToEditor(filePath) {
    try {
        const res = await api.get(`/api/fs/read?path=${encodeURIComponent(filePath)}`);
        state.editorFile = { path: res.path, content: res.content, dirty: false };

        const activeFile = document.getElementById('editor-active-file');
        const pathInput = document.getElementById('editor-path-input');
        const textarea = document.getElementById('code-editor-textarea');
        const dirtyBadge = document.getElementById('editor-dirty-badge');

        if (activeFile) activeFile.textContent = res.path;
        if (pathInput) pathInput.value = res.path;
        if (textarea) textarea.value = res.content;
        if (dirtyBadge) dirtyBadge.classList.add('hidden');

        updateLineNumbers(res.content);
        switchWorkspaceTab('editor');
    } catch (e) {
        alert('Failed to read file: ' + e.message);
    }
}

async function saveEditorFile() {
    if (!state.editorFile.path) {
        const pathInput = document.getElementById('editor-path-input');
        if (pathInput && pathInput.value.trim()) {
            state.editorFile.path = pathInput.value.trim();
        } else {
            alert('Please specify a file path to save.');
            return;
        }
    }

    const textarea = document.getElementById('code-editor-textarea');
    const content = textarea ? textarea.value : '';

    try {
        await api.post('/api/fs/write', {
            path: state.editorFile.path,
            content: content,
            overwrite: true
        });
        state.editorFile.dirty = false;
        const dirtyBadge = document.getElementById('editor-dirty-badge');
        if (dirtyBadge) dirtyBadge.classList.add('hidden');
        appendLog('info', `Saved file: ${state.editorFile.path}`);
        await refreshFileTree();
    } catch (e) {
        alert('Failed to save file: ' + e.message);
    }
}

function updateLineNumbers(text) {
    const gutter = document.getElementById('editor-line-numbers');
    if (!gutter) return;
    const lineCount = (text || '').split('\n').length;
    let lines = '';
    for (let i = 1; i <= Math.max(1, lineCount); i++) {
        lines += i + '\n';
    }
    gutter.textContent = lines;
}

// Diff Engine Subsystem
async function loadDiff(filePath) {
    try {
        const diff = await api.get(`/api/fs/diff?path=${encodeURIComponent(filePath)}`);
        renderDiff(diff);
    } catch (e) {
        alert('Failed to load diff: ' + e.message);
    }
}

async function computeCustomDiff(filePath, proposedContent) {
    try {
        const diff = await api.post('/api/fs/diff', {
            path: filePath,
            proposedContent: proposedContent
        });
        renderDiff(diff);
    } catch (e) {
        alert('Failed to compute diff: ' + e.message);
    }
}

function renderDiff(diff) {
    state.activeDiff = diff;
    const container = document.getElementById('diff-viewer-container');
    const title = document.getElementById('diff-file-title');
    const addBadge = document.getElementById('diff-add-badge');
    const delBadge = document.getElementById('diff-del-badge');

    if (title) title.textContent = `Diff: ${diff.filePath || 'File'}`;
    if (addBadge) addBadge.textContent = `+${diff.additionsCount || 0}`;
    if (delBadge) delBadge.textContent = `-${diff.deletionsCount || 0}`;

    if (!container) return;

    if (!diff.hasChanges && (!diff.unifiedDiff || diff.unifiedDiff.trim() === '')) {
        container.innerHTML = '<div class="empty-placeholder">No modifications detected. Contents are identical.</div>';
        return;
    }

    const lines = (diff.unifiedDiff || '').split('\n');
    container.innerHTML = lines.map(line => {
        let type = 'context';
        if (line.startsWith('+++') || line.startsWith('---') || line.startsWith('@@')) {
            type = 'hunk';
        } else if (line.startsWith('+')) {
            type = 'add';
        } else if (line.startsWith('-')) {
            type = 'del';
        }
        return `<div class="diff-line ${type}">${escapeHtml(line)}</div>`;
    }).join('');

    switchWorkspaceTab('diff');
}

async function approveActiveDiff() {
    if (!state.activeDiff) {
        alert('No active diff to approve.');
        return;
    }

    try {
        const res = await api.post('/api/fs/diff/approve', {
            path: state.activeDiff.filePath,
            proposedContent: state.activeDiff.newContent
        });
        appendLog('info', `Diff approved and applied to ${state.activeDiff.filePath}`);
        alert(`Changes applied successfully! (+${res.additions}/-${res.deletions})`);
        state.activeDiff = null;
        await refreshFileTree();
    } catch (e) {
        alert('Failed to apply diff: ' + e.message);
    }
}

async function rejectActiveDiff() {
    if (!state.activeDiff) {
        alert('No active diff to reject.');
        return;
    }

    try {
        await api.post('/api/fs/diff/reject', { path: state.activeDiff.filePath });
        appendLog('warn', `Diff rejected for ${state.activeDiff.filePath}`);
        state.activeDiff = null;
        const container = document.getElementById('diff-viewer-container');
        if (container) container.innerHTML = '<div class="empty-placeholder">Diff rejected and cleared.</div>';
    } catch (e) {
        alert('Failed to reject diff: ' + e.message);
    }
}

// Terminal Execution Subsystem
async function runTerminalCommand(command, approved = false) {
    const consoleEl = document.getElementById('terminal-console-output');
    if (!consoleEl) return;

    // Display user command in console
    const cmdLine = document.createElement('div');
    cmdLine.className = 'term-line term-system';
    cmdLine.textContent = `> ${command}`;
    consoleEl.appendChild(cmdLine);

    try {
        const backend = document.getElementById('terminal-backend-select')?.value || 'Local';
        const res = await api.post('/api/terminal/exec', {
            command: command,
            sessionId: state.activeSessionId,
            approved: approved
        });

        if (res.stdout) {
            const outLine = document.createElement('div');
            outLine.className = 'term-line term-stdout';
            outLine.textContent = res.stdout;
            consoleEl.appendChild(outLine);
        }

        if (res.stderr) {
            const errLine = document.createElement('div');
            errLine.className = 'term-line term-stderr';
            errLine.textContent = res.stderr;
            consoleEl.appendChild(errLine);
        }

        appendLog('info', `Terminal exit code: ${res.exitCode} (${res.executionTimeMs}ms)`);
        consoleEl.scrollTop = consoleEl.scrollHeight;
    } catch (err) {
        if (err.status === 403 && err.data && err.data.approvalRequired) {
            // Interactive approval required
            showSmartApprovalModal({
                requestId: err.data.requestId,
                actionName: 'terminal',
                tier: err.data.riskTier || 'Red',
                description: `Terminal command: ${command}`,
                commandPayload: command
            });
        } else {
            const errLine = document.createElement('div');
            errLine.className = 'term-line term-stderr';
            errLine.textContent = `Error: ${err.message}`;
            consoleEl.appendChild(errLine);
            consoleEl.scrollTop = consoleEl.scrollHeight;
        }
    }
}

// Skills Hub Subsystem
async function refreshSkills() {
    try {
        const data = await api.get('/api/skills');
        state.skills = data;

        const list = document.getElementById('installed-skills-list');
        if (list) {
            if (!data.installed || data.installed.length === 0) {
                list.innerHTML = '<div class="empty-placeholder">No skills installed</div>';
            } else {
                list.innerHTML = data.installed.map(s => `
                    <div class="sidebar-card">
                        <div style="display:flex; justify-content:space-between; align-items:center;">
                            <strong>${escapeHtml(s.name)}</strong>
                            <span class="pill pill-green">Verified AST</span>
                        </div>
                        <div style="font-size:11px; color:var(--text-muted);">${escapeHtml(s.description || 'Custom Agent Skill')}</div>
                    </div>
                `).join('');
            }
        }
    } catch (e) {
        console.warn('Skills refresh error:', e);
    }
}

async function installSkill(repoOrUrl) {
    try {
        appendLog('info', `Scanning & installing skill from ${repoOrUrl}...`);
        const installed = await api.post('/api/skills/install', { repoOrUrl });
        alert(`Skill '${installed.name || repoOrUrl}' installed successfully after AST verification!`);
        await refreshSkills();
    } catch (e) {
        alert('Skill installation failed: ' + e.message);
    }
}

// Kanban Subsystem
async function refreshKanban() {
    try {
        const cards = await api.get('/api/kanban');
        state.kanbanCards = cards;

        const cols = {
            Pending: document.getElementById('kanban-col-pending'),
            InProgress: document.getElementById('kanban-col-inprogress'),
            Blocked: document.getElementById('kanban-col-blocked'),
            Done: document.getElementById('kanban-col-done')
        };

        Object.values(cols).forEach(c => { if (c) c.innerHTML = ''; });

        cards.forEach(card => {
            const colEl = cols[card.column] || cols.Pending;
            if (colEl) {
                const item = document.createElement('div');
                item.className = 'kanban-card-item';
                item.innerHTML = `
                    <strong>${escapeHtml(card.title)}</strong>
                    <div style="font-size:10px; color:var(--text-muted);">${escapeHtml(card.description || '')}</div>
                    <div style="display:flex; gap:4px; margin-top:4px;">
                        <button class="btn-xs btn-secondary" onclick="moveKanbanCard('${card.id}', 'Pending')">P</button>
                        <button class="btn-xs btn-secondary" onclick="moveKanbanCard('${card.id}', 'InProgress')">IP</button>
                        <button class="btn-xs btn-secondary" onclick="moveKanbanCard('${card.id}', 'Blocked')">B</button>
                        <button class="btn-xs btn-secondary" onclick="moveKanbanCard('${card.id}', 'Done')">D</button>
                    </div>
                `;
                colEl.appendChild(item);
            }
        });
    } catch (e) {
        console.warn('Kanban refresh error:', e);
    }
}

async function createKanbanCard(title, description) {
    try {
        await api.post('/api/kanban', { title, description });
        await refreshKanban();
    } catch (e) {
        alert('Failed to add Kanban card: ' + e.message);
    }
}

async function moveKanbanCard(cardId, column) {
    try {
        await api.post('/api/kanban/move', { cardId, column });
        await refreshKanban();
    } catch (e) {
        console.error('Failed to move Kanban card:', e);
    }
}

// Natural Cron Subsystem
async function refreshCron() {
    try {
        const jobs = await api.get('/api/cron');
        state.cronJobs = jobs;

        const list = document.getElementById('cron-jobs-list');
        if (list) {
            if (jobs.length === 0) {
                list.innerHTML = '<div class="empty-placeholder">No scheduled jobs registered</div>';
            } else {
                list.innerHTML = jobs.map(j => `
                    <div class="sidebar-card">
                        <div style="display:flex; justify-content:space-between; align-items:center;">
                            <strong>${escapeHtml(j.name)}</strong>
                            <span class="pill pill-green">${j.status}</span>
                        </div>
                        <div style="font-size:11px; color:var(--text-secondary); margin-top:2px;">
                            ${escapeHtml(j.naturalSchedule)} <small>(${escapeHtml(j.cronExpression)})</small>
                        </div>
                        <div style="font-size:10px; color:var(--text-muted); margin-top:2px;">
                            Next run: ${j.nextRun ? new Date(j.nextRun).toLocaleTimeString() : '—'}
                        </div>
                    </div>
                `).join('');
            }
        }
    } catch (e) {
        console.warn('Cron refresh error:', e);
    }
}

async function scheduleCronJob(name, schedulePhrase, prompt) {
    try {
        const job = await api.post('/api/cron', { name, schedulePhrase, prompt });
        alert(`Job '${job.name}' registered for natural schedule: ${job.naturalSchedule}`);
        await refreshCron();
    } catch (e) {
        alert('Failed to schedule job: ' + e.message);
    }
}

// Persistent Memory Subsystem
async function refreshMemory() {
    try {
        const data = await api.get('/api/memory');
        state.memoryDocs = data;

        const titleEl = document.getElementById('memory-current-title');
        if (titleEl) titleEl.textContent = `${state.activeMemoryKind.toUpperCase()}.md`;
    } catch (e) {
        console.warn('Memory refresh error:', e);
    }
}

async function appendMemoryEntry(kind, title, content) {
    try {
        await api.post('/api/memory', { kind, title, content });
        alert(`Appended entry '${title}' to ${kind}.md!`);
        await refreshMemory();
    } catch (e) {
        alert('Failed to append memory: ' + e.message);
    }
}

async function searchMemory(query) {
    const resultsContainer = document.getElementById('memory-search-results');
    if (!resultsContainer) return;

    if (!query.trim()) {
        resultsContainer.classList.add('hidden');
        return;
    }

    try {
        const hits = await api.get(`/api/memory/search?q=${encodeURIComponent(query)}`);
        resultsContainer.classList.remove('hidden');

        if (hits.length === 0) {
            resultsContainer.innerHTML = '<div class="empty-placeholder">No memory hits found</div>';
        } else {
            resultsContainer.innerHTML = hits.map(h => `
                <div class="sidebar-card">
                    <div style="display:flex; justify-content:space-between; font-size:10px;">
                        <strong>${escapeHtml(h.category || 'Memory')}</strong>
                        <span class="pill pill-grey">BM25: ${h.bm25Score ? h.bm25Score.toFixed(2) : '0'}</span>
                    </div>
                    <div style="font-weight:600; font-size:11px; margin-top:2px;">${escapeHtml(h.title || 'Entry')}</div>
                    <div style="font-size:11px; color:var(--text-secondary); margin-top:2px;">${escapeHtml(h.content ? h.content.substring(0, 100) : '')}...</div>
                </div>
            `).join('');
        }
    } catch (e) {
        console.error('Memory search failed:', e);
    }
}

// Integrations Subsystem
async function refreshIntegrations() {
    try {
        const channels = await api.get('/api/integrations');
        state.integrations = channels;

        const list = document.getElementById('integrations-list');
        if (list) {
            list.innerHTML = channels.map(ch => `
                <div class="sidebar-card">
                    <div style="display:flex; justify-content:space-between; align-items:center;">
                        <strong>${escapeHtml(ch.channel.toUpperCase())}</strong>
                        <span class="pill ${ch.configured ? 'pill-green' : 'pill-yellow'}">
                            ${ch.configured ? 'Live' : 'Mock Mode'}
                        </span>
                    </div>
                    <div style="font-size:11px; color:var(--text-muted); margin-top:2px;">
                        Status: Connected | Mode: ${ch.mode}
                    </div>
                </div>
            `).join('');
        }
    } catch (e) {
        console.warn('Integrations refresh error:', e);
    }
}

async function testIntegrationChannel(channel, message) {
    try {
        const res = await api.post('/api/integrations/test', { channel, message });
        alert(`Integration test message sent to '${channel}': ${res.details}`);
    } catch (e) {
        alert('Integration test failed: ' + e.message);
    }
}

// Settings Subsystem
async function refreshSettings() {
    try {
        const s = await api.get('/api/settings');
        state.settings = s;

        const defModel = document.getElementById('settings-default-model');
        const fallback = document.getElementById('settings-fallback-model');
        const keyGemini = document.getElementById('settings-key-gemini');
        const keyOpenAi = document.getElementById('settings-key-openai');
        const keyAnthropic = document.getElementById('settings-key-anthropic');
        const keyOpenRouter = document.getElementById('settings-key-openrouter');
        const keyNous = document.getElementById('settings-key-nous');
        const ollama = document.getElementById('settings-ollama-endpoint');
        const autonomy = document.getElementById('settings-autonomy-tier');
        const reqRed = document.getElementById('settings-require-red');

        if (defModel && s.models) defModel.value = s.models.defaultModel || 'gemini-2.5-flash';
        if (fallback && s.models) fallback.value = s.models.fallbackModel || 'offline-mock';
        if (keyGemini && s.apiKeys) keyGemini.value = s.apiKeys.geminiApiKey || '';
        if (keyOpenAi && s.apiKeys) keyOpenAi.value = s.apiKeys.openAiApiKey || '';
        if (keyAnthropic && s.apiKeys) keyAnthropic.value = s.apiKeys.anthropicApiKey || '';
        if (keyOpenRouter && s.apiKeys) keyOpenRouter.value = s.apiKeys.openRouterApiKey || '';
        if (keyNous && s.apiKeys) keyNous.value = s.apiKeys.nousApiKey || '';
        if (ollama && s.apiKeys) ollama.value = s.apiKeys.ollamaEndpoint || 'http://localhost:11434';
        if (autonomy && s.permissions) autonomy.value = s.permissions.defaultAutonomyTier || 'Guarded';
        if (reqRed && s.permissions) reqRed.checked = s.permissions.requireApprovalForRed !== false;
    } catch (e) {
        console.warn('Settings refresh error:', e);
    }
}

async function saveSettingsFromForm() {
    const payload = {
        onboardingCompleted: true,
        models: {
            defaultModel: document.getElementById('settings-default-model')?.value,
            fallbackModel: document.getElementById('settings-fallback-model')?.value
        },
        apiKeys: {
            geminiApiKey: document.getElementById('settings-key-gemini')?.value,
            openAiApiKey: document.getElementById('settings-key-openai')?.value,
            anthropicApiKey: document.getElementById('settings-key-anthropic')?.value,
            openRouterApiKey: document.getElementById('settings-key-openrouter')?.value,
            nousApiKey: document.getElementById('settings-key-nous')?.value,
            ollamaEndpoint: document.getElementById('settings-ollama-endpoint')?.value
        },
        appearance: {
            theme: state.theme
        },
        permissions: {
            defaultAutonomyTier: document.getElementById('settings-autonomy-tier')?.value,
            requireApprovalForRed: document.getElementById('settings-require-red')?.checked
        }
    };

    try {
        await api.post('/api/settings', payload);
        alert('Settings saved and synchronized with runtime engine!');
        appendLog('info', 'Settings updated and LLM router hot-reloaded.');
        await refreshSettings();
    } catch (e) {
        alert('Failed to save settings: ' + e.message);
    }
}

// ============================================================================
// First-Run Setup Wizard & Live LLM Connection Testing
// ============================================================================
let activeSetupProvider = 'gemini';

async function checkOnboardingStatus() {
    try {
        const status = await api.get('/api/setup/status');
        if (status && status.onboardingCompleted === false) {
            openSetupWizard(status);
        }
    } catch (e) {
        console.warn('Setup status check failed:', e);
    }
}

async function openSetupWizard(prefetchedStatus = null) {
    const modal = document.getElementById('setup-wizard-modal');
    if (!modal) return;

    let status = prefetchedStatus;
    if (!status) {
        try {
            status = await api.get('/api/setup/status');
        } catch (e) {
            console.warn('Could not load setup status:', e);
        }
    }

    const provider = status?.preferredProvider || 'gemini';
    selectSetupProvider(provider, status?.defaultModel);

    if (status?.settings) {
        const s = status.settings;
        const ollamaInput = document.getElementById('setup-ollama-endpoint-input');
        if (ollamaInput && s.apiKeys?.ollamaEndpoint) ollamaInput.value = s.apiKeys.ollamaEndpoint;

        const workDirInput = document.getElementById('setup-working-dir');
        if (workDirInput && s.defaultWorkingDir) workDirInput.value = s.defaultWorkingDir;

        const tierSelect = document.getElementById('setup-autonomy-tier');
        if (tierSelect && s.permissions?.defaultAutonomyTier) tierSelect.value = s.permissions.defaultAutonomyTier;

        const themeSelect = document.getElementById('setup-theme');
        if (themeSelect && s.appearance?.theme) themeSelect.value = s.appearance.theme;
    }

    const banner = document.getElementById('setup-test-result');
    if (banner) {
        banner.className = 'setup-test-banner hidden';
        banner.textContent = '';
    }

    modal.classList.remove('hidden');
}

function closeSetupWizard() {
    const modal = document.getElementById('setup-wizard-modal');
    if (modal) modal.classList.add('hidden');
}

function selectSetupProvider(provider, explicitModel = null) {
    activeSetupProvider = (provider || 'gemini').toLowerCase();

    const cards = document.querySelectorAll('.setup-provider-card');
    let defaultModelForProvider = 'gemini-2.5-flash';

    cards.forEach(card => {
        const p = card.getAttribute('data-provider');
        const isMatch = p === activeSetupProvider;
        card.classList.toggle('active', isMatch);
        if (isMatch) {
            defaultModelForProvider = card.getAttribute('data-default-model') || defaultModelForProvider;
        }
    });

    const apiKeyGroup = document.getElementById('setup-apikey-group');
    const ollamaGroup = document.getElementById('setup-ollama-group');
    const keyLabel = document.getElementById('setup-api-key-label');
    const keyInput = document.getElementById('setup-api-key-input');

    if (activeSetupProvider === 'ollama') {
        apiKeyGroup?.classList.add('hidden');
        ollamaGroup?.classList.remove('hidden');
    } else {
        apiKeyGroup?.classList.remove('hidden');
        ollamaGroup?.classList.add('hidden');
        if (keyLabel) keyLabel.textContent = `${activeSetupProvider.toUpperCase()} API Key`;
        if (keyInput) keyInput.value = '';
    }

    const modelInput = document.getElementById('setup-default-model');
    if (modelInput) {
        modelInput.value = explicitModel || defaultModelForProvider;
    }
}

async function testSetupLlmConnection() {
    const banner = document.getElementById('setup-test-result');
    const apiKey = document.getElementById('setup-api-key-input')?.value?.trim();
    const model = document.getElementById('setup-default-model')?.value?.trim();
    const endpointUrl = activeSetupProvider === 'ollama'
        ? document.getElementById('setup-ollama-endpoint-input')?.value?.trim()
        : null;

    if (banner) {
        banner.className = 'setup-test-banner';
        banner.textContent = `Testing live connection to ${activeSetupProvider.toUpperCase()} (${model})...`;
    }

    try {
        const res = await api.post('/api/setup/test-llm', {
            provider: activeSetupProvider,
            apiKey: apiKey || null,
            model: model || null,
            endpointUrl: endpointUrl || null
        });

        if (banner) {
            banner.className = `setup-test-banner ${res.success ? 'success' : 'error'}`;
            banner.textContent = res.success
                ? `✓ ${res.message}`
                : `✗ Connection failed (${res.latencyMs} ms): ${res.message}`;
        }
    } catch (e) {
        if (banner) {
            banner.className = 'setup-test-banner error';
            banner.textContent = `✗ Error: ${e.message}`;
        }
    }
}

async function testSettingsLlmConnection() {
    const banner = document.getElementById('settings-llm-test-result');
    const model = document.getElementById('settings-default-model')?.value || 'gemini-2.5-flash';
    let provider = 'gemini';
    let apiKey = document.getElementById('settings-key-gemini')?.value;

    if (model.startsWith('gpt') || model.startsWith('o1') || model.startsWith('o3')) {
        provider = 'openai';
        apiKey = document.getElementById('settings-key-openai')?.value;
    } else if (model.startsWith('claude')) {
        provider = 'anthropic';
        apiKey = document.getElementById('settings-key-anthropic')?.value;
    } else if (model.startsWith('openrouter')) {
        provider = 'openrouter';
        apiKey = document.getElementById('settings-key-openrouter')?.value;
    } else if (model.startsWith('nous') || model.startsWith('hermes')) {
        provider = 'nous';
        apiKey = document.getElementById('settings-key-nous')?.value;
    } else if (model.startsWith('ollama')) {
        provider = 'ollama';
        apiKey = null;
    }

    if (banner) {
        banner.className = 'setup-test-banner';
        banner.textContent = `Testing ${provider.toUpperCase()} (${model})...`;
    }

    try {
        const res = await api.post('/api/setup/test-llm', {
            provider,
            apiKey: apiKey || null,
            model,
            endpointUrl: provider === 'ollama' ? document.getElementById('settings-ollama-endpoint')?.value : null
        });

        if (banner) {
            banner.className = `setup-test-banner ${res.success ? 'success' : 'error'}`;
            banner.textContent = res.success ? `✓ ${res.message}` : `✗ ${res.message}`;
        }
    } catch (e) {
        if (banner) {
            banner.className = 'setup-test-banner error';
            banner.textContent = `✗ ${e.message}`;
        }
    }
}

async function submitSetupWizard(e) {
    if (e) e.preventDefault();

    const defaultModel = document.getElementById('setup-default-model')?.value?.trim() || 'gemini-2.5-flash';
    const apiKey = document.getElementById('setup-api-key-input')?.value?.trim() || null;
    const ollamaEndpoint = document.getElementById('setup-ollama-endpoint-input')?.value?.trim() || null;
    const workingDir = document.getElementById('setup-working-dir')?.value?.trim() || null;
    const autonomyTier = document.getElementById('setup-autonomy-tier')?.value || 'Guarded';
    const theme = document.getElementById('setup-theme')?.value || state.theme;

    try {
        await api.post('/api/setup/complete', {
            preferredProvider: activeSetupProvider,
            defaultModel,
            apiKey,
            ollamaEndpoint,
            defaultWorkingDir: workingDir,
            autonomyTier,
            theme
        });

        if (theme && theme !== state.theme) {
            toggleTheme();
        }

        const chatSelector = document.getElementById('chat-model-selector');
        if (chatSelector) chatSelector.value = defaultModel;

        const taskSelector = document.getElementById('task-model-select');
        if (taskSelector) taskSelector.value = defaultModel;

        closeSetupWizard();
        await refreshSettings();
        appendLog('info', `Setup completed: ${activeSetupProvider.toUpperCase()} (${defaultModel}) active.`);
    } catch (err) {
        alert('Setup failed to save: ' + err.message);
    }
}

// Emergency Panic Stop
async function triggerPanicStop() {
    try {
        const res = await api.post('/api/panic-stop');
        appendLog('error', 'PANIC STOP TRIGGERED: Hardware cut-off activated.');
        alert('PANIC STOP TRIGGERED! Agent execution halted.');
        await refreshStatus();
    } catch (e) {
        alert('Panic Stop failed: ' + e.message);
    }
}

// Chat Prompt Submission
async function sendChatPrompt() {
    const input = document.getElementById('chat-prompt-input');
    const modelSelect = document.getElementById('chat-model-selector');
    if (!input || !input.value.trim()) return;

    const promptText = input.value.trim();
    const model = modelSelect ? modelSelect.value : 'gemini-2.5-flash';
    input.value = '';

    const stream = document.getElementById('chat-stream-container');
    if (stream) {
        const userMsg = document.createElement('div');
        userMsg.className = 'chat-msg user';
        userMsg.innerHTML = `<div class="chat-bubble">${escapeHtml(promptText)}</div>`;
        stream.appendChild(userMsg);
        stream.scrollTop = stream.scrollHeight;
    }

    try {
        const reply = await api.post('/api/chat', { prompt: promptText, preferredModel: model });
        if (reply.assistantReply && stream) {
            const botMsg = document.createElement('div');
            botMsg.className = 'chat-msg assistant';
            botMsg.innerHTML = `<div class="chat-bubble">${formatMarkdown(reply.assistantReply.text)}</div>`;
            stream.appendChild(botMsg);
            stream.scrollTop = stream.scrollHeight;
        }
    } catch (e) {
        appendLog('error', `Chat error: ${e.message}`);
    }
}

async function refreshChatTranscript() {
    try {
        const history = await api.get('/api/chat');
        const stream = document.getElementById('chat-stream-container');
        if (!stream) return;

        // Render history items if stream is empty
        if (stream.children.length <= 1 && history.length > 0) {
            history.forEach(m => {
                const isUser = m.sender === 'local-user';
                const div = document.createElement('div');
                div.className = `chat-msg ${isUser ? 'user' : 'assistant'}`;
                div.innerHTML = `<div class="chat-bubble">${formatMarkdown(m.text)}</div>`;
                stream.appendChild(div);
            });
            stream.scrollTop = stream.scrollHeight;
        }
    } catch (e) {
        console.warn('Chat transcript refresh error:', e);
    }
}

// ============================================================================
// SmartApproval Prompt Modal Management
// ============================================================================
function showSmartApprovalModal(prompt) {
    const modal = document.getElementById('smart-approval-modal');
    if (!modal) return;

    const actionName = document.getElementById('approval-action-name');
    const desc = document.getElementById('approval-description');
    const tierBadge = document.getElementById('approval-tier-badge');
    const yellowBanner = document.getElementById('approval-yellow-banner');
    const redBanner = document.getElementById('approval-red-banner');

    if (actionName) actionName.textContent = prompt.actionName || 'terminal';
    if (desc) desc.textContent = prompt.description || 'Execution request awaiting approval';

    const isYellow = (prompt.tier || '').toLowerCase() === 'yellow';

    if (tierBadge) {
        tierBadge.textContent = isYellow ? 'Yellow Tier' : 'Red Tier';
        tierBadge.className = `pill ${isYellow ? 'pill-yellow' : 'pill-red'}`;
    }

    if (isYellow) {
        if (yellowBanner) yellowBanner.classList.remove('hidden');
        if (redBanner) redBanner.classList.add('hidden');
        startGracePeriodTimer(5.0);
    } else {
        if (yellowBanner) yellowBanner.classList.add('hidden');
        if (redBanner) redBanner.classList.remove('hidden');
        startRedTimeoutTimer(120);
    }

    modal.classList.remove('hidden');
}

function closeSmartApprovalModal() {
    const modal = document.getElementById('smart-approval-modal');
    if (modal) modal.classList.add('hidden');
    if (state.approvalTimer) {
        clearInterval(state.approvalTimer);
        state.approvalTimer = null;
    }
    state.smartApprovalPending = null;
}

function startGracePeriodTimer(seconds) {
    let remaining = seconds;
    const fill = document.getElementById('approval-timer-fill');
    const text = document.getElementById('approval-timer-seconds');

    if (state.approvalTimer) clearInterval(state.approvalTimer);
    state.approvalTimer = setInterval(() => {
        remaining -= 0.1;
        if (text) text.textContent = remaining.toFixed(1);
        if (fill) fill.style.width = `${(remaining / seconds) * 100}%`;

        if (remaining <= 0) {
            clearInterval(state.approvalTimer);
            state.approvalTimer = null;
            // Grace period auto-approval
            handleApprovalDecision(true);
        }
    }, 100);
}

function startRedTimeoutTimer(seconds) {
    let remaining = seconds;
    const fill = document.getElementById('approval-red-fill');
    const text = document.getElementById('approval-red-seconds');

    if (state.approvalTimer) clearInterval(state.approvalTimer);
    state.approvalTimer = setInterval(() => {
        remaining -= 1;
        if (text) text.textContent = remaining;
        if (fill) fill.style.width = `${(remaining / seconds) * 100}%`;

        if (remaining <= 0) {
            clearInterval(state.approvalTimer);
            state.approvalTimer = null;
            // Strict timeout denies action
            handleApprovalDecision(false);
        }
    }, 1000);
}

async function handleApprovalDecision(approved) {
    const pending = state.smartApprovalPending;
    closeSmartApprovalModal();

    if (!pending) return;

    if (pending.commandPayload) {
        // Re-execute terminal command with approved=true
        if (approved) {
            await runTerminalCommand(pending.commandPayload, true);
        } else {
            appendLog('warn', `Terminal command cancelled: ${pending.commandPayload}`);
        }
    } else if (pending.requestId) {
        try {
            await api.post(`/api/integrations/approvals/${pending.requestId}/respond`, { approved });
            appendLog('info', `Approval request ${pending.requestId} responded: ${approved ? 'Approved' : 'Denied'}`);
        } catch (e) {
            console.error('Failed to respond to approval:', e);
        }
    }
}

// ============================================================================
// Raycast-Style Global Command Palette
// ============================================================================
const paletteCommands = [
    { name: 'Run Setup Wizard / Configure Live LLM', category: 'Setup', action: () => openSetupWizard() },
    { name: 'New Autonomous Task', category: 'Tasks', action: () => { switchSidebarView('tasks'); document.getElementById('task-prompt-input')?.focus(); } },
    { name: 'New Agent Session', category: 'Sessions', action: () => { switchSidebarView('sessions'); document.getElementById('session-title-input')?.focus(); } },
    { name: 'Open File Explorer', category: 'Navigation', action: () => switchSidebarView('files') },
    { name: 'Open Terminal Console', category: 'Navigation', action: () => { switchWorkspaceTab('terminal'); document.getElementById('terminal-cmd-input')?.focus(); } },
    { name: 'View Diff & Changes', category: 'Navigation', action: () => switchWorkspaceTab('diff') },
    { name: 'Skills Hub', category: 'Navigation', action: () => switchSidebarView('skills') },
    { name: 'Subagents Kanban Board', category: 'Navigation', action: () => switchSidebarView('subagents') },
    { name: 'Scheduled Tasks (Cron)', category: 'Navigation', action: () => switchSidebarView('cron') },
    { name: 'Persistent Memory Editor', category: 'Navigation', action: () => switchSidebarView('memory') },
    { name: 'Settings & Models', category: 'Navigation', action: () => switchSidebarView('settings') },
    { name: 'Toggle Dark / Light Theme', category: 'Appearance', action: () => toggleTheme() },
    { name: 'Emergency Panic Stop', category: 'Safety', action: () => triggerPanicStop() }
];

let selectedPaletteIndex = 0;

function toggleCommandPalette() {
    const modal = document.getElementById('palette-modal-backdrop');
    if (!modal) return;

    if (modal.classList.contains('hidden')) {
        modal.classList.remove('hidden');
        const input = document.getElementById('palette-search-input');
        if (input) {
            input.value = '';
            input.focus();
            renderPaletteResults('');
        }
    } else {
        modal.classList.add('hidden');
    }
}

function renderPaletteResults(query) {
    const list = document.getElementById('palette-results-list');
    if (!list) return;

    const q = query.toLowerCase().trim();
    const filtered = paletteCommands.filter(c => c.name.toLowerCase().includes(q) || c.category.toLowerCase().includes(q));

    selectedPaletteIndex = 0;

    if (filtered.length === 0) {
        list.innerHTML = '<div class="empty-placeholder">No matching commands found</div>';
        return;
    }

    list.innerHTML = filtered.map((c, idx) => `
        <div class="palette-item ${idx === 0 ? 'selected' : ''}" data-idx="${idx}">
            <span>${escapeHtml(c.name)}</span>
            <span class="pill pill-grey">${escapeHtml(c.category)}</span>
        </div>
    `).join('');

    // Attach click listeners
    const items = list.querySelectorAll('.palette-item');
    items.forEach((item, idx) => {
        item.addEventListener('click', () => {
            filtered[idx].action();
            toggleCommandPalette();
        });
    });
}

function handlePaletteKeydown(e) {
    const list = document.getElementById('palette-results-list');
    if (!list) return;
    const items = list.querySelectorAll('.palette-item');
    if (items.length === 0) return;

    if (e.key === 'ArrowDown') {
        e.preventDefault();
        items[selectedPaletteIndex]?.classList.remove('selected');
        selectedPaletteIndex = (selectedPaletteIndex + 1) % items.length;
        items[selectedPaletteIndex]?.classList.add('selected');
        items[selectedPaletteIndex]?.scrollIntoView({ block: 'nearest' });
    } else if (e.key === 'ArrowUp') {
        e.preventDefault();
        items[selectedPaletteIndex]?.classList.remove('selected');
        selectedPaletteIndex = (selectedPaletteIndex - 1 + items.length) % items.length;
        items[selectedPaletteIndex]?.classList.add('selected');
        items[selectedPaletteIndex]?.scrollIntoView({ block: 'nearest' });
    } else if (e.key === 'Enter') {
        e.preventDefault();
        items[selectedPaletteIndex]?.click();
    } else if (e.key === 'Escape') {
        toggleCommandPalette();
    }
}

// ============================================================================
// Layout, Tab & Theme Navigation
// ============================================================================

function switchSidebarView(viewName) {
    state.activeView = viewName;

    // Update rail buttons
    const railBtns = document.querySelectorAll('.rail-btn');
    railBtns.forEach(btn => {
        btn.classList.toggle('active', btn.getAttribute('data-view') === viewName);
    });

    // Update view panels
    const viewPanels = document.querySelectorAll('.view-panel');
    viewPanels.forEach(panel => {
        panel.classList.toggle('active', panel.id === `view-${viewName}`);
    });
}

function switchWorkspaceTab(tabName) {
    state.activeTab = tabName;

    // Update tab header buttons
    const tabBtns = document.querySelectorAll('.ws-tab-btn');
    tabBtns.forEach(btn => {
        btn.classList.toggle('active', btn.getAttribute('data-tab') === tabName);
    });

    // Update tab content panels
    const tabPanels = document.querySelectorAll('.workspace-tab-content');
    tabPanels.forEach(panel => {
        panel.classList.toggle('active', panel.id === `tab-${tabName}`);
    });
}

function toggleTheme() {
    state.theme = state.theme === 'dark' ? 'light' : 'dark';
    document.documentElement.setAttribute('data-theme', state.theme);
    localStorage.setItem('humofa_theme', state.theme);

    const sun = document.getElementById('theme-icon-sun');
    const moon = document.getElementById('theme-icon-moon');
    if (sun && moon) {
        if (state.theme === 'dark') {
            sun.classList.remove('hidden');
            moon.classList.add('hidden');
        } else {
            sun.classList.add('hidden');
            moon.classList.remove('hidden');
        }
    }
    appendLog('info', `Switched theme to ${state.theme}`);
}

// Resizable Splitter Handles Logic
function initResizablePanels() {
    const sidebar = document.getElementById('sidebar');
    const sidebarSplitter = document.getElementById('sidebar-splitter');
    const rightPanel = document.getElementById('right-panel');
    const rightPanelSplitter = document.getElementById('right-panel-splitter');

    // Restore saved widths
    const savedSidebarWidth = localStorage.getItem('humofa_sidebar_width');
    if (savedSidebarWidth && sidebar) sidebar.style.width = `${savedSidebarWidth}px`;

    const savedRightWidth = localStorage.getItem('humofa_right_panel_width');
    if (savedRightWidth && rightPanel) rightPanel.style.width = `${savedRightWidth}px`;

    // Sidebar resize
    if (sidebarSplitter && sidebar) {
        let isDragging = false;
        sidebarSplitter.addEventListener('mousedown', (e) => {
            isDragging = true;
            sidebarSplitter.classList.add('active');
            document.body.style.cursor = 'col-resize';
            document.body.style.userSelect = 'none';
        });

        window.addEventListener('mousemove', (e) => {
            if (!isDragging) return;
            const newWidth = Math.min(500, Math.max(200, e.clientX));
            sidebar.style.width = `${newWidth}px`;
            localStorage.setItem('humofa_sidebar_width', newWidth);
        });

        window.addEventListener('mouseup', () => {
            if (isDragging) {
                isDragging = false;
                sidebarSplitter.classList.remove('active');
                document.body.style.cursor = '';
                document.body.style.userSelect = '';
            }
        });
    }

    // Right panel resize
    if (rightPanelSplitter && rightPanel) {
        let isDragging = false;
        rightPanelSplitter.addEventListener('mousedown', (e) => {
            isDragging = true;
            rightPanelSplitter.classList.add('active');
            document.body.style.cursor = 'col-resize';
            document.body.style.userSelect = 'none';
        });

        window.addEventListener('mousemove', (e) => {
            if (!isDragging) return;
            const newWidth = Math.min(480, Math.max(220, window.innerWidth - e.clientX));
            rightPanel.style.width = `${newWidth}px`;
            localStorage.setItem('humofa_right_panel_width', newWidth);
        });

        window.addEventListener('mouseup', () => {
            if (isDragging) {
                isDragging = false;
                rightPanelSplitter.classList.remove('active');
                document.body.style.cursor = '';
                document.body.style.userSelect = '';
            }
        });
    }
}

// Runtime Logs Stream
function appendLog(level, message) {
    const stream = document.getElementById('runtime-logs-stream');
    if (!stream) return;

    const line = document.createElement('div');
    line.className = `log-line log-${level}`;
    line.textContent = `[${new Date().toLocaleTimeString()}] ${message}`;
    stream.appendChild(line);
    stream.scrollTop = stream.scrollHeight;
}

// ============================================================================
// Utilities & Formatting
// ============================================================================
function escapeHtml(str) {
    if (!str) return '';
    return String(str)
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#39;');
}

function formatMarkdown(text) {
    if (!text) return '';
    let html = escapeHtml(text);

    // Code blocks ```code```
    html = html.replace(/```([\s\S]*?)```/g, '<pre class="code-box"><code>$1</code></pre>');
    // Inline code `code`
    html = html.replace(/`([^`]+)`/g, '<code class="code-badge">$1</code>');
    // Bold **text**
    html = html.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');
    // Italic *text*
    html = html.replace(/\*([^*]+)\*/g, '<em>$1</em>');
    // Line breaks
    html = html.replace(/\n/g, '<br/>');

    return html;
}

// ============================================================================
// Initialization & Event Listeners
// ============================================================================
document.addEventListener('DOMContentLoaded', () => {
    // 1. Initialize Theme
    document.documentElement.setAttribute('data-theme', state.theme);
    const sun = document.getElementById('theme-icon-sun');
    const moon = document.getElementById('theme-icon-moon');
    if (sun && moon && state.theme === 'light') {
        sun.classList.add('hidden');
        moon.classList.remove('hidden');
    }

    // 2. Wire Header Controls
    document.getElementById('btn-theme-toggle')?.addEventListener('click', toggleTheme);
    document.getElementById('btn-panic-stop')?.addEventListener('click', triggerPanicStop);
    document.getElementById('btn-palette-trigger')?.addEventListener('click', toggleCommandPalette);
    document.getElementById('btn-setup-wizard')?.addEventListener('click', () => openSetupWizard());
    document.getElementById('btn-open-setup-from-settings')?.addEventListener('click', () => openSetupWizard());
    document.getElementById('btn-setup-close')?.addEventListener('click', closeSetupWizard);
    document.getElementById('btn-setup-skip')?.addEventListener('click', closeSetupWizard);
    document.getElementById('btn-setup-test-connection')?.addEventListener('click', testSetupLlmConnection);
    document.getElementById('btn-test-llm-settings')?.addEventListener('click', testSettingsLlmConnection);
    document.getElementById('form-setup-wizard')?.addEventListener('submit', submitSetupWizard);

    document.querySelectorAll('.setup-provider-card').forEach(card => {
        card.addEventListener('click', () => {
            const p = card.getAttribute('data-provider');
            if (p) selectSetupProvider(p);
        });
    });

    document.getElementById('header-session-select')?.addEventListener('change', (e) => {
        if (e.target.value) activateSession(e.target.value);
    });

    // 3. Wire Rail Buttons
    document.querySelectorAll('.rail-btn').forEach(btn => {
        btn.addEventListener('click', () => {
            const view = btn.getAttribute('data-view');
            if (view) switchSidebarView(view);
        });
    });

    // 4. Wire Workspace Mode Tabs
    document.querySelectorAll('.ws-tab-btn').forEach(btn => {
        btn.addEventListener('click', () => {
            const tab = btn.getAttribute('data-tab');
            if (tab) switchWorkspaceTab(tab);
        });
    });

    // 5. Wire Forms
    document.getElementById('form-new-task')?.addEventListener('submit', (e) => {
        e.preventDefault();
        const prompt = document.getElementById('task-prompt-input')?.value;
        const model = document.getElementById('task-model-select')?.value;
        const autonomy = document.getElementById('task-autonomy-select')?.value;
        if (prompt) dispatchTask(prompt, model, autonomy);
    });

    document.getElementById('form-new-session')?.addEventListener('submit', (e) => {
        e.preventDefault();
        const title = document.getElementById('session-title-input')?.value;
        const model = document.getElementById('session-model-select')?.value;
        if (title) createNewSession(title, model);
    });

    document.getElementById('form-chat')?.addEventListener('submit', (e) => {
        e.preventDefault();
        sendChatPrompt();
    });

    document.getElementById('chat-prompt-input')?.addEventListener('keydown', (e) => {
        if (e.key === 'Enter' && !e.shiftKey) {
            e.preventDefault();
            sendChatPrompt();
        }
    });

    document.getElementById('form-terminal')?.addEventListener('submit', (e) => {
        e.preventDefault();
        const input = document.getElementById('terminal-cmd-input');
        if (input && input.value.trim()) {
            const cmd = input.value.trim();
            input.value = '';
            runTerminalCommand(cmd);
        }
    });

    document.getElementById('btn-terminal-clear')?.addEventListener('click', () => {
        const consoleEl = document.getElementById('terminal-console-output');
        if (consoleEl) consoleEl.innerHTML = '';
    });

    document.getElementById('btn-editor-save')?.addEventListener('click', saveEditorFile);
    document.getElementById('code-editor-textarea')?.addEventListener('input', (e) => {
        updateLineNumbers(e.target.value);
        state.editorFile.dirty = true;
        document.getElementById('editor-dirty-badge')?.classList.remove('hidden');
    });

    document.getElementById('btn-editor-load')?.addEventListener('click', () => {
        const path = document.getElementById('editor-path-input')?.value;
        if (path) loadFileToEditor(path);
    });

    document.getElementById('btn-fs-refresh')?.addEventListener('click', () => refreshFileTree());

    document.getElementById('form-install-skill')?.addEventListener('submit', (e) => {
        e.preventDefault();
        const repo = document.getElementById('skill-repo-input')?.value;
        if (repo) installSkill(repo);
    });

    document.getElementById('form-new-kanban')?.addEventListener('submit', (e) => {
        e.preventDefault();
        const title = document.getElementById('kanban-title-input')?.value;
        const desc = document.getElementById('kanban-desc-input')?.value;
        if (title) createKanbanCard(title, desc);
    });

    document.getElementById('form-new-cron')?.addEventListener('submit', (e) => {
        e.preventDefault();
        const name = document.getElementById('cron-name-input')?.value;
        const phrase = document.getElementById('cron-phrase-input')?.value;
        const prompt = document.getElementById('cron-prompt-input')?.value;
        if (name && phrase && prompt) scheduleCronJob(name, phrase, prompt);
    });

    document.getElementById('form-memory-append')?.addEventListener('submit', (e) => {
        e.preventDefault();
        const title = document.getElementById('memory-entry-title')?.value;
        const content = document.getElementById('memory-entry-content')?.value;
        if (title && content) appendMemoryEntry(state.activeMemoryKind, title, content);
    });

    document.getElementById('memory-search-input')?.addEventListener('input', (e) => {
        searchMemory(e.target.value);
    });

    document.querySelectorAll('.mem-tab-btn').forEach(btn => {
        btn.addEventListener('click', () => {
            document.querySelectorAll('.mem-tab-btn').forEach(b => b.classList.remove('active'));
            btn.classList.add('active');
            state.activeMemoryKind = btn.getAttribute('data-kind') || 'Memory';
            refreshMemory();
        });
    });

    document.getElementById('form-test-integration')?.addEventListener('submit', (e) => {
        e.preventDefault();
        const ch = document.getElementById('integration-channel-select')?.value;
        const msg = document.getElementById('integration-test-msg')?.value;
        if (ch) testIntegrationChannel(ch, msg);
    });

    document.getElementById('form-settings')?.addEventListener('submit', (e) => {
        e.preventDefault();
        saveSettingsFromForm();
    });

    // Diff Buttons
    document.getElementById('btn-diff-load')?.addEventListener('click', () => {
        const path = document.getElementById('diff-path-input')?.value;
        const proposed = document.getElementById('diff-proposed-textarea')?.value;
        if (path && proposed) computeCustomDiff(path, proposed);
        else if (path) loadDiff(path);
    });

    document.getElementById('btn-diff-approve')?.addEventListener('click', approveActiveDiff);
    document.getElementById('btn-diff-reject')?.addEventListener('click', rejectActiveDiff);

    // SmartApproval Decision Buttons
    document.getElementById('btn-approval-accept')?.addEventListener('click', () => handleApprovalDecision(true));
    document.getElementById('btn-approval-reject')?.addEventListener('click', () => handleApprovalDecision(false));
    document.getElementById('btn-approval-cancel')?.addEventListener('click', () => handleApprovalDecision(false));

    // Right Panel Toggle
    document.getElementById('btn-toggle-right-panel')?.addEventListener('click', () => {
        document.getElementById('right-panel')?.classList.toggle('collapsed');
    });

    // 6. Global Command Palette Hotkey & Keyboard Nav
    window.addEventListener('keydown', (e) => {
        if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') {
            e.preventDefault();
            toggleCommandPalette();
        } else if (e.key === 'F12') {
            e.preventDefault();
            triggerPanicStop();
        }
    });

    document.getElementById('palette-search-input')?.addEventListener('input', (e) => {
        renderPaletteResults(e.target.value);
    });

    document.getElementById('palette-search-input')?.addEventListener('keydown', handlePaletteKeydown);

    document.getElementById('palette-modal-backdrop')?.addEventListener('click', (e) => {
        if (e.target.id === 'palette-modal-backdrop') toggleCommandPalette();
    });

    // 7. Initialize Splitter Handlers
    initResizablePanels();

    // 8. Connect WebSocket Stream
    initWebSocket();

    // 9. Initial Data Fetch
    refreshStatus();
    refreshSessions();
    refreshTasks();
    refreshFileTree();
    refreshSkills();
    refreshKanban();
    refreshCron();
    refreshMemory();
    refreshIntegrations();
    refreshSettings();
    refreshChatTranscript();
    checkOnboardingStatus();
});
