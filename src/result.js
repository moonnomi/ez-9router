const params = new URLSearchParams(location.search);
const jobId = params.get("job");
const key = `job:${jobId}`;

const title = document.querySelector("#title");
const mode = document.querySelector("#mode");
const state = document.querySelector("#state");
const answer = document.querySelector("#answer");
const source = document.querySelector("#source");
const copy = document.querySelector("#copy");

copy.addEventListener("click", async () => {
  await navigator.clipboard.writeText(answer.innerText);
  copy.textContent = "Copied";
  setTimeout(() => (copy.textContent = "Copy"), 1200);
});

chrome.storage.onChanged.addListener((changes, area) => {
  if (area === "session" && changes[key]) render(changes[key].newValue);
});

init();

async function init() {
  const job = (await chrome.storage.session.get(key))[key];
  render(job);
}

function render(job) {
  if (!job) {
    state.textContent = "Job not found.";
    return;
  }

  mode.textContent = `${job.model || "model"} · ${new Date(job.createdAt).toLocaleTimeString()}`;
  title.textContent = job.title || "Answer";
  source.textContent = job.input?.text || job.input?.imageUrl || "";

  if (job.status === "running") {
    state.textContent = "Sending to model...";
    answer.innerHTML = "";
    return;
  }

  if (job.status === "error") {
    state.textContent = job.error || "Request failed.";
    state.className = "state error";
    answer.innerHTML = "";
    return;
  }

  state.textContent = job.usage ? `${job.usage.total_tokens} tokens` : "Complete";
  state.className = "state done";
  answer.innerHTML = renderMarkdownLite(job.answer || "");
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

function escapeHtml(value) {
  return value.replace(/[&<>"']/g, (char) => ({
    "&": "&amp;",
    "<": "&lt;",
    ">": "&gt;",
    "\"": "&quot;",
    "'": "&#039;"
  })[char]);
}
