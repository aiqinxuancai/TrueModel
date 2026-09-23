"use strict";
const icons = () => window.lucide?.createIcons();
const icon = (name) => `<i data-lucide="${name}"></i>`;
const navIcons = {
  overview: "layout-dashboard",
  configuration: "network",
  history: "history",
  banks: "fingerprint",
  settings: "sliders-horizontal",
  notifications: "bell",
};
document.querySelectorAll("[data-tab]").forEach((button) => {
  button.removeAttribute("data-icon");
  button.insertAdjacentHTML("afterbegin", icon(navIcons[button.dataset.tab]));
});
$("sidebar").setAttribute("aria-label", "主导航");
$("sidebar").insertAdjacentHTML(
  "beforeend",
  `<div class="sidebar-footer">${icon("shield-check")}管理员工作空间</div>`,
);
document.body.insertAdjacentHTML(
  "afterbegin",
  '<a class="skip-link" href="#application">跳转到主内容</a><button id="navBackdrop" aria-label="关闭导航" hidden></button>',
);
$("application").tabIndex = -1;
document
  .querySelector("header")
  .insertAdjacentHTML(
    "afterbegin",
    `<button id="menuToggle" class="icon-button" aria-label="展开导航" title="展开导航" aria-controls="sidebar" aria-expanded="false" hidden>${icon("menu")}</button>`,
  );
$("logout").insertAdjacentHTML(
  "beforebegin",
  `<span id="syncStatus" hidden>等待同步</span><button id="refreshButton" class="icon-button" aria-label="刷新数据" title="刷新数据" hidden>${icon("refresh-cw")}</button>`,
);
document
  .querySelector("main")
  .insertAdjacentHTML(
    "afterend",
    '<footer class="workspace-footer"><span>TrueModel</span><span>模型检测与归因</span></footer>',
  );
$("login").insertAdjacentHTML(
  "afterbegin",
  `<div class="login-brand">${icon("scan-line")}TrueModel</div>`,
);
const password = $("loginForm").elements.password;
const passwordWrap = document.createElement("span");
passwordWrap.className = "password-field";
password.before(passwordWrap);
passwordWrap.append(password);
passwordWrap.insertAdjacentHTML(
  "beforeend",
  `<button id="togglePassword" type="button" class="icon-button" aria-label="显示密码" title="显示密码">${icon("eye")}</button>`,
);
$("togglePassword").onclick = () => {
  const visible = password.type === "password";
  password.type = visible ? "text" : "password";
  $("togglePassword").setAttribute(
    "aria-label",
    visible ? "隐藏密码" : "显示密码",
  );
  $("togglePassword").title = visible ? "隐藏密码" : "显示密码";
  $("togglePassword").innerHTML = icon(visible ? "eye-off" : "eye");
  icons();
};
const filters = document.createElement("div");
filters.className = "filters";
$("search").before(filters);
const searchWrap = document.createElement("div");
searchWrap.className = "search-field";
searchWrap.innerHTML = icon("search");
filters.append(searchWrap);
searchWrap.append($("search"));
filters.insertAdjacentHTML(
  "beforeend",
  '<select id="statusFilter" aria-label="筛选模型状态"><option value="all">全部状态</option><option value="Running">运行中</option><option value="Queued">排队中</option><option value="Success">成功</option><option value="failure">异常</option><option value="Pending">待检测</option></select>',
);
$("search")
  .closest(".heading")
  .querySelector("h2")
  .insertAdjacentHTML(
    "beforeend",
    '<span id="modelCount" class="count">0</span>',
  );
$("statusFilter").onchange = () => render();
for (const [selector, name] of [
  ['[data-detect="all"]', "play"],
  ["#addSite", "plus"],
  ["#importBank", "upload"],
  ["#testNotification", "send"],
]) {
  const b = document.querySelector(selector);
  b.insertAdjacentHTML("afterbegin", icon(name));
  if (selector !== "#testNotification") b.classList.add("primary");
}
for (const id of ["closeEditor", "closeDetail"]) {
  $(id).innerHTML = icon("x");
  $(id).title = "关闭";
}
$("editor").setAttribute("aria-labelledby", "editorTitle");
$("detail").setAttribute("aria-label", "检测详情");
document.querySelectorAll(".table-wrap").forEach((el) => {
  el.tabIndex = 0;
  el.setAttribute(
    "aria-label",
    el.closest("section").id === "overview" ? "模型状态表" : "检测历史表",
  );
});
function closeNav() {
  $("sidebar").classList.remove("mobile-open");
  $("navBackdrop").hidden = true;
  $("menuToggle").setAttribute("aria-expanded", "false");
}
$("menuToggle").onclick = () => {
  const open = $("sidebar").classList.toggle("mobile-open");
  $("navBackdrop").hidden = !open;
  $("menuToggle").setAttribute("aria-expanded", String(open));
};
$("navBackdrop").onclick = closeNav;
document.addEventListener("keydown", (e) => {
  if (e.key === "Escape") closeNav();
});
function syncShell() {
  const authenticated = !$("application").hidden;
  document.body.classList.toggle("authenticated", authenticated);
  for (const id of ["menuToggle", "refreshButton", "syncStatus"])
    $(id).hidden = !authenticated;
  if (!authenticated) closeNav();
}
new MutationObserver(syncShell).observe($("application"), {
  attributes: true,
  attributeFilter: ["hidden"],
});
syncShell();
function navigate(tab) {
  const button = document.querySelector(`[data-tab="${tab}"]`);
  if (!button) return;
  document.querySelectorAll(".view").forEach((v) => (v.hidden = v.id !== tab));
  document.querySelectorAll("[data-tab]").forEach((b) => {
    b.classList.toggle("selected", b === button);
    if (b === button) b.setAttribute("aria-current", "page");
    else b.removeAttribute("aria-current");
  });
  closeNav();
}
document.querySelectorAll("[data-tab]").forEach((button) =>
  button.addEventListener("click", () => {
    navigate(button.dataset.tab);
    history.replaceState(null, "", "#" + button.dataset.tab);
  }),
);
document.addEventListener("click", (e) => {
  const go = e.target.closest("[data-go]");
  if (go) document.querySelector(`[data-tab="${go.dataset.go}"]`).click();
  if (e.target.closest("[data-clear-filter]")) {
    $("search").value = "";
    $("statusFilter").value = "all";
    render();
  }
});
$("refreshButton").onclick = async () => {
  const b = $("refreshButton");
  b.disabled = true;
  try {
    await refresh();
    if (!$("notifications").hidden) await refreshNotifications();
    message("数据已更新");
  } catch (e) {
    message(e.message);
  } finally {
    b.disabled = false;
  }
};
const knownKeys = new Set();
let expandedKeys = new Set();

function configuredModelCard(model) {
  return `<div class="configured-model">
    <span>${icon("box")}<code>${esc(model.name)}</code></span>
    <div class="actions">
      <button data-detect-model="${model.id}" class="icon-button" aria-label="检测模型 ${esc(model.name)}" title="检测模型">${icon("play")}</button>
      <button data-delete="models/${model.id}" class="icon-button" aria-label="删除模型 ${esc(model.name)}" title="删除模型">${icon("trash-2")}</button>
    </div>
  </div>`;
}

function configuredKeyGroup(key) {
  const open = expandedKeys.has(String(key.id)) || !knownKeys.has(key.id);
  return `<details class="key-group" data-key-id="${key.id}" ${open ? "open" : ""}>
    <summary>
      <span class="key-name"><span class="config-level key-level">${icon("key-round")}Key</span><b>${esc(key.name)}</b><code>${esc(key.mask)}</code><span class="key-count">${key.models.length} 个模型</span></span>
      <span class="actions key-actions">
        <button data-model="${key.id}">${icon("plus")}添加模型</button>
        <button data-detect-key="${key.id}" ${key.models.length ? "" : "disabled"}>${icon("play")}检测 Key</button>
        <button class="icon-button" data-delete="keys/${key.id}" aria-label="删除 Key ${esc(key.name)}" title="删除 Key">${icon("trash-2")}</button>
      </span>
    </summary>
    <div class="configured-models">${key.models.map(configuredModelCard).join("") || '<div class="inline-empty">暂无模型，点击“添加模型”开始配置</div>'}</div>
  </details>`;
}

function configuredSiteCard(site) {
  const modelCount = site.keys.reduce((count, key) => count + key.models.length, 0);
  return `<section class="site-group" aria-labelledby="site-title-${site.id}">
    <div class="site-heading">
      <div class="site-identity">
        <span class="site-icon">${icon("network")}</span>
        <div class="site-info"><div class="site-title"><span class="config-level site-level">站点</span><h2 id="site-title-${site.id}">${esc(site.name)}</h2><span class="site-count">${site.keys.length} 个 Key · ${modelCount} 个模型</span></div><small>${esc(site.baseUrl)}</small></div>
      </div>
      <div class="actions">
        <button data-key="${site.id}">${icon("plus")}添加 Key</button>
        <button data-detect-site="${site.id}" ${modelCount ? "" : "disabled"}>${icon("play")}检测站点</button>
        <button class="icon-button" data-delete="sites/${site.id}" aria-label="删除站点 ${esc(site.name)}" title="删除站点">${icon("trash-2")}</button>
      </div>
    </div>
    <div class="site-keys">${site.keys.map(configuredKeyGroup).join("") || `<div class="inline-empty">暂无 API Key<button data-key="${site.id}">${icon("plus")}添加 Key</button></div>`}</div>
  </section>`;
}
document.addEventListener(
  "toggle",
  (e) => {
    if (e.target.matches(".key-group")) {
      const id = e.target.dataset.keyId;
      if (e.target.open) expandedKeys.add(id);
      else expandedKeys.delete(id);
    }
  },
  true,
);
const baseRender = render;
render = function () {
  const expandedReasons = new Set(
    [...document.querySelectorAll("[data-reason-id][open]")].map(
      (el) => el.dataset.reasonId,
    ),
  );
  baseRender();
  document.querySelectorAll("[data-reason-id]").forEach((el) => {
    el.open = expandedReasons.has(el.dataset.reasonId);
  });
  const statIcons = ["network", "key-round", "box", "circle-check", "triangle-alert"];
  document
    .querySelectorAll(".stat")
    .forEach((el, i) =>
      el.insertAdjacentHTML(
        "beforeend",
        `<div class="stat-icon">${icon(statIcons[i])}</div>`,
      ),
    );
  const filter = $("statusFilter").value;
  let count = 0;
  document.querySelectorAll("#modelRows tr").forEach((row) => {
    const status = row.querySelector(".badge");
    if (!status) return;
    const visible =
      filter === "all" ||
      (filter === "failure"
        ? ["Failed", "Timeout", "Interrupted", "Cancelled"].some((s) =>
            status.classList.contains(s),
          )
        : status.classList.contains(filter));
    row.hidden = !visible;
    if (visible) count++;
  });
  $("modelCount").textContent = count;
  if (!count) {
    const hasModels = sites.some((s) => s.keys.some((k) => k.models.length));
    $("modelRows").innerHTML =
      `<tr><td colspan="10" class="empty">${icon(hasModels ? "search-x" : "box")}<strong>${hasModels ? "没有匹配的模型" : "暂无模型"}</strong><button ${hasModels ? "data-clear-filter" : 'data-go="configuration"'}>${hasModels ? "清除筛选" : "配置站点"}</button></td></tr>`;
  }
  $("siteRows").innerHTML = sites.map(configuredSiteCard).join("") ||
    `<div class="empty">${icon("network")}<strong>暂无站点</strong><button data-create-site>添加站点</button></div>`;
  sites.forEach((s) => s.keys.forEach((k) => knownKeys.add(k.id)));
  document
    .querySelector("[data-create-site]")
    ?.addEventListener("click", () => $("addSite").click());
  if (!runs.length)
    $("runRows").innerHTML =
      `<div class="empty">${icon("activity")}暂无检测任务</div>`;
  document.querySelector('[data-detect="all"]').disabled = !sites.some((s) =>
    s.keys.some((k) => k.models.length),
  );
  icons();
};
const baseRefresh = refresh;
let refreshPromise;
refresh = function () {
  if (refreshPromise) return refreshPromise;
  refreshPromise = baseRefresh()
    .then(() => {
      $("syncStatus").textContent =
        "更新于 " + new Date().toLocaleTimeString("zh-CN", { hour12: false });
      $("syncStatus").classList.remove("stale");
    })
    .catch((e) => {
      $("syncStatus").textContent = "同步失败";
      $("syncStatus").classList.add("stale");
      throw e;
    })
    .finally(() => {
      refreshPromise = null;
    });
  return refreshPromise;
};
const baseEdit = edit;
edit = function (title, fields, action) {
  $("editor").querySelector(".dialog-message")?.remove();
  baseEdit(
    title,
    fields.map((field) =>
      field[0] === "value" ? [field[0], field[1], "text"] : field,
    ),
    action,
  );
  $("editorForm").autocomplete = "off";
  const apiKey = $("editorForm").elements.value;
  if (apiKey) {
    apiKey.autocomplete = "off";
    apiKey.spellcheck = false;
    apiKey.setAttribute("autocapitalize", "none");
    apiKey.setAttribute("data-lpignore", "true");
    apiKey.setAttribute("data-1p-ignore", "true");
  }
  const input = $("editorFields").querySelector("input");
  if (input?.type === "file") {
    input.accept = ".json,application/json";
  }
  input?.focus();
};
let messageTimer;
message = function (text) {
  clearTimeout(messageTimer);
  $("message").textContent = text;
  const dialog = document.querySelector("dialog[open]");
  let inlineMessage = dialog?.querySelector(".dialog-message");
  if (dialog && !inlineMessage) {
    inlineMessage = document.createElement("p");
    inlineMessage.className = "dialog-message";
    inlineMessage.setAttribute("role", "status");
    dialog.querySelector(".heading").after(inlineMessage);
  }
  if (inlineMessage) inlineMessage.textContent = text;
  messageTimer = setTimeout(() => {
    $("message").textContent = "";
    inlineMessage?.remove();
  }, 7000);
};
for (const id of [
  "loginForm",
  "editorForm",
  "settingsForm",
  "notificationForm",
]) {
  const form = $(id),
    submit = form.onsubmit;
  form.onsubmit = async (e) => {
    const button =
      e.submitter || form.querySelector("button:not([type=button])");
    if (button?.disabled) {
      e.preventDefault();
      return;
    }
    if (button) button.disabled = true;
    try {
      await submit(e);
    } finally {
      if (button) button.disabled = false;
    }
  };
}
navigate(location.hash.slice(1) || "overview");
icons();

// Keep the tooltip outside the table's scroll and stacking contexts.
const recentTooltip = document.createElement("div");
recentTooltip.id = "recentResultTooltip";
recentTooltip.className = "recent-tooltip";
recentTooltip.setAttribute("role", "tooltip");
recentTooltip.hidden = true;
document.body.append(recentTooltip);
let tooltipAnchor, tooltipCloseTimer;
function closeRecentTooltip() {
  clearTimeout(tooltipCloseTimer);
  tooltipAnchor?.removeAttribute("aria-describedby");
  tooltipAnchor = null;
  recentTooltip.hidden = true;
}
function showRecentTooltip(anchor) {
  closeRecentTooltip();
  tooltipAnchor = anchor;
  recentTooltip.textContent = anchor.dataset.tooltip;
  anchor.setAttribute("aria-describedby", recentTooltip.id);
  recentTooltip.style.left = "12px";
  recentTooltip.style.top = "12px";
  recentTooltip.hidden = false;
  const rect = anchor.getBoundingClientRect();
  const box = recentTooltip.getBoundingClientRect();
  const width = document.documentElement.clientWidth;
  const height = document.documentElement.clientHeight;
  const top = rect.bottom + 8 + box.height <= height - 12 ? rect.bottom + 8 : rect.top - box.height - 8;
  recentTooltip.style.left = `${Math.max(12, Math.min(rect.right - box.width, width - box.width - 12))}px`;
  recentTooltip.style.top = `${Math.max(12, Math.min(top, height - box.height - 12))}px`;
}
document.addEventListener("pointerover", event => {
  if (recentTooltip.contains(event.target)) { clearTimeout(tooltipCloseTimer); return; }
  const anchor = event.target.closest("[data-tooltip]");
  if (anchor) showRecentTooltip(anchor);
});
document.addEventListener("pointerout", event => {
  if (event.target.closest("[data-tooltip]") || recentTooltip.contains(event.target)) {
    clearTimeout(tooltipCloseTimer);
    tooltipCloseTimer = setTimeout(closeRecentTooltip, 150);
  }
});
document.addEventListener("focusin", event => {
  const anchor = event.target.closest("[data-tooltip]");
  if (anchor) showRecentTooltip(anchor);
});
document.addEventListener("focusout", event => {
  if (event.target === tooltipAnchor) closeRecentTooltip();
});
document.addEventListener("keydown", event => { if (event.key === "Escape") closeRecentTooltip(); });
document.addEventListener("scroll", event => {
  if (event.target !== recentTooltip) closeRecentTooltip();
}, true);
window.addEventListener("resize", closeRecentTooltip);
new MutationObserver(() => {
  if (tooltipAnchor && !tooltipAnchor.isConnected) closeRecentTooltip();
}).observe($("modelRows"), { childList: true, subtree: true });
