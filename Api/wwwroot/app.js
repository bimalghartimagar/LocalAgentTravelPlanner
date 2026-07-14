// ─────────────────────────────────────────────────────────────────────────
// API key — sessionStorage so it survives refresh but clears on tab close
// ─────────────────────────────────────────────────────────────────────────
function getApiKey() { return sessionStorage.getItem('apiKey') || ''; }
function setApiKey(k) { sessionStorage.setItem('apiKey', k); }
function apiHeaders(extra) {
    const key = getApiKey();
    return Object.assign({}, extra || {}, key ? { 'X-Api-Key': key } : {});
}

// ─────────────────────────────────────────────────────────────────────────
// Constants
// ─────────────────────────────────────────────────────────────────────────
const REQUEST_MIN_LENGTH = 10;
const REQUEST_MAX_LENGTH = 2000;
const AGENTS = ['researcher', 'planner', 'accountant', 'auditor', 'aggregator'];
const ROUTE_AGENTS = {
    full: ['researcher', 'planner', 'accountant', 'auditor', 'aggregator'],
    replan: ['planner', 'accountant', 'auditor', 'aggregator'],
    rebudget: ['accountant', 'auditor', 'aggregator'],
    reaudit: ['auditor', 'aggregator'],
    clarify: ['aggregator'],
    offtopic: []
};
const ROUTE_LABELS = {
    full: 'Full pipeline',
    replan: 'Re-planning',
    rebudget: 'Budget tweak',
    reaudit: 'Re-audit',
    clarify: 'Clarification',
    offtopic: 'Off-topic'
};

// ─────────────────────────────────────────────────────────────────────────
// State
// ─────────────────────────────────────────────────────────────────────────
const state = {
    conversationId: sessionStorage.getItem('conversationId') || null,
    conversations: [],   // [{ id, title, lastActivity }]
    latestPlan: null,
    streaming: false,
    abortController: null,
    currentTurn: null    // { bubble, agentContent, currentAgent, route, aggregatorBuffer, planRenderTimer }
};

// ─────────────────────────────────────────────────────────────────────────
// DOM refs
// ─────────────────────────────────────────────────────────────────────────
const sidebar = document.getElementById('sidebar');
const convList = document.getElementById('conv-list');
const convEmpty = document.getElementById('conv-empty');
const newConvBtn = document.getElementById('new-conv-btn');
const chatScroll = document.getElementById('chat-scroll');
const chatEmpty = document.getElementById('chat-empty');
const planScroll = document.getElementById('plan-scroll');
const planEmpty = document.getElementById('plan-empty');
const planMeta = document.getElementById('plan-meta');
const form = document.getElementById('plan-form');
const requestInput = document.getElementById('request-input');
const providerSelect = document.getElementById('provider-select');
const submitBtn = document.getElementById('submit-btn');
const cancelBtn = document.getElementById('cancel-btn');
const charCount = document.getElementById('char-count');
const errorBanner = document.getElementById('error-banner');
const assistantTpl = document.getElementById('tpl-assistant-turn');

marked.setOptions({ breaks: true, gfm: true });

// ─────────────────────────────────────────────────────────────────────────
// Bootstrap
// ─────────────────────────────────────────────────────────────────────────
async function bootstrap() {
    await checkHealth();
    await refreshConversationList();
    if (state.conversationId) {
        const ok = await loadConversation(state.conversationId);
        if (!ok) state.conversationId = null;
    }
    updateChatEmptyState();
}
bootstrap();

async function checkHealth() {
    try {
        const res = await fetch('/api/travel/health');
        const data = await res.json();
        const ollamaBadge = document.getElementById('ollama-badge');
        const anthropicBadge = document.getElementById('anthropic-badge');
        const geminiBadge = document.getElementById('gemini-badge');
        const groqBadge = document.getElementById('groq-badge');
        ollamaBadge.className = 'badge available';
        ollamaBadge.innerHTML = '<span class="badge-dot"></span>Ollama';
        setBadge(anthropicBadge, 'Anthropic', data.providers.anthropic === 'available');
        setBadge(geminiBadge, 'Gemini', data.providers.gemini === 'available');
        setBadge(groqBadge, 'Groq', data.providers.groq === 'available');
    } catch {
        document.getElementById('ollama-badge').className = 'badge unavailable';
        document.getElementById('anthropic-badge').style.display = 'none';
        document.getElementById('gemini-badge').style.display = 'none';
        document.getElementById('groq-badge').style.display = 'none';
    }
}

function setBadge(el, label, available) {
    if (!el) return;
    el.className = 'badge ' + (available ? 'available' : 'unavailable');
    el.innerHTML = '<span class="badge-dot"></span>' + label;
    el.title = available ? '' : 'Not configured';
}

// ─────────────────────────────────────────────────────────────────────────
// Sidebar / conversation list
// ─────────────────────────────────────────────────────────────────────────
async function refreshConversationList() {
    try {
        const res = await fetch('/api/conversations', { headers: apiHeaders() });
        if (res.status === 401) { promptForKey(); return; }
        if (!res.ok) return;
        state.conversations = await res.json();
        renderConversationList();
    } catch {
        // best-effort; server may be offline
    }
}

function renderConversationList() {
    convList.innerHTML = '';
    if (!state.conversations || state.conversations.length === 0) {
        convEmpty.style.display = 'block';
        return;
    }
    convEmpty.style.display = 'none';
    for (const c of state.conversations) {
        const li = document.createElement('li');
        li.className = 'conv-item' + (c.id === state.conversationId ? ' active' : '');
        li.dataset.id = c.id;
        li.innerHTML = `
            <span class="conv-item-title" title="${escapeHtml(c.title || 'Untitled')}">${escapeHtml(c.title || 'Untitled')}</span>
            <span class="conv-item-time">${relativeTime(c.lastActivity)}</span>
            <button class="conv-item-delete" title="Delete">×</button>
        `;
        li.addEventListener('click', (e) => {
            if (e.target.closest('.conv-item-delete')) return;
            if (state.streaming) return;
            switchConversation(c.id);
        });
        li.querySelector('.conv-item-delete').addEventListener('click', async (e) => {
            e.stopPropagation();
            if (!confirm('Delete this conversation?')) return;
            await deleteConversation(c.id);
        });
        convList.appendChild(li);
    }
}

newConvBtn.addEventListener('click', async () => {
    if (state.streaming) return;
    await createConversation();
});

async function createConversation() {
    try {
        const res = await fetch('/api/conversations', { method: 'POST', headers: apiHeaders() });
        if (res.status === 401) { promptForKey(); return null; }
        if (!res.ok) { showError(`Failed to create conversation (HTTP ${res.status})`); return null; }
        const data = await res.json();
        state.conversationId = data.id;
        sessionStorage.setItem('conversationId', data.id);
        clearChat();
        clearPlan();
        await refreshConversationList();
        requestInput.focus();
        return data.id;
    } catch {
        showError('Failed to create conversation.');
        return null;
    }
}

async function deleteConversation(id) {
    try {
        const res = await fetch(`/api/conversations/${id}`, { method: 'DELETE', headers: apiHeaders() });
        if (!res.ok && res.status !== 404) return;
        if (state.conversationId === id) {
            state.conversationId = null;
            sessionStorage.removeItem('conversationId');
            clearChat();
            clearPlan();
        }
        await refreshConversationList();
    } catch { /* ignore */ }
}

async function switchConversation(id) {
    state.conversationId = id;
    sessionStorage.setItem('conversationId', id);
    renderConversationList(); // updates active class
    await loadConversation(id);
}

async function loadConversation(id) {
    try {
        const res = await fetch(`/api/conversations/${id}`, { headers: apiHeaders() });
        if (res.status === 401) { promptForKey(); return false; }
        if (res.status === 404) { return false; }
        if (!res.ok) return false;
        const data = await res.json();

        clearChat();
        for (const msg of data.history) {
            if (msg.role === 'user') {
                appendUserBubble(msg.content);
            } else {
                appendStaticAssistantBubble(msg.content);
            }
        }
        renderPlan(data.latestPlan);
        updateChatEmptyState();
        return true;
    } catch {
        return false;
    }
}

// ─────────────────────────────────────────────────────────────────────────
// Chat rendering helpers
// ─────────────────────────────────────────────────────────────────────────
function clearChat() {
    Array.from(chatScroll.querySelectorAll('.msg')).forEach(n => n.remove());
    updateChatEmptyState();
}
function clearPlan() {
    state.latestPlan = null;
    renderPlan(null);
}
function updateChatEmptyState() {
    const hasMessages = chatScroll.querySelectorAll('.msg').length > 0;
    chatEmpty.style.display = hasMessages ? 'none' : 'flex';
}
function scrollChatToBottom() {
    chatScroll.scrollTop = chatScroll.scrollHeight;
}

function appendUserBubble(text) {
    const div = document.createElement('div');
    div.className = 'msg msg-user';
    div.textContent = text;
    chatScroll.appendChild(div);
    updateChatEmptyState();
    scrollChatToBottom();
}

function appendStaticAssistantBubble(text) {
    // Past assistant turns we load from server — just the body, no pipeline/route chip
    const div = document.createElement('div');
    div.className = 'msg msg-assistant';
    div.innerHTML = `
        <div class="msg-header"><span class="msg-role">Assistant</span></div>
        <div class="msg-body markdown-body"></div>
    `;
    div.querySelector('.msg-body').innerHTML = marked.parse(text || '');
    chatScroll.appendChild(div);
    updateChatEmptyState();
    scrollChatToBottom();
}

function appendStreamingAssistantBubble() {
    const node = assistantTpl.content.firstElementChild.cloneNode(true);
    chatScroll.appendChild(node);
    bindDetailsTabs(node);
    updateChatEmptyState();
    scrollChatToBottom();
    return node;
}

function bindDetailsTabs(bubble) {
    const tabs = bubble.querySelectorAll('.agent-tab');
    tabs.forEach(t => {
        t.addEventListener('click', () => switchAgentDetailsTab(bubble, t.dataset.tab));
    });
    // Activate first tab with content (lazy — defaults to researcher)
    switchAgentDetailsTab(bubble, 'researcher');
}

function switchAgentDetailsTab(bubble, name) {
    bubble.querySelectorAll('.agent-tab').forEach(t => {
        t.classList.toggle('active', t.dataset.tab === name);
    });
    bubble.querySelectorAll('.tab-pane').forEach(p => {
        p.classList.toggle('active', p.dataset.pane === name);
    });
}

// ─────────────────────────────────────────────────────────────────────────
// Plan pane
// ─────────────────────────────────────────────────────────────────────────
function renderPlan(markdown, opts) {
    state.latestPlan = markdown || null;
    if (!markdown) {
        planScroll.innerHTML = '<div class="plan-empty" id="plan-empty">The current travel plan will appear here.</div>';
        planMeta.textContent = '';
        planScroll.classList.remove('streaming');
        return;
    }
    planScroll.innerHTML = `<div class="markdown-body">${marked.parse(markdown)}</div>`;
    if (opts && opts.streaming) {
        planScroll.classList.add('streaming');
    } else {
        planScroll.classList.remove('streaming');
        planMeta.textContent = 'Updated ' + new Date().toLocaleTimeString();
    }
}

// ─────────────────────────────────────────────────────────────────────────
// Composer
// ─────────────────────────────────────────────────────────────────────────
requestInput.addEventListener('input', () => {
    const len = requestInput.value.length;
    charCount.textContent = `${len} / ${REQUEST_MAX_LENGTH}`;
    charCount.className = 'char-count'
        + (len >= REQUEST_MAX_LENGTH ? ' at-limit' : len >= REQUEST_MAX_LENGTH * 0.9 ? ' near-limit' : '');
});

// Example chips fill the composer
document.addEventListener('click', (e) => {
    const chip = e.target.closest('.chip');
    if (!chip) return;
    requestInput.value = chip.textContent;
    requestInput.dispatchEvent(new Event('input'));
    requestInput.focus();
});

form.addEventListener('submit', async (e) => {
    e.preventDefault();
    const message = requestInput.value.trim();
    if (message.length < REQUEST_MIN_LENGTH) { showError(`Message must be at least ${REQUEST_MIN_LENGTH} characters.`); return; }
    if (message.length > REQUEST_MAX_LENGTH) { showError(`Message must not exceed ${REQUEST_MAX_LENGTH} characters.`); return; }
    hideError();

    // Auto-create a conversation on first send
    if (!state.conversationId) {
        const id = await createConversation();
        if (!id) return;
    }

    await startTurn(message, providerSelect.value);
});

cancelBtn.addEventListener('click', () => {
    if (state.abortController) {
        state.abortController.abort();
        state.abortController = null;
    }
});

// ─────────────────────────────────────────────────────────────────────────
// Per-turn streaming
// ─────────────────────────────────────────────────────────────────────────
async function startTurn(message, provider) {
    state.streaming = true;
    submitBtn.style.display = 'none';
    cancelBtn.style.display = 'inline-block';
    requestInput.disabled = true;
    providerSelect.disabled = true;
    newConvBtn.disabled = true;

    // 1. User bubble
    appendUserBubble(message);
    requestInput.value = '';
    requestInput.dispatchEvent(new Event('input'));

    // 2. Assistant bubble scaffolding
    const bubble = appendStreamingAssistantBubble();
    state.currentTurn = {
        bubble,
        agentContent: { researcher: '', planner: '', accountant: '', auditor: '', aggregator: '' },
        currentAgent: null,
        route: null,
        aggregatorBuffer: '',
        planRenderTimer: null,
        detailsTabRenderTimer: null
    };
    setStatus(bubble, 'Connecting…');

    // 3. Open SSE stream
    const params = new URLSearchParams({ message });
    if (provider) params.set('provider', provider);

    state.abortController = new AbortController();

    try {
        const res = await fetch(`/api/conversations/${state.conversationId}/messages/stream?${params}`, {
            signal: state.abortController.signal,
            headers: apiHeaders()
        });

        if (res.status === 401) { promptForKey(); finalizeTurn({ cancelled: true }); return; }
        if (res.status === 404) { showError('Conversation no longer exists.'); finalizeTurn({ cancelled: true }); return; }
        if (res.status === 409) { showError('Another turn is already in progress for this conversation. Wait for it to finish.'); finalizeTurn({ cancelled: true }); return; }
        if (res.status === 429) { showError('Too many requests. Try again in a minute.'); finalizeTurn({ cancelled: true }); return; }
        if (!res.ok) { showError(`Server error: ${res.status}`); finalizeTurn({ cancelled: true }); return; }

        const reader = res.body.getReader();
        const decoder = new TextDecoder();
        let buffer = '';

        outer: while (true) {
            const { done, value } = await reader.read();
            if (done) break;
            buffer += decoder.decode(value, { stream: true });

            const lines = buffer.split('\n');
            buffer = lines.pop();

            let evtType = null;
            for (const line of lines) {
                if (line.startsWith('event: ')) {
                    evtType = line.slice(7).trim();
                } else if (line.startsWith('data: ') && evtType) {
                    try {
                        const data = JSON.parse(line.slice(6));
                        if (!handleEvent(evtType, data)) { break outer; }
                    } catch { /* skip malformed JSON */ }
                    evtType = null;
                } else if (line === '') {
                    evtType = null;
                }
            }
        }

        finalizeTurn({});
    } catch (err) {
        if (err.name === 'AbortError') {
            setStatus(state.currentTurn?.bubble, 'Cancelled', 'error');
        } else {
            showError('Connection to server lost.');
            setStatus(state.currentTurn?.bubble, 'Disconnected', 'error');
        }
        finalizeTurn({ cancelled: true });
    }
}

// Returns false to break the read loop early (terminal event).
function handleEvent(type, data) {
    const turn = state.currentTurn;
    if (!turn) return false;
    const bubble = turn.bubble;

    switch (type) {
        case 'init':
            setStatus(bubble, `Using ${data.provider} · ${data.model}`);
            return true;

        case 'route': {
            turn.route = data.route;
            const chip = bubble.querySelector('.route-chip');
            chip.textContent = ROUTE_LABELS[data.route] || data.route;
            chip.className = 'route-chip visible' + (data.route === 'offtopic' ? ' offtopic' : '');
            // Dim skipped agent dots
            const running = new Set((data.agentsToRun || ROUTE_AGENTS[data.route] || []));
            bubble.querySelectorAll('.agent-dot').forEach(d => {
                if (!running.has(d.dataset.agent)) d.classList.add('skipped');
            });
            // Show the agent-details disclosure once we know there's pipeline work
            if (running.size > 0) {
                bubble.querySelector('.agent-details').hidden = false;
            }
            return true;
        }

        case 'agent-start': {
            turn.currentAgent = data.agent;
            const dot = bubble.querySelector(`.agent-dot[data-agent="${data.agent}"]`);
            if (dot) { dot.classList.remove('completed', 'failed'); dot.classList.add('active'); }
            setStatus(bubble, `${capitalize(data.agent)} working…`);
            markDetailsTabStreaming(bubble, data.agent, true);
            // Auto-switch the inner tab to whichever agent is active so the user can peek
            scheduleDetailsTabSwitch(bubble, data.agent);
            return true;
        }

        case 'content': {
            if (!data.content || !data.agent) return true;
            turn.agentContent[data.agent] = (turn.agentContent[data.agent] || '') + data.content;
            markDetailsTabHasContent(bubble, data.agent);
            schedulePaneRender(bubble, data.agent);
            // Stream aggregator content live into the right pane
            if (data.agent === 'aggregator') {
                turn.aggregatorBuffer += data.content;
                schedulePlanRender(turn.aggregatorBuffer, { streaming: true });
            }
            return true;
        }

        case 'agent-complete': {
            const dot = bubble.querySelector(`.agent-dot[data-agent="${data.agent}"]`);
            if (dot) { dot.classList.remove('active'); dot.classList.add('completed'); }
            markDetailsTabStreaming(bubble, data.agent, false);
            renderDetailsPane(bubble, data.agent, turn.agentContent[data.agent]);
            return true;
        }

        case 'plan-final': {
            if (data.plan) renderPlan(data.plan);
            setStatus(bubble, 'Plan updated');
            // Surface a tiny pointer in the bubble body
            bubble.querySelector('.msg-body').innerHTML =
                '<em style="color:var(--text-muted);font-size:0.85rem;">Updated plan shown on the right →</em>';
            // Refresh sidebar timestamps
            refreshConversationList();
            return true;
        }

        case 'clarified': {
            // Chat answer for clarify / off-topic — render as markdown in the bubble
            const body = bubble.querySelector('.msg-body');
            body.innerHTML = marked.parse(data.reply || '');
            setStatus(bubble, turn.route === 'offtopic' ? 'Off-topic' : 'Answered');
            refreshConversationList();
            return true;
        }

        case 'error': {
            showError(data.content || 'An error occurred');
            const dot = turn.currentAgent
                ? bubble.querySelector(`.agent-dot[data-agent="${turn.currentAgent}"]`)
                : null;
            if (dot) { dot.classList.remove('active'); dot.classList.add('failed'); }
            setStatus(bubble, data.content || 'Error', 'error');
            return false; // terminal
        }

        case 'complete':
            return false; // terminal — main loop exits and finalizeTurn runs
    }
    return true;
}

function finalizeTurn(opts) {
    const turn = state.currentTurn;
    if (turn) {
        // Flush any pending renders
        if (turn.planRenderTimer) { clearTimeout(turn.planRenderTimer); turn.planRenderTimer = null; }
        if (turn.detailsTabRenderTimer) { clearTimeout(turn.detailsTabRenderTimer); turn.detailsTabRenderTimer = null; }
        // Force final render of details panes
        if (turn.bubble) {
            for (const a of AGENTS) {
                if (turn.agentContent[a]) renderDetailsPane(turn.bubble, a, turn.agentContent[a]);
            }
        }
        // If aggregator streamed but no plan-final arrived (e.g. cancel), still settle the right pane
        if (turn.aggregatorBuffer && !opts.cancelled && turn.route && turn.route !== 'clarify') {
            renderPlan(turn.aggregatorBuffer);
        } else {
            planScroll.classList.remove('streaming');
        }
    }
    state.currentTurn = null;
    state.streaming = false;
    state.abortController = null;
    submitBtn.style.display = 'inline-block';
    cancelBtn.style.display = 'none';
    requestInput.disabled = false;
    providerSelect.disabled = false;
    newConvBtn.disabled = false;
    requestInput.focus();
    refreshConversationList();
}

// ─────────────────────────────────────────────────────────────────────────
// Throttled renderers
// ─────────────────────────────────────────────────────────────────────────
function schedulePaneRender(bubble, agent) {
    const turn = state.currentTurn;
    if (!turn) return;
    if (turn.detailsTabRenderTimer) return;
    turn.detailsTabRenderTimer = setTimeout(() => {
        renderDetailsPane(bubble, agent, turn.agentContent[agent]);
        turn.detailsTabRenderTimer = null;
    }, 100);
}

function schedulePlanRender(markdown, opts) {
    const turn = state.currentTurn;
    if (!turn) { renderPlan(markdown, opts); return; }
    if (turn.planRenderTimer) return;
    turn.planRenderTimer = setTimeout(() => {
        renderPlan(markdown, opts);
        turn.planRenderTimer = null;
    }, 100);
}

function renderDetailsPane(bubble, agent, content) {
    const pane = bubble.querySelector(`.tab-pane[data-pane="${agent}"]`);
    if (!pane) return;
    pane.innerHTML = content
        ? `<div class="markdown-body">${marked.parse(content)}</div>`
        : '';
}

function markDetailsTabHasContent(bubble, agent) {
    const tab = bubble.querySelector(`.agent-tab[data-tab="${agent}"]`);
    if (tab) tab.classList.add('has-content');
}
function markDetailsTabStreaming(bubble, agent, on) {
    bubble.querySelectorAll('.agent-tab').forEach(t => t.classList.remove('streaming'));
    if (on) {
        const tab = bubble.querySelector(`.agent-tab[data-tab="${agent}"]`);
        if (tab) tab.classList.add('streaming');
    }
}
function scheduleDetailsTabSwitch(bubble, agent) {
    // Don't override the user's manual tab pick — only auto-switch if no tab is active
    const hasActive = bubble.querySelector('.agent-tab.active');
    if (!hasActive || !hasActive.classList.contains('has-content')) {
        switchAgentDetailsTab(bubble, agent);
    }
}

function setStatus(bubble, text, kind) {
    if (!bubble) return;
    const el = bubble.querySelector('.msg-status');
    if (!el) return;
    el.textContent = text || '';
    el.className = 'msg-status' + (kind === 'error' ? ' error' : '');
}

// ─────────────────────────────────────────────────────────────────────────
// Misc
// ─────────────────────────────────────────────────────────────────────────
function showError(msg) {
    errorBanner.textContent = msg;
    errorBanner.classList.add('visible');
}
function hideError() {
    errorBanner.classList.remove('visible');
}
function promptForKey() {
    const key = prompt('Enter API key:');
    if (key) { setApiKey(key.trim()); window.location.reload(); }
    else { showError('API key required.'); }
}
function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, c => ({
        '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
    }[c]));
}
function capitalize(s) { return s ? s.charAt(0).toUpperCase() + s.slice(1) : s; }
function relativeTime(iso) {
    if (!iso) return '';
    const d = new Date(iso);
    const diff = (Date.now() - d.getTime()) / 1000;
    if (diff < 60) return 'just now';
    if (diff < 3600) return `${Math.floor(diff / 60)}m`;
    if (diff < 86400) return `${Math.floor(diff / 3600)}h`;
    if (diff < 604800) return `${Math.floor(diff / 86400)}d`;
    return d.toLocaleDateString();
}
