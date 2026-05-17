const ROOT_ID = "ez9router-root";
const SNIP_ID = "ez9router-snip";
let currentJob = null;
let savedPosition = null;
let lastContextPoint = null;

document.addEventListener("contextmenu", (event) => {
  lastContextPoint = { x: event.clientX, y: event.clientY };
}, true);

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (message?.type === "ez9router:showJob") {
    currentJob = message.job;
    render();
    sendResponse({ ok: true });
  }

  if (message?.type === "ez9router:updateJob") {
    currentJob = { ...currentJob, ...message.job };
    render();
    sendResponse({ ok: true });
  }

  if (message?.type === "ez9router:startSnip") {
    startSnip(message);
    sendResponse({ ok: true });
  }

  if (message?.type === "ez9router:getHtml") {
    sendResponse({
      ok: true,
      html: document.documentElement.outerHTML,
      title: document.title,
      url: location.href
    });
  }

  if (message?.type === "ez9router:getContextPoint") {
    sendResponse({ ok: true, point: lastContextPoint });
  }
});

function render() {
  const root = ensureRoot();
  root.dataset.theme = resolveTheme(currentJob?.theme);
  root.innerHTML = `
    <div class="ez9-card" role="dialog" aria-live="polite">
      <div class="ez9-glow"></div>
      <header class="ez9-head" data-drag-handle>
        <div class="ez9-title-row">
          <div class="ez9-spark">9</div>
          <div>
          <div class="ez9-kicker">ez-9router</div>
          <h2>${escapeHtml(currentJob?.title || "Answer")}</h2>
          </div>
        </div>
        <button class="ez9-icon" data-action="close" title="Close">x</button>
      </header>
      <div class="ez9-meta">
        <span>${escapeHtml(currentJob?.model || "model")}</span>
        <span class="ez9-status ${escapeHtml(currentJob?.status || "running")}">${renderStatusLabel()}</span>
      </div>
      ${renderBody()}
      ${renderSource()}
      <footer class="ez9-actions">
        <button class="ez9-btn" data-action="copy">Copy</button>
        <button class="ez9-btn ghost" data-action="close">Dismiss</button>
      </footer>
    </div>
  `;

  attachDrag(root);
  root.querySelectorAll("[data-action='close']").forEach((button) => {
    button.addEventListener("click", () => root.remove());
  });
  root.querySelector("[data-action='copy']")?.addEventListener("click", async (event) => {
    await navigator.clipboard.writeText(currentJob?.answer || "");
    event.currentTarget.textContent = "Copied";
    setTimeout(() => (event.currentTarget.textContent = "Copy"), 1200);
  });
}

function ensureRoot() {
  let root = document.getElementById(ROOT_ID);
  if (root) return root;

  root = document.createElement("div");
  root.id = ROOT_ID;
  document.documentElement.append(root);
  positionRoot(root);
  return root;
}

function positionRoot(root) {
  const width = Math.min(300, window.innerWidth - 24);
  const anchor = currentJob?.anchor;
  const preferredLeft = anchor ? anchor.x + 14 : Math.max(12, window.innerWidth - width - 18);
  const fallbackLeft = anchor ? anchor.x - width - 14 : preferredLeft;
  const left = savedPosition?.left ?? (preferredLeft + width < window.innerWidth - 12 ? preferredLeft : fallbackLeft);
  const top = savedPosition?.top ?? (anchor ? anchor.y - 8 : 18);
  root.style.left = `${clamp(left, 14, window.innerWidth - width - 14)}px`;
  root.style.top = `${clamp(top, 14, window.innerHeight - 160)}px`;
  root.style.right = "auto";
  root.style.width = `${width}px`;
}

function attachDrag(root) {
  const handle = root.querySelector("[data-drag-handle]");
  if (!handle) return;

  handle.addEventListener("pointerdown", (event) => {
    if (event.target.closest("button")) return;
    const startX = event.clientX;
    const startY = event.clientY;
    const box = root.getBoundingClientRect();
    handle.setPointerCapture(event.pointerId);
    root.classList.add("dragging");

    const move = (moveEvent) => {
      const left = clamp(box.left + moveEvent.clientX - startX, 8, window.innerWidth - root.offsetWidth - 8);
      const top = clamp(box.top + moveEvent.clientY - startY, 8, window.innerHeight - 80);
      savedPosition = { left, top };
      root.style.left = `${left}px`;
      root.style.top = `${top}px`;
    };

    const up = () => {
      root.classList.remove("dragging");
      handle.removeEventListener("pointermove", move);
      handle.removeEventListener("pointerup", up);
      handle.removeEventListener("pointercancel", up);
    };

    handle.addEventListener("pointermove", move);
    handle.addEventListener("pointerup", up);
    handle.addEventListener("pointercancel", up);
  });
}

function startSnip(config) {
  document.getElementById(SNIP_ID)?.remove();
  const layer = document.createElement("div");
  layer.id = SNIP_ID;
  layer.innerHTML = `
    <div class="ez9-snip-help">Drag to snip. Esc cancels.</div>
    <div class="ez9-snip-box"></div>
  `;
  document.documentElement.append(layer);

  const box = layer.querySelector(".ez9-snip-box");
  let start = null;

  const onDown = (event) => {
    event.preventDefault();
    start = { x: event.clientX, y: event.clientY };
    drawBox(box, start.x, start.y, 1, 1);
    layer.setPointerCapture(event.pointerId);
  };

  const onMove = (event) => {
    if (!start) return;
    const left = Math.min(start.x, event.clientX);
    const top = Math.min(start.y, event.clientY);
    drawBox(box, left, top, Math.abs(event.clientX - start.x), Math.abs(event.clientY - start.y));
  };

  const onUp = async (event) => {
    if (!start) return;
    const rect = normalizeRect(start.x, start.y, event.clientX, event.clientY);
    cleanup();
    if (rect.width < 8 || rect.height < 8) return;
    const customPrompt = config.promptMode === "custom"
      ? window.prompt("Prompt for this snip", "Answer what is shown in this snip.")
      : "";
    if (config.promptMode === "custom" && !customPrompt) return;
    await chrome.runtime.sendMessage({
      type: "ez9router:snipComplete",
      promptMode: config.promptMode,
      promptId: config.promptId,
      customPrompt,
      rect,
      devicePixelRatio: window.devicePixelRatio || 1
    });
  };

  const onKey = (event) => {
    if (event.key === "Escape") cleanup();
  };

  const cleanup = () => {
    layer.removeEventListener("pointerdown", onDown);
    layer.removeEventListener("pointermove", onMove);
    layer.removeEventListener("pointerup", onUp);
    document.removeEventListener("keydown", onKey);
    layer.remove();
  };

  layer.addEventListener("pointerdown", onDown);
  layer.addEventListener("pointermove", onMove);
  layer.addEventListener("pointerup", onUp);
  document.addEventListener("keydown", onKey);
}

function drawBox(box, left, top, width, height) {
  box.style.left = `${left}px`;
  box.style.top = `${top}px`;
  box.style.width = `${width}px`;
  box.style.height = `${height}px`;
}

function normalizeRect(x1, y1, x2, y2) {
  return {
    left: Math.min(x1, x2),
    top: Math.min(y1, y2),
    width: Math.abs(x2 - x1),
    height: Math.abs(y2 - y1)
  };
}

function renderBody() {
  if (currentJob?.status === "running") {
    return `
      <div class="ez9-thinking">
        <p>Thinking</p>
      </div>
    `;
  }

  if (currentJob?.status === "error") {
    return `
      <div class="ez9-error">
        <strong>${escapeHtml(currentJob.error || "Request failed.")}</strong>
        ${renderErrorDetails()}
      </div>
    `;
  }

  return `<article class="ez9-answer">${renderMarkdownLite(currentJob?.answer || "")}</article>`;
}

function renderErrorDetails() {
  const details = currentJob?.errorDetails;
  if (!details) return "";
  return `
    <details>
      <summary>Debug details</summary>
      <pre>${escapeHtml(JSON.stringify(details, null, 2))}</pre>
    </details>
  `;
}

function renderSource() {
  const value = currentJob?.input?.kind === "snip"
    ? "Browser snip"
    : (currentJob?.input?.text || currentJob?.input?.imageUrl || "");
  if (!value) return "";
  return `
    <details class="ez9-source">
      <summary>Source</summary>
      <pre>${escapeHtml(value)}</pre>
    </details>
  `;
}

function renderStatusLabel() {
  if (currentJob?.status === "done" && currentJob?.usage?.total_tokens) {
    return `${currentJob.usage.total_tokens} tokens`;
  }
  if (currentJob?.status === "error") return "error";
  return "working";
}

function resolveTheme(theme) {
  if (theme === "dark" || theme === "light") return theme;
  return window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
}

function renderMarkdownLite(text) {
  const escaped = escapeHtml(text);
  return escaped
    .replace(/^### (.*)$/gm, "<h3>$1</h3>")
    .replace(/^## (.*)$/gm, "<h2>$1</h2>")
    .replace(/^# (.*)$/gm, "<h1>$1</h1>")
    .replace(/^\- (.*)$/gm, "<li>$1</li>")
    .replace(/(<li>.*<\/li>)/gs, "<ul>$1</ul>")
    .replace(/\*\*(.*?)\*\*/g, "<strong>$1</strong>")
    .replace(/\n{2,}/g, "</p><p>")
    .replace(/\n/g, "<br>")
    .replace(/^/, "<p>")
    .replace(/$/, "</p>");
}

function escapeHtml(value = "") {
  return value.replace(/[&<>"']/g, (char) => ({
    "&": "&amp;",
    "<": "&lt;",
    ">": "&gt;",
    "\"": "&quot;",
    "'": "&#039;"
  })[char]);
}

function clamp(value, min, max) {
  return Math.min(Math.max(value, min), max);
}
