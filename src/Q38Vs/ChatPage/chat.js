/* q38-vs 聊天页。与宿主（ChatBridge）只走 postMessage：
   页 → 宿主：ready / send / abort / permitDecision / getContext
   宿主 → 页：status / state / hello / event / history / permit / context / error */

const $ = (id) => document.getElementById(id);
const list = $("list");
const scroller = $("scroller");
const input = $("input");

let busy = false;
let pendingContext = "";
let permitId = null;

// 流式中的气泡（delta 还原）：{ root, reasoningBody, contentEl, reasoning, content }
let stream = null;
let streamFlushTimer = 0;

window.chrome.webview.addEventListener("message", (ev) => handleHost(ev.data));

function post(msg) { window.chrome.webview.postMessage(msg); }

/* ---------- 宿主消息 ---------- */

function handleHost(msg) {
  if (!msg || typeof msg !== "object") return;
  switch (msg.type) {
    case "hello": {
      const st = msg.state || {};
      applyState(st);
      clearList();
      (msg.events || []).forEach(renderEvent);
      if (msg.permit) showPermit(msg.permit);
      break;
    }
    case "history": {
      clearList();
      (msg.events || []).forEach(renderEvent);
      break;
    }
    case "event":
      renderEvent(msg.event);
      break;
    case "state":
      applyState(msg.state || {});
      break;
    case "permit":
      showPermit(msg.permit);
      break;
    case "status": {
      const dot = $("dot");
      if (msg.kind === "up") { dot.className = "dot up"; hideStatus(); }
      else if (msg.kind === "down") { dot.className = "dot down"; showStatus(msg.detail || "连接断开，重连中…"); }
      else if (msg.kind === "connecting") { dot.className = "dot connecting"; showStatus(msg.detail || "连接中…"); }
      else showStatus(msg.detail || "");
      break;
    }
    case "context": {
      pendingContext = msg.text || "";
      if (pendingContext) {
        $("ctx-text").textContent = pendingContext.split("\n")[0];
        $("ctxchip").classList.remove("hidden");
      } else {
        $("ctxchip").classList.add("hidden");
      }
      break;
    }
    case "error":
      renderNotice("error", msg.message || "发生错误");
      break;
  }
}

/* ---------- 状态 ---------- */

function applyState(st) {
  busy = !!(st.busy || st.turn_in_flight);
  $("stop").classList.toggle("hidden", !busy);
  $("send").disabled = busy && false; // 忙时仍可输入排队
  const bits = [];
  if (st.workspace) bits.push(st.workspace.replace(/^.*[\\/]/, "").length ? st.workspace : st.workspace);
  if (st.model) bits.push(st.model);
  $("meta").textContent = bits.join(" · ");
  $("meta").title = [st.workspace, st.model, st.session].filter(Boolean).join("\n");
}

function showStatus(text) {
  const el = $("statusbar");
  el.textContent = text;
  el.classList.remove("hidden");
}
function hideStatus() { $("statusbar").classList.add("hidden"); }

function clearList() {
  stream = null;
  list.innerHTML = "";
  const empty = document.createElement("div");
  empty.id = "empty";
  empty.innerHTML = '<div class="logo">◇</div>Qwenthin 已就绪<br><span style="font-size:11px">本地 q38 · 想到什么问什么</span>';
  list.appendChild(empty);
}

function dropEmpty() {
  const empty = $("empty");
  if (empty) empty.remove();
}

function scrollDown() {
  scroller.scrollTop = scroller.scrollHeight;
}

/* ---------- 事件渲染 ---------- */

function renderEvent(e) {
  if (!e || typeof e !== "object") return;
  switch (e.type) {
    case "delta": onDelta(e); break;
    case "user":
      dropEmpty();
      endStream();
      addDiv("msg user", e.text || "");
      scrollDown();
      break;
    case "assistant":
      dropEmpty();
      renderAssistant(e);
      scrollDown();
      break;
    case "tool":
      dropEmpty();
      endStream();
      addTool(e);
      scrollDown();
      break;
    case "stop":
      renderNotice("stop", "停止 · " + (e.reason || ""));
      break;
    case "policy":
      renderNotice("", "policy · " + (e.reason || ""));
      break;
    case "session/compact":
      renderNotice("", "上下文已压缩");
      break;
    case "session/fork":
      renderNotice("", "会话已分叉");
      break;
    case "session/undo":
      renderNotice("", "已撤销上一轮");
      clearList();
      break;
    /* session/start 等不画 */
  }
}

function addDiv(cls, text) {
  const el = document.createElement("div");
  el.className = cls;
  el.textContent = text;
  list.appendChild(el);
  return el;
}

function renderNotice(cls, text) {
  endStream();
  const el = addDiv("notice " + cls, text);
  scrollDown();
  return el;
}

function renderAssistant(e) {
  endStream();
  const el = document.createElement("div");
  el.className = "msg assistant";
  if (e.reasoning) {
    const th = document.createElement("details");
    th.className = "reasoning";
    th.innerHTML = "<summary>思考</summary><div class='reasoning-body'></div>";
    th.querySelector(".reasoning-body").textContent = e.reasoning;
    el.appendChild(th);
  }
  const body = document.createElement("div");
  body.className = "content";
  body.innerHTML = mdRender(e.content || "");
  el.appendChild(body);
  const bits = [];
  if (e.completion_tokens) bits.push(e.completion_tokens + " tok");
  if (e.decode_tok_s) bits.push(Number(e.decode_tok_s).toFixed(1) + " tok/s");
  if (e.cached_tokens) bits.push("cache " + e.cached_tokens);
  if (bits.length) {
    const meta = document.createElement("div");
    meta.className = "meta";
    meta.textContent = bits.join(" · ");
    el.appendChild(meta);
  }
  list.appendChild(el);
}

/* 工具行 */
function addTool(e) {
  const el = document.createElement("div");
  el.className = "tool";
  const name = document.createElement("div");
  name.className = "tool-name";
  name.textContent = "▸ " + (e.name || "tool");
  const out = document.createElement("pre");
  out.className = "tool-out";
  const text = e.output || "";
  out.textContent = text.length > 500 ? text.slice(0, 500) + "\n…（点击展开，共 " + text.length + " 字符）" : text;
  if (text.length > 500) out.addEventListener("click", () => {
    el.classList.toggle("expanded");
    out.textContent = el.classList.contains("expanded") ? text : text.slice(0, 500) + "\n…";
  });
  el.appendChild(name);
  el.appendChild(out);
  list.appendChild(el);
}

/* ---------- 流式 delta ---------- */

function onDelta(d) {
  const channel = d.channel || "content";
  if (d.reset) endStream(true);
  if (!stream) {
    dropEmpty();
    const root = document.createElement("div");
    root.className = "msg assistant streaming";
    const think = document.createElement("details");
    think.className = "reasoning";
    think.innerHTML = "<summary>思考</summary><div class='reasoning-body'></div>";
    const body = document.createElement("div");
    body.className = "content";
    root.appendChild(think);
    root.appendChild(body);
    list.appendChild(root);
    stream = { root, think, body, reasoning: "", content: "" };
  }
  if (channel === "reasoning") {
    stream.reasoning += d.text || "";
    stream.think.querySelector(".reasoning-body").textContent = stream.reasoning;
    stream.think.open = true;
  } else {
    stream.content += d.text || "";
  }
  scheduleFlush();
  scrollDown();
}

function scheduleFlush() {
  if (streamFlushTimer) return;
  streamFlushTimer = setTimeout(() => {
    streamFlushTimer = 0;
    if (!stream) return;
    stream.body.innerHTML = mdRender(stream.content);
  }, 50);
}

function endStream(keepBubble) {
  if (streamFlushTimer) { clearTimeout(streamFlushTimer); streamFlushTimer = 0; }
  if (!stream) return;
  stream.root.classList.remove("streaming");
  stream.body.innerHTML = mdRender(stream.content);
  if (stream.reasoning) stream.think.open = false;
  if (!keepBubble && !stream.content && !stream.reasoning) stream.root.remove();
  stream = null;
}

/* ---------- markdown-lite：围栏代码块、行内代码、粗体 ---------- */

function esc(s) {
  return s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
}

function mdRender(src) {
  if (!src) return "";
  const parts = String(src).split(/```/);
  let html = "";
  for (let i = 0; i < parts.length; i++) {
    if (i % 2 === 1) {
      // 围栏内：首行若是语言名则吃掉
      let block = parts[i];
      const nl = block.indexOf("\n");
      if (nl >= 0 && nl <= 12) block = block.slice(nl + 1);
      html += "<pre><code>" + esc(block) + "</code></pre>";
    } else {
      html += mdInline(parts[i]);
    }
  }
  return html;
}

function mdInline(text) {
  const out = [];
  for (const line of String(text).split("\n")) {
    let l = esc(line);
    l = l.replace(/`([^`]+)`/g, "<code class='inline'>$1</code>");
    l = l.replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>");
    out.push(l);
  }
  return out.join("\n");
}

/* ---------- 审批 ---------- */

function showPermit(permit) {
  if (!permit) { hidePermit(); return; }
  permitId = permit.id;
  $("permit-tool").textContent = permit.tool || "tool";
  $("permit-preview").textContent = permit.preview || "";
  $("permit").classList.remove("hidden");
  scrollDown();
}

function hidePermit() {
  permitId = null;
  $("permit").classList.add("hidden");
}

$("p-allow").addEventListener("click", () => decide("allow"));
$("p-always").addEventListener("click", () => decide("always"));
$("p-deny").addEventListener("click", () => decide("deny"));

function decide(decision) {
  if (permitId == null) return;
  post({ type: "permitDecision", id: permitId, decision });
  hidePermit();
}

/* ---------- 输入 ---------- */

$("ctx-btn").addEventListener("click", () => post({ type: "getContext" }));
$("ctx-clear").addEventListener("click", () => {
  pendingContext = "";
  $("ctxchip").classList.add("hidden");
});

$("send").addEventListener("click", send);
$("stop").addEventListener("click", () => post({ type: "abort" }));

input.addEventListener("keydown", (ev) => {
  if (ev.key === "Enter" && !ev.shiftKey) {
    ev.preventDefault();
    send();
  }
});

input.addEventListener("input", () => {
  input.style.height = "auto";
  input.style.height = Math.min(input.scrollHeight, 180) + "px";
});

function send() {
  const text = input.value.trim();
  if (!text) return;
  let full = text;
  if (pendingContext) {
    full = pendingContext + "\n\n" + text;
    pendingContext = "";
    $("ctxchip").classList.add("hidden");
  }
  post({ type: "send", text: full });
  input.value = "";
  input.style.height = "auto";
}

/* 开场：通知宿主页面就绪 */
post({ type: "ready" });
