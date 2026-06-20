const DEFAULTS = {
  endpoint: "http://127.0.0.1:20128",
  apiKey: "sk_9-router",
  model: "cx/gpt-5.5",
  theme: "system",
  resumeConversations: true,
  debugEnabled: true,
  stealthMode: false,
  prompts: [
    {
      id: "answer",
      title: "Answer this",
      prompt: "Answer the selected question clearly and concisely."
    },
    {
      id: "explain",
      title: "Explain this",
      prompt: "Explain the selected content in plain language."
    },
    {
      id: "summarize",
      title: "Summarize",
      prompt: "Summarize the selected content into the key points."
    }
  ]
};

const SYSTEM_PROMPT = "You are a precise browser assistant. Return a direct, useful answer with clear formatting.";

chrome.runtime.onInstalled.addListener(async () => {
  const existing = await chrome.storage.local.get(Object.keys(DEFAULTS));
  await chrome.storage.local.set({ ...DEFAULTS, ...compact(existing) });
  await rebuildMenus();
});

chrome.runtime.onStartup.addListener(rebuildMenus);

chrome.storage.onChanged.addListener((changes, area) => {
  if (area === "local" && changes.prompts) rebuildMenus();
});

chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  const id = info.menuItemId.toString();
  const settings = await getSettings();

  if (id.startsWith("faf:prompt:")) {
    const prompt = findPrompt(settings, id.slice("faf:prompt:".length));
    await startJob(tab, settings, prompt, buildInput(info, tab));
    return;
  }

  if (id === "faf:snip") {
    await postToTab(tab?.id, { type: "ez9router:startSnip", promptMode: "default", stealthMode: settings.stealthMode });
    return;
  }

  if (id.startsWith("faf:snipPrompt:")) {
    await postToTab(tab?.id, {
      type: "ez9router:startSnip",
      promptMode: "prompt",
      promptId: id.slice("faf:snipPrompt:".length),
      stealthMode: settings.stealthMode
    });
    return;
  }

  if (id === "faf:snipCustom") {
    await postToTab(tab?.id, { type: "ez9router:startSnip", promptMode: "custom", stealthMode: settings.stealthMode });
    return;
  }

  if (id === "faf:fullHtml") {
    const page = await postToTab(tab?.id, { type: "ez9router:getHtml" });
    const prompt = {
      id: "full-html",
      title: "Full HTML",
      prompt: "Analyze the full page HTML. Extract the useful answer or summarize the page structure clearly."
    };
    await startJob(tab, settings, prompt, {
      text: page?.html || "",
      pageUrl: tab?.url || "",
      kind: "html"
    });
  }
});


chrome.commands.onCommand.addListener(async (command) => {
  const settings = await getSettings();
  const tab = await getActiveTab();
  if (!tab?.id) return;

  if (command === "ez-answer-selection") {
    const selection = await postToTab(tab.id, { type: "ez9router:getSelection" });
    const prompt = findPrompt(settings, "answer");
    await startJob(tab, settings, prompt, {
      text: selection?.text || "",
      pageUrl: tab.url || "",
      kind: "selection"
    });
    return;
  }

  if (command === "ez-snip") {
    await postToTab(tab.id, { type: "ez9router:startSnip", promptMode: "default", stealthMode: settings.stealthMode });
    return;
  }

  if (command === "ez-send-html") {
    const page = await postToTab(tab.id, { type: "ez9router:getHtml" });
    await startJob(tab, settings, {
      id: "full-html",
      title: "Full HTML",
      prompt: "Analyze the full page HTML. Extract the useful answer or summarize the page structure clearly."
    }, {
      text: page?.html || "",
      pageUrl: tab.url || "",
      kind: "html"
    });
  }
});
chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message?.type === "fetchModels") {
    fetchModels().then(sendResponse);
    return true;
  }
  if (message?.type === "testModel") {
    testModel(message.model).then(sendResponse);
    return true;
  }
  if (message?.type === "rebuildMenus") {
    rebuildMenus().then(() => sendResponse({ ok: true }));
    return true;
  }
  if (message?.type === "ez9router:snipComplete") {
    handleSnipComplete(message, sender.tab).then(sendResponse);
    return true;
  }
  if (message?.type === "getDashboard") {
    getDashboard().then(sendResponse);
    return true;
  }
  if (message?.type === "clearConversation") {
    clearConversation(message.origin).then(sendResponse);
    return true;
  }
  if (message?.type === "getDebugLogs") {
    getDebugLogs().then(sendResponse);
    return true;
  }
  if (message?.type === "clearDebugLogs") {
    clearDebugLogs().then(sendResponse);
    return true;
  }
});

async function rebuildMenus() {
  await chrome.contextMenus.removeAll();
  const { prompts } = await getSettings();

  for (const prompt of prompts) {
    chrome.contextMenus.create({
      id: `faf:prompt:${prompt.id}`,
      title: prompt.title,
      contexts: ["selection", "image"]
    });
  }

  chrome.contextMenus.create({
    id: "faf:snip",
    title: "Snip mode",
    contexts: ["page"]
  });

  chrome.contextMenus.create({
    id: "faf:snipPromptRoot",
    title: "Snip mode (with prompt)",
    contexts: ["page"]
  });

  for (const prompt of prompts) {
    chrome.contextMenus.create({
      id: `faf:snipPrompt:${prompt.id}`,
      parentId: "faf:snipPromptRoot",
      title: prompt.title,
      contexts: ["page"]
    });
  }

  chrome.contextMenus.create({
    id: "faf:snipCustom",
    parentId: "faf:snipPromptRoot",
    title: "Custom...",
    contexts: ["page"]
  });

  chrome.contextMenus.create({
    id: "faf:fullHtml",
    title: "Send full HTML",
    contexts: ["page"]
  });
}

async function handleSnipComplete(message, tab) {
  try {
    const settings = await getSettings();
    const prompt = await resolveSnipPrompt(settings, message);
    const snip = await cropVisibleTab(tab.windowId, message.rect, message.devicePixelRatio || 1);
    await startJob(tab, settings, prompt, {
      imageUrl: snip.dataUrl,
      imageMeta: snip.meta,
      pageUrl: tab?.url || "",
      kind: "snip"
    });
    return { ok: true };
  } catch (error) {
    return { ok: false, error: error.message };
  }
}

async function resolveSnipPrompt(settings, message) {
  if (message.promptMode === "custom") {
    return {
      id: "custom",
      title: "Custom snip",
      prompt: message.customPrompt || "Analyze this browser snip."
    };
  }

  if (message.promptMode === "prompt") {
    return findPrompt(settings, message.promptId);
  }

  return {
    id: "snip",
    title: "Snip mode",
    prompt: "Analyze this browser snip and answer with the useful details."
  };
}

async function cropVisibleTab(windowId, rect, devicePixelRatio) {
  const screenshot = await chrome.tabs.captureVisibleTab(windowId, { format: "png" });
  const image = await createImageBitmap(await (await fetch(screenshot)).blob());
  const scale = devicePixelRatio || 1;
  const sx = Math.max(0, Math.round(rect.left * scale));
  const sy = Math.max(0, Math.round(rect.top * scale));
  const sw = Math.max(1, Math.round(rect.width * scale));
  const sh = Math.max(1, Math.round(rect.height * scale));
  const maxSide = 1280;
  const ratio = Math.min(1, maxSide / Math.max(sw, sh));
  const outW = Math.max(1, Math.round(sw * ratio));
  const outH = Math.max(1, Math.round(sh * ratio));
  const canvas = new OffscreenCanvas(outW, outH);
  const ctx = canvas.getContext("2d");
  ctx.drawImage(image, sx, sy, sw, sh, 0, 0, outW, outH);
  const blob = await canvas.convertToBlob({ type: "image/jpeg", quality: 0.86 });
  return {
    dataUrl: await blobToDataUrl(blob),
    meta: {
      sourceWidth: sw,
      sourceHeight: sh,
      width: outW,
      height: outH,
      type: blob.type,
      bytes: blob.size
    }
  };
}

async function startJob(tab, settings, prompt, input) {
  const jobId = crypto.randomUUID();
  const origin = getOrigin(input.pageUrl || tab?.url || "");
  const createdAt = Date.now();
  const anchor = await postToTab(tab?.id, { type: "ez9router:getContextPoint" });
  const job = {
    id: jobId,
    status: "running",
    title: prompt.title,
    model: settings.model,
    input,
    origin,
    theme: settings.theme,
    stealthMode: settings.stealthMode,
    anchor: anchor?.point || null,
    createdAt
  };

  await chrome.storage.session.set({ [`job:${jobId}`]: job });

  const opened = await postToTab(tab?.id, { type: "ez9router:showJob", job });
  if (!opened) {
    chrome.windows.create({
      url: chrome.runtime.getURL(`src/result.html?job=${encodeURIComponent(jobId)}`),
      type: "popup",
      width: 720,
      height: 820
    });
  }

  runJob(jobId, settings, prompt, input, tab?.id, origin);
}

async function fetchModels() {
  try {
    const settings = await getSettings();
    const response = await fetch(`${trimSlash(settings.endpoint)}/v1/models`, {
      headers: { Authorization: `Bearer ${settings.apiKey}`,
        "X-Api-Key": settings.apiKey || "" }
    });
    const body = await response.json().catch(() => ({}));
    if (!response.ok) {
      return { ok: false, error: body.error?.message || `HTTP ${response.status}` };
    }
    return {
      ok: true,
      models: Array.isArray(body.data) ? body.data.map((model) => model.id).filter(Boolean) : []
    };
  } catch (error) {
    return { ok: false, error: error.message };
  }
}

async function testModel(model) {
  const settings = await getSettings();
  const target = model || settings.model;
  const startedAt = Date.now();
  try {
    const response = await fetch(`${trimSlash(settings.endpoint)}/v1/chat/completions`, {
      method: "POST",
      headers: {
        Authorization: `Bearer ${settings.apiKey}`,
        "X-Api-Key": settings.apiKey || "",
        "Content-Type": "application/json"
      },
      body: JSON.stringify({
        model: target,
        messages: [{ role: "user", content: "Hi" }],
        stream: false
      })
    });
    const raw = await response.text();
    const body = parseJson(raw);
    const result = {
      model: target,
      ok: response.ok,
      status: response.status,
      durationMs: Date.now() - startedAt,
      message: response.ok
        ? body.choices?.[0]?.message?.content || "OK"
        : body?.error?.message || raw.slice(0, 1000),
      hint: response.ok ? "" : buildProviderHint(target, {}, body, raw)
    };
    await logDebug(settings, response.ok ? "model-test" : "model-test-error", result);
    return result;
  } catch (error) {
    const result = {
      model: target,
      ok: false,
      status: 0,
      durationMs: Date.now() - startedAt,
      message: error.message,
      hint: "Could not reach 9router or the selected provider."
    };
    await logDebug(settings, "model-test-error", result);
    return result;
  }
}

async function runJob(jobId, settings, prompt, input, tabId, origin) {
  const startedAt = Date.now();
  let requestSummary = null;
  try {
    const history = settings.resumeConversations ? await getConversationMessages(origin) : [];
    const userContent = await buildMessageContent(prompt, input);
    const messages = [
      { role: "system", content: SYSTEM_PROMPT },
      ...history,
      { role: "user", content: userContent }
    ];

    requestSummary = summarizeRequest(settings, messages, input, prompt);
    await logDebug(settings, "request", requestSummary);

    const response = await fetch(`${trimSlash(settings.endpoint)}/v1/chat/completions`, {
      method: "POST",
      headers: {
        Authorization: `Bearer ${settings.apiKey}`,
        "X-Api-Key": settings.apiKey || "",
        "Content-Type": "application/json"
      },
      body: JSON.stringify({
        model: settings.model,
        messages,
        stream: false
      })
    });

    const raw = await response.text();
    const body = parseJson(raw);
    if (!response.ok) {
      const details = {
        status: response.status,
        statusText: response.statusText,
        providerCode: body?.error?.code || body?.code || null,
        providerMessage: body?.error?.message || body?.message || raw.slice(0, 1200),
        raw: raw.slice(0, 3000),
        hint: buildProviderHint(settings.model, input, body, raw)
      };
      await logDebug(settings, "error", { ...requestSummary, details, durationMs: Date.now() - startedAt });
      throw Object.assign(new Error(details.providerMessage || `HTTP ${response.status}`), { details });
    }

    const answer = body.choices?.[0]?.message?.content || "";
    await logDebug(settings, "response", {
      ...requestSummary,
      durationMs: Date.now() - startedAt,
      usage: body.usage || null,
      answerChars: answer.length
    });
    await appendConversation(origin, input.pageUrl, userContent, answer);
    await updateJob(jobId, {
      status: "done",
      answer,
      usage: body.usage || null
    }, tabId);
  } catch (error) {
    if (!error.details) {
      await logDebug(settings, "error", {
        ...(requestSummary || summarizeRequest(settings, [], input, prompt)),
        durationMs: Date.now() - startedAt,
        details: { message: error.message }
      });
    }
    await updateJob(jobId, {
      status: "error",
      error: error.message,
      errorDetails: error.details || null
    }, tabId);
  }
}

async function buildMessageContent(prompt, input) {
  const selected = input.text || input.imageUrl || "";
  const text = `${prompt.prompt}\n\nSelected content:\n${selected}`;
  if (!input.imageUrl) return text;

  const imageUrl = await toDataUrl(input.imageUrl);
  return [
    { type: "text", text },
    { type: "image_url", image_url: { url: imageUrl } }
  ];
}

async function toDataUrl(url) {
  if (url.startsWith("data:")) return url;
  const response = await fetch(url);
  if (!response.ok) throw new Error(`Could not load image: HTTP ${response.status}`);
  return await blobToDataUrl(await response.blob());
}

async function blobToDataUrl(blob) {
  const bytes = new Uint8Array(await blob.arrayBuffer());
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return `data:${blob.type || "application/octet-stream"};base64,${btoa(binary)}`;
}

async function updateJob(jobId, patch, tabId) {
  const key = `job:${jobId}`;
  const existing = (await chrome.storage.session.get(key))[key] || {};
  const job = { ...existing, ...patch, updatedAt: Date.now() };
  await chrome.storage.session.set({ [key]: job });
  await postToTab(tabId, { type: "ez9router:updateJob", job });
}

function buildInput(info, tab) {
  return {
    text: info.selectionText || "",
    imageUrl: info.srcUrl || "",
    pageUrl: info.pageUrl || tab?.url || "",
    kind: info.srcUrl ? "image" : "selection"
  };
}

async function getSettings() {
  const values = await chrome.storage.local.get(Object.keys(DEFAULTS));
  return { ...DEFAULTS, ...compact(values) };
}

function findPrompt(settings, id) {
  return settings.prompts.find((item) => item.id === id) || settings.prompts[0];
}

async function getConversationMessages(origin) {
  if (!origin) return [];
  const conversation = await getConversation(origin);
  return (conversation.messages || []).slice(-10).map(({ role, content }) => ({ role, content }));
}

async function appendConversation(origin, pageUrl, userContent, answer) {
  if (!origin) return;
  const all = await getConversationStore();
  const existing = all[origin] || { origin, title: origin, messages: [] };
  const textContent = Array.isArray(userContent)
    ? userContent.find((part) => part.type === "text")?.text || "Image request"
    : userContent;

  all[origin] = {
    ...existing,
    origin,
    title: existing.title || origin,
    pageUrl: pageUrl || existing.pageUrl || "",
    updatedAt: Date.now(),
    messages: [
      ...(existing.messages || []),
      { role: "user", content: clip(textContent, 6000), at: Date.now() },
      { role: "assistant", content: clip(answer, 6000), at: Date.now() }
    ].slice(-20)
  };
  await chrome.storage.local.set({ conversations: all });
}

async function getConversation(origin) {
  const all = await getConversationStore();
  return all[origin] || { origin, title: origin, messages: [] };
}

async function getConversationStore() {
  const { conversations } = await chrome.storage.local.get("conversations");
  return conversations || {};
}

async function clearConversation(origin) {
  const all = await getConversationStore();
  delete all[origin];
  await chrome.storage.local.set({ conversations: all });
  return { ok: true };
}

async function getDashboard() {
  const settings = await getSettings();
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  const activeOrigin = getOrigin(tab?.url || "");
  const conversations = Object.values(await getConversationStore())
    .sort((a, b) => (b.updatedAt || 0) - (a.updatedAt || 0));
  return {
    ok: true,
    activeOrigin,
    resumeConversations: settings.resumeConversations,
    conversations
  };
}


async function getActiveTab() {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  return tab || null;
}
function getOrigin(url) {
  try {
    const parsed = new URL(url);
    return parsed.origin;
  } catch {
    return "";
  }
}

function compact(value) {
  return Object.fromEntries(Object.entries(value).filter(([, item]) => item !== undefined && item !== ""));
}

function trimSlash(value) {
  return value.replace(/\/+$/, "");
}

function parseJson(value) {
  try {
    return JSON.parse(value);
  } catch {
    return {};
  }
}

function summarizeRequest(settings, messages, input, prompt) {
  return {
    endpoint: `${trimSlash(settings.endpoint)}/v1/chat/completions`,
    model: settings.model,
    promptId: prompt.id,
    promptTitle: prompt.title,
    inputKind: input.kind,
    pageUrl: input.pageUrl || "",
    imageMeta: input.imageMeta || summarizeImageUrl(input.imageUrl),
    messageCount: messages.length,
    messageShape: messages.map((message) => ({
      role: message.role,
      content: Array.isArray(message.content)
        ? message.content.map((part) => part.type)
        : "text"
    }))
  };
}

function summarizeImageUrl(value = "") {
  if (!value) return null;
  const match = value.match(/^data:([^;]+);base64,(.*)$/);
  if (!match) return { type: "url", chars: value.length };
  return {
    type: match[1],
    bytesApprox: Math.round(match[2].length * 0.75),
    chars: value.length
  };
}

function buildProviderHint(model, input, body, raw) {
  const text = `${body?.error?.message || ""}\n${raw || ""}`.toLowerCase();
  if (model === "ag/gemini-3.1-pro-high" && text.includes("invalid_argument")) {
    return "This 9router model alias currently rejects even a minimal text request. Use ag/gemini-3.1-pro-low, ag/gemini-3-flash, or cx/gpt-5.5 for now.";
  }
  if (input.imageUrl) {
    return "The selected model/provider rejected the image payload. Try cx/gpt-5.5, reduce snip size, or choose a known vision-capable model.";
  }
  if (model.startsWith("ag/") && text.includes("invalid_argument")) {
    return "The Antigravity route rejected the request shape or model alias. Test the model from the popup and try another ag/* alias if it fails.";
  }
  return "";
}

async function logDebug(settings, type, data) {
  if (!settings.debugEnabled) return;
  const { debugLogs } = await chrome.storage.local.get("debugLogs");
  const logs = Array.isArray(debugLogs) ? debugLogs : [];
  logs.push({
    id: crypto.randomUUID(),
    at: Date.now(),
    type,
    data
  });
  await chrome.storage.local.set({ debugLogs: logs.slice(-80) });
}

async function getDebugLogs() {
  const { debugLogs } = await chrome.storage.local.get("debugLogs");
  return { ok: true, logs: Array.isArray(debugLogs) ? debugLogs : [] };
}

async function clearDebugLogs() {
  await chrome.storage.local.set({ debugLogs: [] });
  return { ok: true };
}

function clip(value, max) {
  if (!value || value.length <= max) return value || "";
  return `${value.slice(0, max)}\n\n[truncated]`;
}

async function postToTab(tabId, message) {
  if (!tabId) return false;
  try {
    return await chrome.tabs.sendMessage(tabId, message);
  } catch {
    return false;
  }
}
