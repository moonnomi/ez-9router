const ROOT_ID = "ez9router-root";
let currentJob = null;

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (message?.type === "ez9router:showJob") {
    currentJob = message.job;
    positionRoot(ensureRoot());
    render();
    sendResponse({ ok: true });
  }

  if (message?.type === "ez9router:updateJob") {
    currentJob = { ...currentJob, ...message.job };
    render();
    sendResponse({ ok: true });
  }
});

function render() {
  const root = ensureRoot();
  root.dataset.theme = resolveTheme(currentJob?.theme);
  root.innerHTML = `
    <div class="ez9-card" role="dialog" aria-live="polite">
      <div class="ez9-glow"></div>
      <header class="ez9-head">
        <div>
          <div class="ez9-kicker">ez-9router</div>
          <h2>${escapeHtml(currentJob?.title || "Answer")}</h2>
        </div>
        <button class="ez9-icon" data-action="close" title="Close">x</button>
      </header>
      <div class="ez9-meta">
        <span>${escapeHtml(currentJob?.model || "model")}</span>
        <span>${renderStatusLabel()}</span>
      </div>
      ${renderBody()}
      ${renderSource()}
      <footer class="ez9-actions">
        <button class="ez9-btn" data-action="copy">Copy</button>
        <button class="ez9-btn ghost" data-action="close">Dismiss</button>
      </footer>
    </div>
  `;

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
  const rect = getSelectionRect();
  if (!rect) {
    root.style.top = "24px";
    root.style.right = "24px";
    return;
  }

  const width = Math.min(420, window.innerWidth - 28);
  const left = clamp(rect.left, 14, window.innerWidth - width - 14);
  const top = clamp(rect.bottom + 12, 14, window.innerHeight - 180);
  root.style.left = `${left}px`;
  root.style.top = `${top}px`;
  root.style.width = `${width}px`;
}

function getSelectionRect() {
  const selection = window.getSelection();
  if (!selection || selection.rangeCount === 0 || selection.isCollapsed) return null;
  const rect = selection.getRangeAt(0).getBoundingClientRect();
  if (!rect.width && !rect.height) return null;
  return rect;
}

function renderBody() {
  if (currentJob?.status === "running") {
    return `
      <div class="ez9-thinking">
        <span></span><span></span><span></span>
        <p>Thinking through it</p>
      </div>
    `;
  }

  if (currentJob?.status === "error") {
    return `<div class="ez9-error">${escapeHtml(currentJob.error || "Request failed.")}</div>`;
  }

  return `<article class="ez9-answer">${renderMarkdownLite(currentJob?.answer || "")}</article>`;
}

function renderSource() {
  const value = currentJob?.input?.text || currentJob?.input?.imageUrl || "";
  if (!value) return "";
  return `
    <details class="ez9-source">
      <summary>Selection</summary>
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
