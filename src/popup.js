const DEFAULT_PROMPTS = [
  ["answer", "Answer this", "Answer the selected question clearly and concisely."],
  ["explain", "Explain this", "Explain the selected content in plain language."],
  ["summarize", "Summarize", "Summarize the selected content into the key points."]
];

const endpoint = document.querySelector("#endpoint");
const apiKey = document.querySelector("#apiKey");
const model = document.querySelector("#model");
const promptList = document.querySelector("#promptList");
const status = document.querySelector("#status");
const refreshModels = document.querySelector("#refreshModels");
const theme = document.querySelector("#theme");
const resumeConversations = document.querySelector("#resumeConversations");
const activeOrigin = document.querySelector("#activeOrigin");
const conversationList = document.querySelector("#conversationList");
const addPrompt = document.querySelector("#addPrompt");
const debugEnabled = document.querySelector("#debugEnabled");
const debugList = document.querySelector("#debugList");
const copyLogs = document.querySelector("#copyLogs");
const clearLogs = document.querySelector("#clearLogs");

init();

window.matchMedia("(prefers-color-scheme: dark)").addEventListener("change", () => {
  applyResolvedTheme(theme.querySelector(".active")?.dataset.theme || "system");
});

refreshModels.addEventListener("click", async () => {
  await save();
  await loadModels();
});

for (const item of [endpoint, apiKey, model]) {
  item.addEventListener("change", save);
}

resumeConversations.addEventListener("change", save);
debugEnabled.addEventListener("change", save);

copyLogs.addEventListener("click", async () => {
  const logs = await chrome.runtime.sendMessage({ type: "getDebugLogs" });
  await navigator.clipboard.writeText(JSON.stringify(logs?.logs || [], null, 2));
  setStatus("Debug logs copied");
});

clearLogs.addEventListener("click", async () => {
  await chrome.runtime.sendMessage({ type: "clearDebugLogs" });
  await loadDebugLogs();
  setStatus("Debug logs cleared");
});

addPrompt.addEventListener("click", async () => {
  const prompts = readPrompts();
  prompts.push({
    id: `custom-${Date.now()}`,
    title: "Custom prompt",
    prompt: "Write your prompt here."
  });
  renderPrompts(prompts);
  await save();
});

theme.addEventListener("click", async (event) => {
  const button = event.target.closest("[data-theme]");
  if (!button) return;
  setTheme(button.dataset.theme);
  await save();
});

async function init() {
  const settings = await chrome.storage.local.get([
    "endpoint",
    "apiKey",
    "model",
    "theme",
    "resumeConversations",
    "debugEnabled",
    "prompts",
    "models"
  ]);
  endpoint.value = settings.endpoint || "http://127.0.0.1:20128";
  apiKey.value = settings.apiKey || "sk_9-router";
  resumeConversations.checked = settings.resumeConversations !== false;
  debugEnabled.checked = settings.debugEnabled !== false;
  setTheme(settings.theme || "system");
  renderModelOptions(settings.models || [], settings.model || "cx/gpt-5.5");
  renderPrompts(settings.prompts || toPromptObjects(DEFAULT_PROMPTS));
  await loadDashboard();
  await loadDebugLogs();
  await loadModels(false);
}

async function save() {
  const prompts = readPrompts();

  await chrome.storage.local.set({
    endpoint: endpoint.value.trim(),
    apiKey: apiKey.value.trim(),
    model: model.value.trim(),
    theme: theme.querySelector(".active")?.dataset.theme || "system",
    resumeConversations: resumeConversations.checked,
    debugEnabled: debugEnabled.checked,
    prompts
  });
  chrome.runtime.sendMessage({ type: "rebuildMenus" });
  setStatus("Saved");
}

async function loadModels(showStatus = true) {
  if (showStatus) setStatus("Loading models...");
  const result = await chrome.runtime.sendMessage({ type: "fetchModels" });
  if (!result?.ok) {
    setStatus(result?.error || "Could not fetch models");
    return;
  }
  const selected = model.value || "cx/gpt-5.5";
  await chrome.storage.local.set({ models: result.models });
  renderModelOptions(result.models, result.models.includes(selected) ? selected : selected);
  setStatus(`${result.models.length} models loaded`);
}

function renderModelOptions(models, selected) {
  const unique = [...new Set([selected, ...models].filter(Boolean))];
  model.innerHTML = "";
  for (const id of unique) {
    const option = document.createElement("option");
    option.value = id;
    option.textContent = id;
    option.selected = id === selected;
    model.append(option);
  }
}

function renderPrompts(prompts) {
  promptList.innerHTML = "";
  for (const prompt of prompts) {
    const row = document.createElement("div");
    row.className = "prompt";
    row.dataset.id = prompt.id;
    row.innerHTML = `
      <div class="prompt-top">
        <input class="prompt-title" value="">
        <button class="delete-prompt" title="Delete prompt">x</button>
      </div>
      <textarea class="prompt-body" rows="3"></textarea>
    `;
    row.querySelector(".prompt-title").value = prompt.title;
    row.querySelector(".prompt-body").value = prompt.prompt;
    row.addEventListener("change", save);
    row.querySelector(".delete-prompt").addEventListener("click", async () => {
      row.remove();
      await save();
    });
    promptList.append(row);
  }
}

function readPrompts() {
  return [...promptList.querySelectorAll(".prompt")].map((row) => ({
    id: row.dataset.id,
    title: row.querySelector(".prompt-title").value.trim(),
    prompt: row.querySelector(".prompt-body").value.trim()
  })).filter((item) => item.id && item.title && item.prompt);
}

function toPromptObjects(rows) {
  return rows.map(([id, title, prompt]) => ({ id, title, prompt }));
}

function setStatus(message) {
  status.textContent = message;
}

function setTheme(value) {
  for (const button of theme.querySelectorAll("button")) {
    button.classList.toggle("active", button.dataset.theme === value);
  }
  applyResolvedTheme(value);
}

function applyResolvedTheme(value) {
  document.documentElement.dataset.theme = value === "system"
    ? (window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light")
    : value;
}

async function loadDashboard() {
  const dashboard = await chrome.runtime.sendMessage({ type: "getDashboard" });
  if (!dashboard?.ok) return;
  activeOrigin.textContent = dashboard.activeOrigin || "No active site";
  resumeConversations.checked = dashboard.resumeConversations !== false;
  renderConversations(dashboard.conversations || [], dashboard.activeOrigin);
}

function renderConversations(conversations, currentOrigin) {
  conversationList.innerHTML = "";
  if (!conversations.length) {
    conversationList.innerHTML = `<div class="empty">No conversations yet</div>`;
    return;
  }

  for (const conversation of conversations.slice(0, 6)) {
    const item = document.createElement("div");
    item.className = "conversation";
    item.innerHTML = `
      <div>
        <strong>${escapeHtml(conversation.origin || "site")}</strong>
        <span>${conversation.messages?.length || 0} messages${conversation.origin === currentOrigin ? " - current" : ""}</span>
      </div>
      <button class="small-btn">Clear</button>
    `;
    item.querySelector("button").addEventListener("click", async () => {
      await chrome.runtime.sendMessage({ type: "clearConversation", origin: conversation.origin });
      await loadDashboard();
    });
    conversationList.append(item);
  }
}

async function loadDebugLogs() {
  const result = await chrome.runtime.sendMessage({ type: "getDebugLogs" });
  const logs = result?.logs || [];
  debugList.innerHTML = "";
  if (!logs.length) {
    debugList.innerHTML = `<div class="empty">No debug logs yet</div>`;
    return;
  }

  for (const log of logs.slice(-4).reverse()) {
    const row = document.createElement("details");
    row.className = "debug-row";
    row.innerHTML = `
      <summary>${new Date(log.at).toLocaleTimeString()} - ${escapeHtml(log.type)} - ${escapeHtml(log.data?.model || "")}</summary>
      <pre>${escapeHtml(JSON.stringify(log.data, null, 2))}</pre>
    `;
    debugList.append(row);
  }
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
