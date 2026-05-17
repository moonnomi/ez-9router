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

theme.addEventListener("click", async (event) => {
  const button = event.target.closest("[data-theme]");
  if (!button) return;
  setTheme(button.dataset.theme);
  await save();
});

async function init() {
  const settings = await chrome.storage.local.get(["endpoint", "apiKey", "model", "theme", "prompts", "models"]);
  endpoint.value = settings.endpoint || "http://127.0.0.1:20128";
  apiKey.value = settings.apiKey || "sk_9-router";
  setTheme(settings.theme || "system");
  renderModelOptions(settings.models || [], settings.model || "cx/gpt-5.5");
  renderPrompts(settings.prompts || toPromptObjects(DEFAULT_PROMPTS));
  await loadModels(false);
}

async function save() {
  const prompts = [...promptList.querySelectorAll(".prompt")].map((row) => ({
    id: row.dataset.id,
    title: row.querySelector(".prompt-title").value.trim(),
    prompt: row.querySelector(".prompt-body").value.trim()
  })).filter((item) => item.id && item.title && item.prompt);

  await chrome.storage.local.set({
    endpoint: endpoint.value.trim(),
    apiKey: apiKey.value.trim(),
    model: model.value.trim(),
    theme: theme.querySelector(".active")?.dataset.theme || "system",
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
      <input class="prompt-title" value="">
      <textarea class="prompt-body" rows="3"></textarea>
    `;
    row.querySelector(".prompt-title").value = prompt.title;
    row.querySelector(".prompt-body").value = prompt.prompt;
    row.addEventListener("change", save);
    promptList.append(row);
  }
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
