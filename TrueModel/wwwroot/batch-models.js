"use strict";
const batchModelEditor = $("batchModelEditor");
let batchModelSelection = new Set(), savingBatchModels = false, batchModelError = "";
const allBatchModels = () => sites.flatMap(s => s.keys.flatMap(k => k.models.map(m => ({ s, k, m }))));
const scopedBatchModels = () => allBatchModels().filter(({ s, k }) =>
  (!$("batchModelSite").value || s.id === Number($("batchModelSite").value)) &&
  (!$("batchModelKey").value || k.id === Number($("batchModelKey").value)));
const visibleBatchModels = () => scopedBatchModels().filter(({ m }) =>
  m.name.toLowerCase().includes($("batchModelSearch").value.trim().toLowerCase()));

function syncBatchModelFilters() {
  const siteId = $("batchModelSite").value;
  $("batchModelSite").innerHTML = '<option value="">全部站点</option>' +
    sites.map(s => `<option value="${s.id}">${esc(s.name)}</option>`).join("");
  $("batchModelSite").value = sites.some(s => String(s.id) === siteId) ? siteId : "";
  const keyId = $("batchModelKey").value;
  const keys = sites.filter(s => !$("batchModelSite").value || String(s.id) === $("batchModelSite").value)
    .flatMap(s => s.keys.map(k => ({ s, k })));
  $("batchModelKey").innerHTML = '<option value="">全部 Key</option>' +
    keys.map(({ s, k }) => `<option value="${k.id}">${esc(s.name)} / ${esc(k.name)}</option>`).join("");
  $("batchModelKey").value = keys.some(({ k }) => String(k.id) === keyId) ? keyId : "";
}

function replacementModelName(name) {
  const find = $("batchModelFind").value;
  if (!find) return name;
  const replacement = $("batchModelReplacement").value;
  return ($("batchModelMatch").value === "whole"
    ? name === find ? replacement : name
    : name.split(find).join(replacement)).trim();
}

function renderBatchModelOptions() {
  const focusedId = $("batchModelOptions").contains(document.activeElement) ? document.activeElement.value : null;
  const scoped = scopedBatchModels();
  const availableIds = new Set(scoped.map(({ m }) => m.id));
  for (const id of batchModelSelection) if (!availableIds.has(id)) batchModelSelection.delete(id);
  const visible = visibleBatchModels();
  const selected = scoped.filter(({ m }) => batchModelSelection.has(m.id));
  const changed = selected.filter(({ m }) => replacementModelName(m.name) !== m.name);
  let validationError = "";
  if (changed.some(({ m }) => !replacementModelName(m.name))) validationError = "替换后的模型 ID 不能为空";
  const finalNames = new Set();
  for (const { k, m } of allBatchModels()) {
    const name = batchModelSelection.has(m.id) ? replacementModelName(m.name) : m.name;
    const identity = JSON.stringify([k.id, name]);
    if (finalNames.has(identity)) validationError = "替换后同一 Key 下存在重复模型 ID，请调整替换内容或选择范围";
    finalNames.add(identity);
  }
  $("batchModelOptions").innerHTML = visible.map(({ s, k, m }) => {
    const name = replacementModelName(m.name);
    return `<label class="model-option batch-model-option"><input type="checkbox" value="${m.id}" ${batchModelSelection.has(m.id) ? "checked" : ""} ${savingBatchModels ? "disabled" : ""} />
      <span class="batch-model-info"><small>${esc(s.name)} / ${esc(k.name)}</small><code>${esc(m.name)}</code>${name !== m.name ? `<span class="batch-model-preview ${name ? "" : "invalid"}">→ <code>${name ? esc(name) : "（空 ID）"}</code></span>` : ""}</span></label>`;
  }).join("") || '<p class="empty">没有匹配的模型</p>';
  const selectedVisible = visible.filter(({ m }) => batchModelSelection.has(m.id)).length;
  $("selectBatchModels").checked = visible.length > 0 && selectedVisible === visible.length;
  $("selectBatchModels").indeterminate = selectedVisible > 0 && selectedVisible < visible.length;
  $("selectBatchModels").disabled = savingBatchModels || !visible.length;
  $("batchModelVisibleCount").textContent = `${visible.length} 个模型`;
  $("batchModelSelectedCount").textContent = `已选 ${selected.length} 个${$("batchModelFind").value ? ` · 将替换 ${changed.length} 个` : ""}`;
  $("batchModelMessage").textContent = batchModelError || validationError;
  $("deleteBatchModels").disabled = savingBatchModels || !selected.length;
  $("replaceBatchModels").disabled = savingBatchModels || !changed.length || Boolean(validationError);
  for (const id of ["closeBatchModelEditor", "batchModelSite", "batchModelKey", "batchModelSearch", "batchModelMatch", "batchModelFind", "batchModelReplacement"])
    $(id).disabled = savingBatchModels;
  $("batchModelOptions").setAttribute("aria-busy", String(savingBatchModels));
  if (focusedId) [...$("batchModelOptions").querySelectorAll("input")].find(input => input.value === focusedId)?.focus({ preventScroll: true });
}

function openBatchModelEditor(siteId, keyId) {
  if (savingBatchModels) return;
  batchModelSelection.clear();
  batchModelError = "";
  for (const id of ["batchModelSearch", "batchModelFind", "batchModelReplacement"]) $(id).value = "";
  $("batchModelMatch").value = "partial";
  $("batchModelSite").value = "";
  $("batchModelKey").value = "";
  syncBatchModelFilters();
  const site = sites.find(s => s.id === siteId || s.keys.some(k => k.id === keyId));
  $("batchModelSite").value = site ? String(site.id) : "";
  syncBatchModelFilters();
  $("batchModelKey").value = keyId ? String(keyId) : "";
  renderBatchModelOptions();
  batchModelEditor.showModal();
  icons();
  $("batchModelSearch").focus();
}

document.addEventListener("click", event => {
  const button = event.target.closest("button[data-manage-models]");
  if (!button || button.disabled) return;
  event.preventDefault();
  openBatchModelEditor(Number(button.dataset.manageSite) || null, Number(button.dataset.manageKey) || null);
});
$("closeBatchModelEditor").onclick = () => { if (!savingBatchModels) batchModelEditor.close(); };
batchModelEditor.addEventListener("cancel", event => { if (savingBatchModels) event.preventDefault(); });
for (const id of ["batchModelSite", "batchModelKey"]) $(id).onchange = () => {
  batchModelSelection.clear();
  batchModelError = "";
  if (id === "batchModelSite") $("batchModelKey").value = "";
  syncBatchModelFilters();
  renderBatchModelOptions();
};
for (const id of ["batchModelSearch", "batchModelFind", "batchModelReplacement", "batchModelMatch"]) $(id).addEventListener("input", () => {
  batchModelError = "";
  renderBatchModelOptions();
});
$("batchModelOptions").onchange = event => {
  if (!event.target.matches('input[type="checkbox"]') || savingBatchModels) return;
  const id = Number(event.target.value);
  batchModelError = "";
  if (event.target.checked && batchModelSelection.size >= 500) batchModelError = "每次最多处理 500 个模型";
  else if (event.target.checked) batchModelSelection.add(id);
  else batchModelSelection.delete(id);
  renderBatchModelOptions();
};
$("selectBatchModels").onchange = event => {
  if (savingBatchModels) return;
  batchModelError = "";
  for (const { m } of visibleBatchModels()) {
    if (!event.target.checked) batchModelSelection.delete(m.id);
    else if (batchModelSelection.size < 500) batchModelSelection.add(m.id);
    else if (!batchModelSelection.has(m.id)) batchModelError = "每次最多处理 500 个模型，请分批操作";
  }
  renderBatchModelOptions();
};

async function saveBatchModels(kind) {
  if (savingBatchModels || $(kind === "delete" ? "deleteBatchModels" : "replaceBatchModels").disabled) return;
  const selected = scopedBatchModels().filter(({ m }) => batchModelSelection.has(m.id));
  const count = kind === "delete" ? selected.length : selected.filter(({ m }) => replacementModelName(m.name) !== m.name).length;
  if (!confirm(kind === "delete"
    ? `确认删除所选 ${count} 个模型？未完成的归因检测将取消，历史记录仍然保留。`
    : `确认替换 ${count} 个模型 ID？替换后需重新检测，收藏和历史记录仍然保留。`)) return;
  const body = { ids: selected.map(({ m }) => m.id) };
  if (kind === "replace") Object.assign(body, {
    find: $("batchModelFind").value,
    replacement: $("batchModelReplacement").value,
    matchWholeId: $("batchModelMatch").value === "whole",
  });
  savingBatchModels = true;
  batchModelError = "";
  renderBatchModelOptions();
  try {
    const result = await api(`/models/batch-${kind}`, "POST", body);
    batchModelEditor.close();
    message(kind === "delete" ? `已删除 ${result.deleted} 个模型${result.skipped ? `，${result.skipped} 个已不存在` : ""}`
      : `已替换 ${result.replaced} 个模型 ID${result.skipped ? `，${result.skipped} 个未变化` : ""}`);
    await refresh();
  } catch (error) {
    if (batchModelEditor.open) batchModelError = error.message;
    else message(error.message);
  } finally {
    savingBatchModels = false;
    renderBatchModelOptions();
  }
}
$("deleteBatchModels").onclick = () => saveBatchModels("delete");
$("replaceBatchModels").onclick = () => saveBatchModels("replace");

const renderBeforeBatchModels = render;
render = function () {
  renderBeforeBatchModels();
  $("manageModels").disabled = !sites.some(s => s.keys.some(k => k.models.length));
  if (batchModelEditor.open && !savingBatchModels) {
    syncBatchModelFilters();
    renderBatchModelOptions();
  }
};
