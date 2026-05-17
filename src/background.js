const DEFAULTS = {
  endpoint: "http://127.0.0.1:20128",
  apiKey: "sk_9-router",
  model: "cx/gpt-5.5",
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

chrome.runtime.onInstalled.addListener(async () => {
  const existing = await chrome.storage.local.get(Object.keys(DEFAULTS));
  await chrome.storage.local.set({ ...DEFAULTS, ...compact(existing) });
  await rebuildMenus();
});

chrome.runtime.onStartup.addListener(rebuildMenus);

chrome.storage.onChanged.addListener((changes, area) => {
  if (area === "local" && changes.prompts) rebuildMenus();
});

chrome.contextMenus.onClicked.addListener(async (info) => {
  if (!info.menuItemId.toString().startsWith("faf:")) return;

  const promptId = info.menuItemId.toString().slice(4);
  const jobId = crypto.randomUUID();
  const settings = await getSettings();
  const prompt = settings.prompts.find((item) => item.id === promptId) || settings.prompts[0];
  const input = buildInput(info);

  await chrome.storage.session.set({
    [`job:${jobId}`]: {
      id: jobId,
      status: "running",
      title: prompt.title,
      model: settings.model,
      input,
      createdAt: Date.now()
    }
  });

  chrome.windows.create({
    url: chrome.runtime.getURL(`src/result.html?job=${encodeURIComponent(jobId)}`),
    type: "popup",
    width: 720,
    height: 820
  });

  runJob(jobId, settings, prompt, input);
});

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (message?.type === "fetchModels") {
    fetchModels().then(sendResponse);
    return true;
  }
  if (message?.type === "rebuildMenus") {
    rebuildMenus().then(() => sendResponse({ ok: true }));
    return true;
  }
});

async function rebuildMenus() {
  await chrome.contextMenus.removeAll();
  const { prompts } = await getSettings();

  for (const prompt of prompts) {
    chrome.contextMenus.create({
      id: `faf:${prompt.id}`,
      title: prompt.title,
      contexts: ["selection", "image"]
    });
  }
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

async function runJob(jobId, settings, prompt, input) {
  try {
    const messages = [
      {
        role: "system",
        content: "You are a precise assistant. Return a direct, useful answer with clear formatting."
      },
      {
        role: "user",
        content: await buildMessageContent(prompt, input)
      }
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

    await updateJob(jobId, {
      status: "done",
      answer: body.choices?.[0]?.message?.content || "",
      usage: body.usage || null
    });
  } catch (error) {
    await updateJob(jobId, {
      status: "error",
      error: error.message
    });
  }
}

async function buildMessageContent(prompt, input) {
  const text = `${prompt.prompt}\n\nSelected content:\n${input.text || input.imageUrl || ""}`;
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
  const blob = await response.blob();
  const bytes = new Uint8Array(await blob.arrayBuffer());
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return `data:${blob.type || "application/octet-stream"};base64,${btoa(binary)}`;
}

async function updateJob(jobId, patch) {
  const key = `job:${jobId}`;
  const existing = (await chrome.storage.session.get(key))[key] || {};
  await chrome.storage.session.set({ [key]: { ...existing, ...patch, updatedAt: Date.now() } });
}

function buildInput(info) {
  return {
    text: info.selectionText || "",
    imageUrl: info.srcUrl || "",
    pageUrl: info.pageUrl || ""
  };
}

async function getSettings() {
  const values = await chrome.storage.local.get(Object.keys(DEFAULTS));
  return { ...DEFAULTS, ...compact(values) };
}

function compact(value) {
  return Object.fromEntries(Object.entries(value).filter(([, item]) => item !== undefined));
}

function trimSlash(value) {
  return value.replace(/\/+$/, "");
}
