"use strict";
const icons = () => window.lucide?.createIcons();
const icon = (name) => `<i data-lucide="${name}"></i>`;
const pageTitles = {
  overview: "工作台 / 总览",
  history: "工作台 / 检测历史",
  configuration: "配置 / 站点配置",
  banks: "配置 / 指纹库",
  settings: "配置 / 检测设置",
  notifications: "配置 / 通知",
};

// Theme: the inline head script applies the saved value before first paint.
function applyTheme(theme) {
  document.documentElement.dataset.theme = theme;
  const dark = theme === "dark";
  const label = dark ? "切换浅色模式" : "切换深色模式";
  $("themeToggle").setAttribute("aria-label", label);
  $("themeToggle").title = label;
  $("themeToggle").innerHTML = icon(dark ? "sun" : "moon");
  icons();
}
$("themeToggle").onclick = () => {
  const theme = document.documentElement.dataset.theme === "dark" ? "light" : "dark";
  try { localStorage.setItem("truemodel-theme", theme); } catch {}
  applyTheme(theme);
};
applyTheme(document.documentElement.dataset.theme || "light");

const password = $("loginForm").elements.password;
$("togglePassword").onclick = () => {
  const visible = password.type === "password";
  password.type = visible ? "text" : "password";
  const label = visible ? "隐藏密码" : "显示密码";
  $("togglePassword").setAttribute("aria-label", label);
  $("togglePassword").title = label;
  $("togglePassword").innerHTML = icon(visible ? "eye-off" : "eye");
  icons();
};
$("statusFilter").onchange = () => render();

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
  $("pageTitle").textContent = pageTitles[tab] || "";
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
  const close = e.target.closest("[data-close-dialog]");
  if (close) close.closest("dialog")?.close();
});
$("refreshButton").onclick = async () => {
  const b = $("refreshButton");
  b.disabled = true;
  b.setAttribute("aria-busy", "true");
  try {
    await refresh();
    if (!$("notifications").hidden) await refreshNotifications();
    message("数据已更新");
  } catch (e) {
    message(e.message);
  } finally {
    b.disabled = false;
    b.removeAttribute("aria-busy");
  }
};

const knownKeys = new Set();
let expandedKeys = new Set();

function configuredModelCard(model) {
  return `<div class="configured-model">
    <span class="configured-model-name">${icon("box")}<code title="${esc(model.name)}">${esc(model.name)}</code></span>
    <div class="actions">
      <button data-detect-model="${model.id}" class="icon-button ghost sm" aria-label="检测模型 ${esc(model.name)}" title="检测模型">${icon("play")}</button>
      <button data-delete="models/${model.id}" class="icon-button ghost sm" aria-label="删除模型 ${esc(model.name)}" title="删除模型">${icon("trash-2")}</button>
    </div>
  </div>`;
}

function configuredKeyGroup(key) {
  const open = expandedKeys.has(String(key.id)) || !knownKeys.has(key.id);
  return `<details class="key-group" data-key-id="${key.id}" ${open ? "open" : ""}>
    <summary>
      <span class="key-chevron" aria-hidden="true">${icon("chevron-right")}</span>
      <span class="key-name">
        <span class="key-icon" aria-hidden="true">${icon("key-round")}</span>
        <b>${esc(key.name)}</b>
        <button class="icon-button ghost xs" data-rename-key="${key.id}" aria-label="重命名 Key ${esc(key.name)}" title="重命名 Key">${icon("pencil")}</button>
        <code>${esc(key.mask)}</code>
        <span class="pill">${key.models.length} 个模型</span>
      </span>
      <span class="actions key-actions">
        <button data-model="${key.id}">${icon("plus")}添加模型</button>
        <button data-manage-models data-manage-key="${key.id}" ${key.models.length ? "" : "disabled"}>${icon("list-checks")}批量管理</button>
        <button data-detect-key="${key.id}" ${key.models.length ? "" : "disabled"}>${icon("play")}检测 Key</button>
        <button class="icon-button" data-delete="keys/${key.id}" aria-label="删除 Key ${esc(key.name)}" title="删除 Key">${icon("trash-2")}</button>
      </span>
    </summary>
    <div class="configured-models">${key.models.map(configuredModelCard).join("") || '<div class="inline-empty">暂无模型，点击“添加模型”开始配置</div>'}</div>
  </details>`;
}

function configuredSiteCard(site) {
  const modelCount = site.keys.reduce((count, key) => count + key.models.length, 0);
  return `<section class="site-group card" aria-labelledby="site-title-${site.id}">
    <div class="site-heading">
      <div class="site-identity">
        <span class="site-icon" aria-hidden="true">${icon("globe")}</span>
        <div class="site-info">
          <div class="site-title"><h2 id="site-title-${site.id}">${esc(site.name)}</h2><span class="pill">${site.keys.length} 个 Key · ${modelCount} 个模型</span></div>
          <small>${esc(site.baseUrl)}</small>
        </div>
      </div>
      <div class="actions">
        <button data-key="${site.id}">${icon("plus")}添加 Key</button>
        <button data-manage-models data-manage-site="${site.id}" ${modelCount ? "" : "disabled"}>${icon("list-checks")}批量管理</button>
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
document.addEventListener("click", (e) => {
  const button = e.target.closest("button[data-rename-key]");
  if (!button) return;
  // The button sits inside <summary>; keep the Key group from toggling.
  e.preventDefault();
  const id = Number(button.dataset.renameKey);
  const key = sites.flatMap((s) => s.keys).find((k) => k.id === id);
  if (!key) return;
  edit("重命名 Key", [["name", "Key 名称", "text", key.name]], async (v) => {
    await api(`/keys/${id}`, "PUT", { name: v.name });
    return "Key 名称已更新";
  });
  const input = $("editorForm").elements.name;
  input.maxLength = 200;
  input.select();
});

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
    `<div class="card empty">${icon("network")}<strong>暂无站点</strong><span>添加站点后即可配置 API Key 与模型</span><button data-create-site class="primary">${icon("plus")}添加站点</button></div>`;
  sites.forEach((s) => s.keys.forEach((k) => knownKeys.add(k.id)));
  document
    .querySelector("[data-create-site]")
    ?.addEventListener("click", () => $("addSite").click());
  if (!runs.length)
    $("runRows").innerHTML =
      `<div class="empty">${icon("activity")}<strong>暂无检测任务</strong></div>`;
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
      field[0] === "value" ? [field[0], field[1], "text", field[3]] : field,
    ),
    action,
  );
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
    const heading = dialog.querySelector(".heading");
    if (heading) heading.after(inlineMessage);
    else dialog.prepend(inlineMessage);
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
let tooltipAnchor, tooltipScope, tooltipCloseTimer;
function closeRecentTooltip() {
  clearTimeout(tooltipCloseTimer);
  tooltipAnchor?.removeAttribute("aria-describedby");
  tooltipAnchor = null;
  tooltipScope = null;
  recentTooltip.hidden = true;
}
function showRecentTooltip(anchor) {
  clearTimeout(tooltipCloseTimer);
  if (tooltipAnchor === anchor) return;
  closeRecentTooltip();
  tooltipAnchor = anchor;
  tooltipScope = anchor.closest("tbody");
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
    if (event.relatedTarget instanceof Node &&
        (tooltipAnchor?.contains(event.relatedTarget) || recentTooltip.contains(event.relatedTarget))) return;
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
  if (!tooltipAnchor || tooltipAnchor.isConnected) return;
  // Polling replaces table rows. Keep the open tooltip attached to the same
  // item in the same table (a model can also appear in favorites).
  const key = tooltipAnchor.dataset.tooltipKey;
  const replacement = key && [...(tooltipScope?.querySelectorAll("[data-tooltip-key]") || [])]
    .find(anchor => anchor.dataset.tooltipKey === key && anchor.getClientRects().length);
  if (!replacement) { closeRecentTooltip(); return; }
  tooltipAnchor.removeAttribute("aria-describedby");
  tooltipAnchor = replacement;
  replacement.setAttribute("aria-describedby", recentTooltip.id);
  if (recentTooltip.textContent !== replacement.dataset.tooltip)
    recentTooltip.textContent = replacement.dataset.tooltip;
}).observe($("overview"), { childList: true, subtree: true });
