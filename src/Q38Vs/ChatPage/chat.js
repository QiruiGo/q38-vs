/* q38-vs 聊天页。与宿主（ChatBridge）只走 postMessage：
   页 → 宿主：ready / send / abort / permitDecision / getContext / openFile
   宿主 → 页：status / state / hello / event / history / permit / context / toast / error */

const $ = (id) => document.getElementById(id);
const list = $("list");
const scroller = $("scroller");
const input = $("input");

let busy = false;
let contexts = [];   // ＠ 附加的上下文芯片：{ path, hasSel, lineStart, lineEnd, text }
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
      const ctx = msg.context;
      if (ctx && ctx.path) {
        // 同一文档重复 ＠ → 覆盖旧芯片（刷新选区）
        contexts = contexts.filter((c) => c.path !== ctx.path);
        contexts.push({
          path: ctx.path,
          hasSel: !!ctx.hasSelection,
          lineStart: ctx.lineStart || 0,
          lineEnd: ctx.lineEnd || 0,
          text: ctx.text || "",
        });
        renderChips();
      } else {
        showToast("没有可附加的活动文档");
      }
      break;
    }
    case "toast":
      showToast(msg.message || "");
      break;
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
  const truncated = text.length > 500;
  renderTextWithPaths(out, truncated ? text.slice(0, 500) + "\n…（点击展开，共 " + text.length + " 字符）" : text);
  if (truncated) out.addEventListener("click", () => {
    el.classList.toggle("expanded");
    out.innerHTML = "";
    renderTextWithPaths(out, el.classList.contains("expanded") ? text : text.slice(0, 500) + "\n…");
  });
  el.appendChild(name);
  el.appendChild(out);
  list.appendChild(el);
}

/* ---------- 工具输出里的文件路径 → 可点链接 ----------
   认两种形态：绝对盘符（D:\x\y.cs）与含目录分隔符的相对路径（src\Q38Vs\chat.js、
   ./a/b.rs、../c/d.ts），均可带 :行号 后缀；行首 "NN| " 前缀（grep 风格）紧邻路径时
   NN 作为默认行号。点击 postMessage {type:"openFile", path, line} 给宿主。 */

// 末段须含点（有扩展名）才认成文件，避免把普通词组/URL 误判；路径字符不含冒号/空白，
// 所以 "D:\a.cs:12" 里的行号天然落到第二个捕获组。
const PATH_RE = /(?<![\w.:/\\-])([A-Za-z]:[\\/](?:[^\s:\\/]+[\\/])*[^\s:\\/]+|(?:(?:\.{1,2}[\\/])?[\w.@\\-]+(?:[\\/][\w.@\\-]+)+))(:\d+)?/g;
const TRAIL_PUNCT = /[.,;!?)\]}'">…。，；！？）】》」』]+$/;

function renderTextWithPaths(outEl, text) {
  const lines = String(text).split("\n");
  for (let i = 0; i < lines.length; i++) {
    if (i) outEl.appendChild(document.createTextNode("\n"));
    linkifyLine(outEl, lines[i]);
  }
}

function linkifyLine(outEl, line) {
  let rest = line, prefixLine = 0;
  // 行首 "NN| "：仅当其后紧跟路径才吞掉前缀，NN 作为行号兜底
  const pm = rest.match(/^\s*(\d{1,5})\s*\|\s?/);
  if (pm) {
    PATH_RE.lastIndex = 0;
    const probe = PATH_RE.exec(rest.slice(pm[0].length));
    if (probe && probe.index === 0 && lastSegHasDot(probe[1])) {
      outEl.appendChild(document.createTextNode(pm[0]));
      prefixLine = parseInt(pm[1], 10);
      rest = rest.slice(pm[0].length);
    }
  }
  let last = 0, hit;
  PATH_RE.lastIndex = 0;
  while ((hit = PATH_RE.exec(rest))) {
    let path = hit[1];
    let end = hit.index + hit[0].length;
    // 剥掉粘在路径尾部的标点（句子收尾等）
    const punct = path.match(TRAIL_PUNCT);
    if (punct) { path = path.slice(0, path.length - punct[0].length); end -= punct[0].length; }
    if (hit.index > last) outEl.appendChild(document.createTextNode(rest.slice(last, hit.index)));
    last = end;
    if (!lastSegHasDot(path)) {
      outEl.appendChild(document.createTextNode(rest.slice(hit.index, end)));
      continue;
    }
    const lineNo = hit[2] ? parseInt(hit[2].slice(1), 10) : prefixLine;
    const a = document.createElement("a");
    a.className = "path-link";
    a.textContent = rest.slice(hit.index, end);
    a.title = "在 VS 中打开" + (lineNo > 0 ? "（第 " + lineNo + " 行）" : "");
    a.addEventListener("click", (ev) => {
      ev.stopPropagation(); // 别触发外层 pre 的展开/收起
      post({ type: "openFile", path: path, line: lineNo });
    });
    outEl.appendChild(a);
  }
  if (last < rest.length) outEl.appendChild(document.createTextNode(rest.slice(last)));
}

function lastSegHasDot(path) {
  const segs = String(path).split(/[\\/]/);
  const tail = segs[segs.length - 1] || "";
  return tail.indexOf(".") > 0;
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

/* ---------- 上下文芯片 ---------- */

function fileName(p) {
  const segs = String(p).split(/[\\/]/);
  return segs[segs.length - 1] || p;
}

function renderChips() {
  const box = $("ctxchips");
  box.innerHTML = "";
  box.classList.toggle("hidden", contexts.length === 0);
  for (const c of contexts) {
    const chip = document.createElement("div");
    chip.className = "ctxchip";
    const label = document.createElement("span");
    label.className = "ctx-label";
    const text = c.hasSel ? fileName(c.path) + " · L" + c.lineStart + (c.lineEnd > c.lineStart ? "-L" + c.lineEnd : "")
                          : fileName(c.path);
    label.textContent = text;
    label.title = c.path + (c.hasSel ? " · 已选中 " + c.text.length + " 字符" : "");
    const x = document.createElement("button");
    x.className = "chip-x";
    x.title = "移除上下文";
    x.textContent = "×";
    x.addEventListener("click", () => {
      contexts = contexts.filter((it) => it !== c);
      renderChips();
    });
    chip.appendChild(label);
    chip.appendChild(x);
    box.appendChild(chip);
  }
}

/* 发送前把芯片格式化成上下文块。上游 q38 的 turn.start 只消费 prompt/text 与媒体
   part（ContentPart::media_part 对 text 恒为 None），本地文件引用不会被读取，
   所以真正生效的是拼进 text 的格式化块；content_parts 按 wire shape 一并带给
   宿主透传（turn.start / turn.queue 都带），上游侧忽略，仅为对齐协议。 */
function contextBlock(c) {
  if (!c.hasSel) return "@" + c.path;
  const name = fileName(c.path);
  const dot = name.lastIndexOf(".");
  const lang = dot > 0 ? name.slice(dot + 1).toLowerCase() : "";
  const range = c.lineEnd > c.lineStart ? "L" + c.lineStart + "-L" + c.lineEnd : "L" + c.lineStart;
  return "@" + c.path + "（" + range + "，当前选中部分）：\n```" + lang + "\n" + c.text + "\n```";
}

/* ---------- toast ---------- */

let toastTimer = 0;
function showToast(message) {
  if (!message) return;
  const t = $("toast");
  t.textContent = message;
  t.classList.remove("hidden");
  if (toastTimer) clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { $("toast").classList.add("hidden"); toastTimer = 0; }, 3500);
}

/* ---------- 输入 ---------- */

$("ctx-btn").addEventListener("click", () => post({ type: "getContext" }));

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
  const msg = { type: "send", text: text };
  if (contexts.length) {
    const blocks = contexts.map(contextBlock);
    msg.text = blocks.join("\n\n") + "\n\n" + text;
    msg.content_parts = blocks.map((b) => ({ type: "text", text: b }));
    contexts = [];
    renderChips();
  }
  post(msg);
  input.value = "";
  input.style.height = "auto";
}

/* 开场：通知宿主页面就绪 */
post({ type: "ready" });
