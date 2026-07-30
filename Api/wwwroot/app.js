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

// Shared "plan updated" pointer used by both live plan-final and restored bubbles.
const PLAN_UPDATED_POINTER_HTML =
    '<em style="color:var(--text-muted);font-size:0.85rem;">Plan updated — see right pane →</em>';

// ─────────────────────────────────────────────────────────────────────────
// State
// ─────────────────────────────────────────────────────────────────────────
const state = {
    conversationId: sessionStorage.getItem('conversationId') || null,
    conversations: [],   // [{ id, title, lastActivity }]
    latestPlan: null,
    streaming: false,
    abortController: null,
    currentTurn: null,   // { bubble, agentContent, currentAgent, route, aggregatorBuffer, planRenderTimer }
    requireApproval: false,      // Per-conversation setting; toggle drives it.
    pendingDecision: null        // Set when the current conversation is paused mid-turn.
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
const approvalToggle = document.getElementById('require-approval-toggle');

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
        const openRouterBadge = document.getElementById('openrouter-badge');
        ollamaBadge.className = 'badge available';
        ollamaBadge.innerHTML = '<span class="badge-dot"></span>Ollama';
        setBadge(anthropicBadge, 'Anthropic', data.providers.anthropic === 'available');
        setBadge(geminiBadge, 'Gemini', data.providers.gemini === 'available');
        setBadge(groqBadge, 'Groq', data.providers.groq === 'available');
        setBadge(openRouterBadge, 'OpenRouter', data.providers.openRouter === 'available');
    } catch {
        document.getElementById('ollama-badge').className = 'badge unavailable';
        document.getElementById('anthropic-badge').style.display = 'none';
        document.getElementById('gemini-badge').style.display = 'none';
        document.getElementById('groq-badge').style.display = 'none';
        document.getElementById('openrouter-badge').style.display = 'none';
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

// Toggle drives per-conversation RequireApproval via PATCH. Disabled while a turn is
// streaming — flipping mid-turn is meaningless (the gate decision already happened).
approvalToggle.addEventListener('change', async () => {
    if (state.streaming) { approvalToggle.checked = state.requireApproval; return; }
    if (!state.conversationId) {
        // Remember the preference; it will apply once a conversation exists.
        state.requireApproval = approvalToggle.checked;
        return;
    }
    const newValue = approvalToggle.checked;
    try {
        const res = await fetch(`/api/conversations/${state.conversationId}/settings`, {
            method: 'PATCH',
            headers: apiHeaders({ 'Content-Type': 'application/json' }),
            body: JSON.stringify({ requireApproval: newValue })
        });
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        state.requireApproval = newValue;
    } catch {
        approvalToggle.checked = state.requireApproval;
        showError('Failed to update approval setting.');
    }
});

async function createConversation() {
    try {
        const res = await fetch('/api/conversations', { method: 'POST', headers: apiHeaders() });
        if (res.status === 401) { promptForKey(); return null; }
        if (!res.ok) { showError(`Failed to create conversation (HTTP ${res.status})`); return null; }
        const data = await res.json();
        state.conversationId = data.id;
        sessionStorage.setItem('conversationId', data.id);
        state.pendingDecision = null;
        clearChat();
        clearPlan();
        // Apply pre-toggle preference if user checked "Require approval" before creating.
        if (approvalToggle.checked && !state.requireApproval) {
            state.requireApproval = true;
            fetch(`/api/conversations/${data.id}/settings`, {
                method: 'PATCH',
                headers: apiHeaders({ 'Content-Type': 'application/json' }),
                body: JSON.stringify({ requireApproval: true })
            }).catch(() => { /* best-effort; toggle handler will re-emit if user re-checks */ });
        }
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
        // Turns metadata is parallel to user/assistant pairs. Turn index N corresponds to
        // history messages at positions (2N user, 2N+1 assistant).
        const turnsByIndex = new Map((data.turns || []).map(t => [t.turnIndex, t]));
        let assistantIndex = 0;
        for (const msg of data.history) {
            if (msg.role === 'user') {
                appendUserBubble(msg.content);
            } else {
                const meta = turnsByIndex.get(assistantIndex);
                appendRestoredAssistantBubble(msg.content, meta);
                assistantIndex++;
            }
        }
        renderPlan(data.latestPlan);

        // Reflect the persisted approval toggle in the composer checkbox.
        state.requireApproval = !!data.requireApproval;
        approvalToggle.checked = state.requireApproval;

        // If a pending decision survived a refresh, render the user's pending message
        // and the approval card so they can resume or cancel.
        state.pendingDecision = data.pendingDecision || null;
        if (state.pendingDecision) restorePendingDecisionUI(state.pendingDecision);

        updateChatEmptyState();
        return true;
    } catch {
        return false;
    }
}

/// Rebuilds the approval-required UI after page refresh. Adds the pending user message
/// (not yet in history — it commits with the assistant reply on approve) plus a fresh
/// assistant bubble carrying the approval card and per-agent details.
function restorePendingDecisionUI(pending) {
    appendUserBubble(pending.userMessage);
    const bubble = appendStreamingAssistantBubble();
    // Populate route chip + pipeline dots from the pending metadata so the UI matches
    // what a live approval-required event would have produced.
    const chip = bubble.querySelector('.route-chip');
    chip.textContent = ROUTE_LABELS[pending.route] || pending.route;
    chip.className = 'route-chip visible';
    const ran = new Set(pending.agentsRun || []);
    const routeSet = new Set(ROUTE_AGENTS[pending.route] || []);
    bubble.querySelectorAll('.agent-dot').forEach(d => {
        const agent = d.dataset.agent;
        if (!routeSet.has(agent)) d.classList.add('skipped');
        else if (ran.has(agent)) d.classList.add('completed');
        // Aggregator dot stays inactive — it hasn't run yet
    });
    // Wire the details tabs and populate the per-agent panes.
    bubble.querySelector('.agent-details').hidden = false;
    bindDetailsTabs(bubble);
    const outputs = pending.agentOutputs || {};
    for (const agent of AGENTS) {
        if (outputs[agent]) {
            markDetailsTabHasContent(bubble, agent);
            renderDetailsPane(bubble, agent, outputs[agent]);
        }
    }
    setStatus(bubble, `Awaiting decision (${pending.provider || 'unknown'} · ${pending.model || 'unknown'})`);
    showApprovalCard(bubble, pending);
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

// Restored assistant bubble WITH route chip + completed pipeline dots (based on turn
// metadata persisted server-side). For Clarify/OffTopic turns, put the text in body.
// For plan-changing turns, body is empty and the plan lives in the right pane (LatestPlan).
function appendRestoredAssistantBubble(text, meta) {
    if (!meta) {
        // No metadata (older conversation, migration case) — fall back to plain
        appendStaticAssistantBubble(text);
        return;
    }
    const node = assistantTpl.content.firstElementChild.cloneNode(true);

    // Route chip
    const chip = node.querySelector('.route-chip');
    chip.textContent = ROUTE_LABELS[meta.route] || meta.route;
    chip.className = 'route-chip visible' + (meta.route === 'offtopic' ? ' offtopic' : '');

    // Dim dots not in this route's subset; mark ran dots completed
    const ran = new Set(meta.agentsRun || []);
    const routeSet = new Set(ROUTE_AGENTS[meta.route] || []);
    node.querySelectorAll('.agent-dot').forEach(d => {
        const agent = d.dataset.agent;
        if (!routeSet.has(agent)) d.classList.add('skipped');
        else if (ran.has(agent)) d.classList.add('completed');
    });

    // Body: only Clarify/OffTopic replies live in the bubble body
    const body = node.querySelector('.msg-body');
    if (meta.route === 'clarify' || meta.route === 'offtopic') {
        body.innerHTML = marked.parse(text || '');
    } else if (meta.changeSummary && meta.changeSummary.trim()) {
        body.innerHTML =
            '<div style="font-size:0.85rem;color:var(--text-muted);margin-bottom:0.35rem;">Plan updated — see right pane →</div>' +
            '<div class="markdown-body">' + marked.parse(meta.changeSummary) + '</div>';
    } else {
        body.innerHTML = PLAN_UPDATED_POINTER_HTML;
    }

    // Status line: "45s · groq · llama-3.3-70b-versatile"
    const parts = [];
    if (meta.durationMs != null) parts.push(formatDurationMs(meta.durationMs));
    if (meta.provider) parts.push(meta.provider.toLowerCase());
    if (meta.model) parts.push(meta.model);
    if (parts.length > 0) setStatus(node, parts.join(' · '));

    // Agent-details disclosure — populate from restored per-agent content if present
    const outputs = meta.agentOutputs || {};
    const hasAnyOutput = Object.keys(outputs).some(k => outputs[k]);
    if (hasAnyOutput) {
        node.querySelector('.agent-details').hidden = false;
        bindDetailsTabs(node);
        for (const agent of AGENTS) {
            const content = outputs[agent];
            if (content) {
                markDetailsTabHasContent(node, agent);
                renderDetailsPane(node, agent, content);
            }
        }
    } else {
        node.querySelector('.agent-details').hidden = true;
    }

    chatScroll.appendChild(node);
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
    if (state.pendingDecision) {
        showError('Resolve the pending draft (approve, reject, or cancel) before sending a new message.');
        return;
    }
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
        bubbleBodyRenderTimer: null,
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

        if (!res.ok) {
            const handled = await handleApiErrorResponse(res);
            finalizeTurn({ cancelled: true });
            if (!handled) showError(`Server error: ${res.status}`);
            return;
        }

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
            // Aggregator streaming target depends on route:
            // clarify/offtopic → chat bubble body (chat-answer mode, plan stays untouched)
            // everything else → right pane (plan-generation mode)
            if (data.agent === 'aggregator') {
                turn.aggregatorBuffer += data.content;
                if (turn.route === 'clarify' || turn.route === 'offtopic') {
                    scheduleBubbleBodyRender(bubble, turn.aggregatorBuffer);
                } else {
                    schedulePlanRender(turn.aggregatorBuffer, { streaming: true });
                }
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
            // If Aggregator emitted a "Changes This Turn" section, show it in bubble body.
            // Otherwise fall back to pointer (first turn Full route or non-updating agent).
            const body = bubble.querySelector('.msg-body');
            if (data.changeSummary && data.changeSummary.trim()) {
                body.innerHTML =
                    '<div style="font-size:0.85rem;color:var(--text-muted);margin-bottom:0.35rem;">Plan updated — see right pane →</div>' +
                    '<div class="markdown-body">' + marked.parse(data.changeSummary) + '</div>';
            } else {
                body.innerHTML = PLAN_UPDATED_POINTER_HTML;
            }
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

        case 'approval-required': {
            // Upstream finished, Aggregator paused. Persist pending state and show the
            // approval card on the current bubble. Returning false ends the current SSE
            // read loop — a fresh stream opens when the user clicks approve or reject.
            state.pendingDecision = data;
            setStatus(bubble, `Awaiting decision (${data.provider || 'unknown'} · ${data.model || 'unknown'})`);
            showApprovalCard(bubble, data);
            refreshConversationList();
            return false;
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
        if (turn.bubbleBodyRenderTimer) { clearTimeout(turn.bubbleBodyRenderTimer); turn.bubbleBodyRenderTimer = null; }
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
    // When a pending decision is outstanding, keep the composer blocked until the user
    // resolves it — the server will 409 on any new message anyway.
    const paused = !!state.pendingDecision;
    state.currentTurn = null;
    state.streaming = false;
    state.abortController = null;
    submitBtn.style.display = 'inline-block';
    cancelBtn.style.display = 'none';
    requestInput.disabled = paused;
    providerSelect.disabled = paused;
    newConvBtn.disabled = false;
    if (!paused) requestInput.focus();
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

// Throttled render into the assistant bubble's body. Used for Clarify/OffTopic where
// Aggregator's streamed content is a chat-style answer, not a plan document.
function scheduleBubbleBodyRender(bubble, markdown) {
    const turn = state.currentTurn;
    if (!turn) return;
    if (turn.bubbleBodyRenderTimer) return;
    turn.bubbleBodyRenderTimer = setTimeout(() => {
        const body = bubble.querySelector('.msg-body');
        if (body) body.innerHTML = marked.parse(markdown || '');
        turn.bubbleBodyRenderTimer = null;
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

/// Renders the approval card on the given bubble and wires up its buttons. Card is
/// idempotent — safe to call twice (later calls just re-populate the verdict/actions).
function showApprovalCard(bubble, pending) {
    const card = bubble.querySelector('.approval-card');
    if (!card) return;
    card.hidden = false;

    const verdict = (pending.auditorVerdict || '').toLowerCase();
    const verdictEl = card.querySelector('.approval-verdict');
    verdictEl.textContent = verdict ? verdict : 'unknown';
    verdictEl.className = 'approval-verdict ' + (verdict || '');

    const feedbackWrap = card.querySelector('.approval-feedback');
    const feedbackInput = card.querySelector('.approval-feedback-input');
    const approveBtn = card.querySelector('.approval-approve');
    const rejectBtn = card.querySelector('.approval-reject');
    const rejectReplanBtn = card.querySelector('.approval-reject-replan');
    const cancelBtn = card.querySelector('.approval-cancel');

    // Reset any prior wiring — cloneNode strips listeners, faster than tracking handles
    const rewire = (btn) => {
        const clone = btn.cloneNode(true);
        btn.replaceWith(clone);
        return clone;
    };
    const approveFresh = rewire(approveBtn);
    const rejectFresh = rewire(rejectBtn);
    const rejectReplanFresh = rewire(rejectReplanBtn);
    const cancelFresh = rewire(cancelBtn);

    // Reject button toggles feedback textarea + shows the replan button. Second click
    // (with empty feedback) posts a plain rejection.
    let feedbackOpen = false;
    rejectFresh.addEventListener('click', () => {
        if (!feedbackOpen) {
            feedbackWrap.hidden = false;
            rejectReplanFresh.hidden = false;
            cancelFresh.hidden = false;
            feedbackInput.focus();
            feedbackOpen = true;
            return;
        }
        // Second click on Reject = commit rejection without replan
        resolveDecision(bubble, {
            approve: false,
            feedback: feedbackInput.value.trim() || null,
            replan: false
        });
    });

    rejectReplanFresh.addEventListener('click', () => {
        const txt = feedbackInput.value.trim();
        if (!txt) { feedbackInput.focus(); showError('Feedback required to replan.'); return; }
        resolveDecision(bubble, { approve: false, feedback: txt, replan: true });
    });

    cancelFresh.addEventListener('click', async () => {
        if (!confirm('Discard this pending draft?')) return;
        try {
            const res = await fetch(`/api/conversations/${state.conversationId}/pending`, {
                method: 'DELETE',
                headers: apiHeaders()
            });
            if (!res.ok && res.status !== 404) throw new Error(`HTTP ${res.status}`);
            state.pendingDecision = null;
            // Drop the paused bubble entirely; user starts fresh next turn
            bubble.remove();
            const paused = false;
            requestInput.disabled = paused;
            providerSelect.disabled = paused;
            requestInput.focus();
            refreshConversationList();
        } catch { showError('Failed to cancel pending draft.'); }
    });

    approveFresh.addEventListener('click', () => {
        resolveDecision(bubble, { approve: true, feedback: null, replan: false });
    });
}

/// Opens a fresh SSE stream to /decision/stream to resume the paused turn. Reuses the
/// existing bubble so the plan renders in place, and reuses handleEvent so approve
/// (Aggregator run) and reject-and-replan (full new turn) both drive the same UI.
async function resolveDecision(bubble, decision) {
    if (!state.conversationId || !state.pendingDecision) return;
    hideError();

    // Hide the approval card while resuming — reopens if the server errors out.
    const card = bubble.querySelector('.approval-card');
    if (card) card.hidden = true;

    // Wire the turn state onto the existing bubble so aggregator content streams into
    // the right pane exactly like a normal turn. Preserve prior agent content so the
    // details tabs stay populated during Aggregator's run.
    const priorOutputs = state.pendingDecision.agentOutputs || {};
    state.currentTurn = {
        bubble,
        agentContent: {
            researcher: priorOutputs.researcher || '',
            planner: priorOutputs.planner || '',
            accountant: priorOutputs.accountant || '',
            auditor: priorOutputs.auditor || '',
            aggregator: ''
        },
        currentAgent: null,
        route: state.pendingDecision.route,
        aggregatorBuffer: '',
        planRenderTimer: null,
        bubbleBodyRenderTimer: null,
        detailsTabRenderTimer: null
    };
    state.streaming = true;
    submitBtn.style.display = 'none';
    cancelBtn.style.display = 'inline-block';
    requestInput.disabled = true;
    providerSelect.disabled = true;

    // Once we start resolving, treat the previous pending state as consumed. If the
    // server emits a NEW approval-required (e.g. reject-and-replan and gate still on),
    // that event will re-populate state.pendingDecision.
    state.pendingDecision = null;

    const params = new URLSearchParams({
        approve: decision.approve ? 'true' : 'false',
        replan: decision.replan ? 'true' : 'false'
    });
    if (decision.feedback) params.set('feedback', decision.feedback);
    const provider = providerSelect.value;
    if (provider) params.set('provider', provider);

    state.abortController = new AbortController();

    try {
        const res = await fetch(`/api/conversations/${state.conversationId}/decision/stream?${params}`, {
            signal: state.abortController.signal,
            headers: apiHeaders()
        });
        if (!res.ok) {
            const handled = await handleApiErrorResponse(res);
            finalizeTurn({ cancelled: true });
            if (!handled) showError(`Server error: ${res.status}`);
            return;
        }

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
                if (line.startsWith('event: ')) evtType = line.slice(7).trim();
                else if (line.startsWith('data: ') && evtType) {
                    try {
                        const data = JSON.parse(line.slice(6));
                        if (!handleEvent(evtType, data)) break outer;
                    } catch { /* skip malformed JSON */ }
                    evtType = null;
                } else if (line === '') evtType = null;
            }
        }
        finalizeTurn({});
    } catch (err) {
        if (err.name === 'AbortError') setStatus(bubble, 'Cancelled', 'error');
        else { showError('Connection to server lost.'); setStatus(bubble, 'Disconnected', 'error'); }
        finalizeTurn({ cancelled: true });
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
/// Parses an ApiError response body and dispatches on the stable `code` field.
/// Returns true if the code was recognized (and the appropriate UI action taken),
/// false to let the caller fall back to a generic status-code message.
async function handleApiErrorResponse(res) {
    let body = null;
    try { body = await res.json(); } catch { /* not JSON */ }
    const code = body?.code;
    const message = body?.error || body?.detail;

    switch (code) {
        case 'auth.api_key_missing':
            promptForKey();
            return true;

        case 'conversation.not_found':
            showError('Conversation no longer exists.');
            return true;

        case 'conversation.busy':
            showError('Conversation is busy processing another request. Try again in a moment.');
            return true;

        case 'conversation.pending_decision_open': {
            // Extras carries the PendingDecisionDto — hydrate UI so user can resolve it
            // without a full page reload.
            const pending = body?.extras?.pendingDecision;
            if (pending) {
                state.pendingDecision = pending;
                showError('A previous draft is awaiting approval. Resolve it before sending a new message.');
                // Rebuild the paused bubble if it was not already on screen
                if (!document.querySelector('.approval-card:not([hidden])')) {
                    restorePendingDecisionUI(pending);
                }
            } else {
                showError('This conversation has a pending decision. Approve, reject, or cancel it before continuing.');
            }
            return true;
        }

        case 'conversation.pending_decision_missing':
            showError('No pending draft to resolve — it may have already been cancelled.');
            state.pendingDecision = null;
            return true;

        case 'validation.message_too_short':
        case 'validation.message_too_long':
        case 'validation.feedback_too_long':
            showError(message || 'Validation error.');
            return true;

        case 'provider.not_configured':
            showError(message || 'The selected LLM provider is not configured.');
            return true;

        case 'rate.limited':
            showError('Too many requests. Try again in a minute.');
            return true;

        case 'workflow.failed':
        case 'workflow.aggregator_empty':
            showError(message || 'The pipeline could not produce a plan.');
            return true;

        case 'internal.unhandled':
            showError(message || 'Something went wrong on the server.');
            return true;
    }

    // Fallback by HTTP status when no code (older endpoints, 5xx without body)
    switch (res.status) {
        case 401: promptForKey(); return true;
        case 404: showError('Not found.'); return true;
        case 409: showError('Conflict — refresh and try again.'); return true;
        case 429: showError('Too many requests. Try again in a minute.'); return true;
    }

    if (message) { showError(message); return true; }
    return false;
}

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
function formatDurationMs(ms) {
    if (ms == null) return '';
    if (ms < 1000) return `${ms}ms`;
    if (ms < 60000) return `${(ms / 1000).toFixed(1)}s`;
    const mins = Math.floor(ms / 60000);
    const secs = Math.floor((ms % 60000) / 1000);
    return `${mins}m ${secs}s`;
}
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
