// State
let renderTimer = null;
let startTime = null;
let activeTab = 'researcher';
let currentStreamingAgent = null;
let streamDone = false;
let abortController = null;

const agents = ['researcher', 'planner', 'accountant', 'auditor', 'aggregator'];
const allTabs = ['researcher', 'planner', 'accountant', 'auditor', 'aggregator', 'summary'];
const agentLabels = {
    researcher: 'Researching destination...',
    planner: 'Creating itinerary...',
    accountant: 'Analyzing budget...',
    auditor: 'Validating plan...',
    aggregator: 'Preparing final output...'
};

// Per-agent content storage (includes virtual 'summary' tab)
let agentContent = {};
function resetAgentContent() {
    agentContent = {};
    allTabs.forEach(a => agentContent[a] = '');
}
resetAgentContent();

// DOM refs
const form = document.getElementById('plan-form');
const requestInput = document.getElementById('request-input');
const providerSelect = document.getElementById('provider-select');
const submitBtn = document.getElementById('submit-btn');
const cancelBtn = document.getElementById('cancel-btn');
const pipelineSection = document.getElementById('pipeline-section');
const progressBar = document.getElementById('progress-bar');
const statusText = document.getElementById('status-text');
const outputSection = document.getElementById('output-section');
const outputMeta = document.getElementById('output-meta');
const outputScroll = document.getElementById('output-scroll');
const errorBanner = document.getElementById('error-banner');
const newPlanRow = document.getElementById('new-plan-row');

// Configure marked
marked.setOptions({ breaks: true, gfm: true });

// Tab switching
document.getElementById('agent-tabs').addEventListener('click', (e) => {
    const tab = e.target.closest('.agent-tab');
    if (!tab) return;
    switchTab(tab.dataset.tab);
});

function switchTab(tabName) {
    activeTab = tabName;
    // Update tab buttons
    document.querySelectorAll('.agent-tab').forEach(t => {
        t.classList.toggle('active', t.dataset.tab === tabName);
    });
    // Update panes
    document.querySelectorAll('.tab-pane').forEach(p => {
        p.classList.toggle('active', p.id === `pane-${tabName}`);
    });
    // Render current tab content
    renderAgentPane(tabName);
    // Scroll to top of output
    outputScroll.scrollTop = 0;
}

function renderAgentPane(agentName) {
    const pane = document.getElementById(`pane-${agentName}`);
    const content = agentContent[agentName];
    if (content) {
        pane.innerHTML = '<div class="markdown-body">' + marked.parse(content) + '</div>';
    } else {
        const labels = {
            researcher: 'Waiting for research data...',
            planner: 'Waiting for itinerary...',
            accountant: 'Waiting for budget analysis...',
            auditor: 'Waiting for audit report...',
            aggregator: 'Waiting for final travel plan...',
            summary: 'Waiting for summary of checks...'
        };
        pane.innerHTML = `<div class="tab-pane-empty">${labels[agentName]}</div>`;
    }
}

function markTabHasContent(agentName) {
    const tab = document.querySelector(`.agent-tab[data-tab="${agentName}"]`);
    if (tab) tab.classList.add('has-content');
}

function markTabStreaming(agentName, isStreaming) {
    document.querySelectorAll('.agent-tab').forEach(t => {
        t.classList.remove('streaming');
    });
    if (isStreaming) {
        const tab = document.querySelector(`.agent-tab[data-tab="${agentName}"]`);
        if (tab) tab.classList.add('streaming');
    }
}

function resetTabs() {
    document.querySelectorAll('.agent-tab').forEach(t => {
        t.classList.remove('has-content', 'streaming');
    });
    switchTab('researcher');
}

// Health check on load
async function checkHealth() {
    try {
        const res = await fetch('/api/travel/health');
        const data = await res.json();

        const ollamaBadge = document.getElementById('ollama-badge');
        const anthropicBadge = document.getElementById('anthropic-badge');

        ollamaBadge.innerHTML = '<span class="badge-dot"></span>Ollama: available';
        ollamaBadge.className = 'badge available';

        if (data.providers.anthropic === 'available') {
            anthropicBadge.innerHTML = '<span class="badge-dot"></span>Anthropic: available';
            anthropicBadge.className = 'badge available';
        } else {
            anthropicBadge.innerHTML = '<span class="badge-dot"></span>Anthropic: not configured';
            anthropicBadge.className = 'badge unavailable';
        }
    } catch {
        document.getElementById('ollama-badge').innerHTML = '<span class="badge-dot"></span>API: offline';
        document.getElementById('ollama-badge').className = 'badge unavailable';
        document.getElementById('anthropic-badge').style.display = 'none';
    }
}
checkHealth();

// Example chips
document.querySelectorAll('.chip').forEach(chip => {
    chip.addEventListener('click', () => {
        requestInput.value = chip.textContent;
        requestInput.focus();
    });
});

// Input limits (must match API validation)
const REQUEST_MIN_LENGTH = 10;
const REQUEST_MAX_LENGTH = 2000;

// Character counter
const charCount = document.getElementById('char-count');
requestInput.addEventListener('input', () => {
    const len = requestInput.value.length;
    charCount.textContent = `${len} / ${REQUEST_MAX_LENGTH}`;
    charCount.className = 'char-count'
        + (len >= REQUEST_MAX_LENGTH ? ' at-limit' : len >= REQUEST_MAX_LENGTH * 0.9 ? ' near-limit' : '');
});

// Form submit
form.addEventListener('submit', (e) => {
    e.preventDefault();
    const request = requestInput.value.trim();
    if (request.length < REQUEST_MIN_LENGTH) {
        showError(`Request must be at least ${REQUEST_MIN_LENGTH} characters.`);
        return;
    }
    if (request.length > REQUEST_MAX_LENGTH) {
        showError(`Request must not exceed ${REQUEST_MAX_LENGTH} characters.`);
        return;
    }
    errorBanner.classList.remove('visible');
    startStreaming(request, providerSelect.value);
});

// Cancel
cancelBtn.addEventListener('click', () => {
    if (abortController) {
        abortController.abort();
        abortController = null;
    }
    streamDone = true;
    statusText.textContent = 'Cancelled';
    markTabStreaming(null, false);
    resetFormState();
});

// New plan
document.getElementById('new-plan-btn').addEventListener('click', () => {
    requestInput.value = '';
    outputSection.classList.remove('visible');
    pipelineSection.classList.remove('visible');
    newPlanRow.style.display = 'none';
    errorBanner.classList.remove('visible');
    resetPipeline();
    resetTabs();
    resetAgentContent();
    requestInput.focus();
});

// Using fetch + ReadableStream instead of EventSource to avoid
// EventSource's built-in auto-reconnect which causes duplicate workflows.
async function startStreaming(request, provider) {
    // Reset state
    resetAgentContent();
    startTime = Date.now();
    streamDone = false;
    currentStreamingAgent = null;
    errorBanner.classList.remove('visible');
    outputMeta.innerHTML = '';
    outputSection.classList.remove('visible');
    newPlanRow.style.display = 'none';
    resetPipeline();
    resetTabs();

    // Show pipeline, toggle buttons
    pipelineSection.classList.add('visible');
    submitBtn.style.display = 'none';
    cancelBtn.style.display = 'inline-block';
    requestInput.disabled = true;
    providerSelect.disabled = true;
    statusText.textContent = 'Connecting...';

    // Build URL
    const params = new URLSearchParams({ request });
    if (provider) params.set('provider', provider);

    // AbortController for cancel support
    abortController = new AbortController();

    try {
        const response = await fetch(`/api/travel/plan/stream?${params}`, {
            signal: abortController.signal
        });

        if (!response.ok) {
            showError(`Server error: ${response.status}`);
            resetFormState();
            return;
        }

        const reader = response.body.getReader();
        const decoder = new TextDecoder();
        let buffer = '';

        while (true) {
            const { done, value } = await reader.read();
            if (done) break;

            buffer += decoder.decode(value, { stream: true });

            // Parse SSE events from the buffer
            const lines = buffer.split('\n');
            buffer = lines.pop(); // Keep incomplete line in buffer

            let currentEventType = null;
            for (const line of lines) {
                if (line.startsWith('event: ')) {
                    currentEventType = line.slice(7).trim();
                } else if (line.startsWith('data: ') && currentEventType) {
                    const jsonStr = line.slice(6);
                    try {
                        const data = JSON.parse(jsonStr);
                        handleSseEvent(currentEventType, data);
                    } catch { /* skip malformed JSON */ }
                    currentEventType = null;
                } else if (line === '') {
                    currentEventType = null;
                }
            }

            if (streamDone) break;
        }

        // Stream ended normally without a 'complete' event - treat as done
        if (!streamDone) {
            onStreamComplete();
        }
    } catch (err) {
        if (err.name === 'AbortError') {
            statusText.textContent = 'Cancelled';
        } else {
            showError('Connection to server lost.');
            statusText.textContent = 'Disconnected';
        }
        resetFormState();
    }
}

function handleSseEvent(eventType, data) {
    if (streamDone) return;

    switch (eventType) {
        case 'init':
            statusText.textContent = data.content || 'Starting pipeline...';
            break;

        case 'agent-start':
            currentStreamingAgent = data.agent;
            setActiveAgent(data.agent);
            updateProgress(data.progressPercent);
            statusText.textContent = agentLabels[data.agent] || `${data.agent} processing...`;
            markTabStreaming(data.agent, true);
            // Auto-switch to the streaming agent's tab
            switchTab(data.agent);
            break;

        case 'content':
            if (data.content && data.agent) {
                agentContent[data.agent] += data.content;
                markTabHasContent(data.agent);
                scheduleRender();
            }
            updateProgress(data.progressPercent);
            break;

        case 'agent-complete':
            markAgentComplete(data.agent);
            markTabStreaming(data.agent, false);
            markTabHasContent(data.agent);
            updateProgress(data.progressPercent);
            // Split auditor content: extract "Summary of Checks" into Summary tab
            if (data.agent === 'auditor') {
                splitAuditorSummary();
            }
            // Render final state for this agent
            renderAgentPane(data.agent);
            break;

        case 'complete':
            onStreamComplete();
            break;

        case 'error':
            showError(data.content || 'An error occurred');
            statusText.textContent = 'Failed';
            streamDone = true;
            markTabStreaming(null, false);
            resetFormState();
            break;
    }
}

function onStreamComplete() {
    streamDone = true;
    currentStreamingAgent = null;
    markAllComplete();
    markTabStreaming(null, false);
    updateProgress(100);

    const elapsed = ((Date.now() - startTime) / 1000).toFixed(1);
    statusText.textContent = `Completed in ${elapsed}s`;

    outputMeta.innerHTML = `<span>Completed in ${elapsed}s</span>`;
    outputSection.classList.add('visible');
    newPlanRow.style.display = 'block';
    resetFormState();

    // Ensure auditor content is split before final render
    splitAuditorSummary();

    // Final render of all panes and switch to aggregator (final output)
    if (renderTimer) { clearTimeout(renderTimer); renderTimer = null; }
    allTabs.forEach(a => renderAgentPane(a));
    switchTab('aggregator');
}

// Split auditor output into Audit (detailed scores) and Summary (verdict + issues).
// Tries multiple heading patterns since LLM output format varies.
function splitAuditorSummary() {
    const auditorText = agentContent['auditor'];
    if (!auditorText || agentContent['summary']) return;

    // Log all headings found in auditor output for debugging
    const allHeadings = auditorText.match(/^#{1,4}.+$/gm) || [];
    console.log('[splitAuditorSummary] Auditor headings found:', allHeadings);

    // Try split points in order of preference (broad patterns to handle emoji/formatting variations):
    const patterns = [
        /^#{1,4}\s*.*(?:Issues\s*Found|Issues\s*Identified)/mi,
        /^#{1,4}\s*.*FINAL\s*VERDICT/mi,
        /^#{1,4}\s*.*(?:verdict|decision|overall\s+score)/mi,
        /^#{1,4}\s*.*(?:\u{1F3AF}|\u{1F3C6})/mu,          // Emoji-based headings from template
        /^#{1,4}\s*.*(?:suggestion|improvement|recommendation)/mi,
    ];

    for (const pattern of patterns) {
        const match = auditorText.match(pattern);
        if (match) {
            const splitIndex = auditorText.indexOf(match[0]);
            // Only split if we're not grabbing almost everything (leave at least 20% for audit)
            if (splitIndex > auditorText.length * 0.2) {
                console.log('[splitAuditorSummary] Split at:', match[0]);
                agentContent['summary'] = auditorText.slice(splitIndex);
                agentContent['auditor'] = auditorText.slice(0, splitIndex).trimEnd();
                markTabHasContent('summary');
                renderAgentPane('summary');
                return;
            }
        }
    }

    console.log('[splitAuditorSummary] No split pattern matched. First 500 chars:', auditorText.substring(0, 500));
}

function resetFormState() {
    submitBtn.style.display = 'inline-block';
    cancelBtn.style.display = 'none';
    requestInput.disabled = false;
    providerSelect.disabled = false;
}

function showError(msg) {
    errorBanner.textContent = msg;
    errorBanner.classList.add('visible');
}

// Pipeline management
function resetPipeline() {
    document.querySelectorAll('.agent-step').forEach((step, i) => {
        step.className = 'agent-step';
        step.querySelector('.step-icon').textContent = i + 1;
    });
    document.querySelectorAll('.pipeline-connector').forEach(c => {
        c.classList.remove('filled');
    });
    progressBar.style.width = '0%';
}

function setActiveAgent(agentName) {
    const idx = agents.indexOf(agentName);
    document.querySelectorAll('.agent-step').forEach((step, i) => {
        if (step.dataset.agent === agentName) {
            step.className = 'agent-step active';
        } else if (i < idx) {
            if (!step.classList.contains('completed')) {
                step.className = 'agent-step completed';
                step.querySelector('.step-icon').textContent = '\u2713';
            }
        }
    });
    document.querySelectorAll('.pipeline-connector').forEach(c => {
        if (parseInt(c.dataset.index) < idx) {
            c.classList.add('filled');
        }
    });
}

function markAgentComplete(agentName) {
    const step = document.querySelector(`[data-agent="${agentName}"]`);
    if (step) {
        step.className = 'agent-step completed';
        step.querySelector('.step-icon').textContent = '\u2713';
    }
    const idx = agents.indexOf(agentName);
    const connector = document.querySelector(`.pipeline-connector[data-index="${idx}"]`);
    if (connector) connector.classList.add('filled');
}

function markAllComplete() {
    document.querySelectorAll('.agent-step').forEach(step => {
        step.className = 'agent-step completed';
        step.querySelector('.step-icon').textContent = '\u2713';
    });
    document.querySelectorAll('.pipeline-connector').forEach(c => {
        c.classList.add('filled');
    });
}

function updateProgress(percent) {
    progressBar.style.width = Math.min(100, Math.max(0, percent)) + '%';
}

// Throttled rendering - renders the active tab's content
function scheduleRender() {
    if (!renderTimer) {
        renderTimer = setTimeout(() => {
            renderAgentPane(activeTab);
            // Only auto-scroll if viewing the tab that's currently streaming
            if (activeTab === currentStreamingAgent) {
                outputScroll.scrollTop = outputScroll.scrollHeight;
            }
            renderTimer = null;
        }, 100);
    }
    // Show output section once we have content
    if (!outputSection.classList.contains('visible')) {
        outputSection.classList.add('visible');
    }
}
