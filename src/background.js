const DEFAULTS = {
  endpoint: "http://127.0.0.1:20128",
  apiKey: "sk_9-router",
  model: "cx/gpt-5.5",
  theme: "system",
  resumeConversations: true,
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
    await postToTab(tab?.id, { type: "ez9router:startSnip", promptMode: "default" });
    return;
  }

  if (id.startsWith("faf:snipPrompt:")) {
    await postToTab(tab?.id, {
      type: "ez9router:startSnip",
      promptMode: "prompt",
      promptId: id.slice("faf:snipPrompt:".length)
    });
    return;
  }

  if (id === "faf:snipCustom") {
    await postToTab(tab?.id, { type: "ez9router:startSnip", promptMode: "custom" });
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

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message?.type === "fetchModels") {
    fetchModels().then(sendResponse);
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
    const imageUrl = await cropVisibleTab(tab.windowId, message.rect, message.devicePixelRatio || 1);
    await startJob(tab, settings, prompt, {
      imageUrl,
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
  const canvas = new OffscreenCanvas(sw, sh);
  const ctx = canvas.getContext("2d");
  ctx.drawImage(image, sx, sy, sw, sh, 0, 0, sw, sh);
  const blob = await canvas.convertToBlob({ type: "image/png" });
  return await blobToDataUrl(blob);
}

async function startJob(tab, settings, prompt, input) {
  const jobId = crypto.randomUUID();
  const origin = getOrigin(input.pageUrl || tab?.url || "");
  const createdAt = Date.now();
  const job = {
    id: jobId,
    status: "running",
    title: prompt.title,
    model: settings.model,
    input,
    origin,
    theme: settings.theme,
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
      headers: { Authorization: `Bearer ${settings.apiKey}` }
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

async function runJob(jobId, settings, prompt, input, tabId, origin) {
  try {
    const history = settings.resumeConversations ? await getConversationMessages(origin) : [];
    const userContent = await buildMessageContent(prompt, input);
    const messages = [
      { role: "system", content: SYSTEM_PROMPT },
      ...history,
      { role: "user", content: userContent }
    ];

    const response = await fetch(`${trimSlash(settings.endpoint)}/v1/chat/completions`, {
      method: "POST",
      headers: {
        Authorization: `Bearer ${settings.apiKey}`,
        "Content-Type": "application/json"
      },
      body: JSON.stringify({
        model: settings.model,
        messages,
        stream: false
      })
    });

    const body = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(body.error?.message || `HTTP ${response.status}`);

    const answer = body.choices?.[0]?.message?.content || "";
    await appendConversation(origin, input.pageUrl, userContent, answer);
    await updateJob(jobId, {
      status: "done",
      answer,
      usage: body.usage || null
    }, tabId);
  } catch (error) {
    await updateJob(jobId, {
      status: "error",
      error: error.message
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

function getOrigin(url) {
  try {
    const parsed = new URL(url);
    return parsed.origin;
  } catch {
    return "";
  }
}

function compact(value) {
  return Object.fromEntries(Object.entries(value).filter(([, item]) => item !== undefined));
}

function trimSlash(value) {
  return value.replace(/\/+$/, "");
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
