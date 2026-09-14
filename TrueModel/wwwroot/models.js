"use strict";
const picker = $("modelPicker");
let pickerKeyId,
  modelOptions = [],
  modelSelection = new Set(),
  discoveryGeneration = 0;
let loadingModels = false,
  savingModels = false,
  discoveryError = "";
const configuredNames = () =>
  new Set(
    sites
      .flatMap((s) => s.keys)
      .find((k) => k.id === pickerKeyId)
      ?.models.map((m) => m.name) || [],
  );
const visibleModels = () =>
  modelOptions.filter((name) =>
    name
      .toLowerCase()
      .includes($("modelPickerSearch").value.trim().toLowerCase()),
  );

function renderModelOptions() {
  const focusedName = $("availableModels").contains(document.activeElement)
    ? document.activeElement.value
    : null;
  const configured = configuredNames();
  for (const name of modelSelection)
    if (configured.has(name)) modelSelection.delete(name);
  const visible = visibleModels();
  $("availableModels").setAttribute("aria-busy", String(loadingModels));
  $("availableModels").innerHTML = loadingModels
    ? '<p class="empty">正在获取模型…</p>'
    : discoveryError
      ? '<p class="empty">获取失败</p>'
      : visible
          .map(
            (name) =>
              `<label class="model-option"><input type="checkbox" value="${esc(name)}" ${configured.has(name) || savingModels ? "disabled" : ""} ${modelSelection.has(name) || configured.has(name) ? "checked" : ""}><span>${esc(name)}</span>${configured.has(name) ? "<small>已添加</small>" : ""}</label>`,
          )
          .join("") ||
        `<p class="empty">${modelOptions.length ? "没有匹配的模型" : "接口未返回可用模型"}</p>`;
  const selectable = visible.filter((name) => !configured.has(name));
  const selected = selectable.filter((name) => modelSelection.has(name)).length;
  $("selectVisibleModels").checked =
    selectable.length > 0 && selected === selectable.length;
  $("selectVisibleModels").indeterminate =
    selected > 0 && selected < selectable.length;
  $("selectVisibleModels").disabled =
    loadingModels || savingModels || !selectable.length;
  $("availableModelCount").textContent = loadingModels
    ? ""
    : `${visible.length} 个模型`;
  $("selectedModelCount").textContent = `已选 ${modelSelection.size} 个`;
  $("addSelectedModels").disabled =
    loadingModels || savingModels || !modelSelection.size;
  $("reloadModels").disabled = loadingModels || savingModels;
  if (focusedName)
    [...$("availableModels").querySelectorAll("input")]
      .find((input) => input.value === focusedName)
      ?.focus({ preventScroll: true });
}

async function discoverModels() {
  const generation = ++discoveryGeneration;
  loadingModels = true;
  discoveryError = "";
  $("modelPickerMessage").textContent = "";
  renderModelOptions();
  try {
    const data = await api(`/keys/${pickerKeyId}/discover-models`, "POST", {});
    if (generation !== discoveryGeneration || !picker.open) return;
    modelOptions = data.models;
    modelSelection = new Set(
      [...modelSelection].filter((name) => modelOptions.includes(name)),
    );
  } catch (error) {
    if (generation !== discoveryGeneration || !picker.open) return;
    discoveryError = error.message;
    modelOptions = [];
    modelSelection.clear();
    $("modelPickerMessage").textContent = error.message;
  } finally {
    if (generation === discoveryGeneration && picker.open) {
      loadingModels = false;
      renderModelOptions();
    }
  }
}

function setPickerMode(manual) {
  $("manualPanel").hidden = !manual;
  $("discoveredPanel").hidden = manual;
  $("modelPickerFooter").hidden = manual;
  $("manualMode").setAttribute("aria-selected", String(manual));
  $("discoverMode").setAttribute("aria-selected", String(!manual));
  $("manualMode").tabIndex = manual ? 0 : -1;
  $("discoverMode").tabIndex = manual ? -1 : 0;
  if (manual) $("manualModelName").focus();
}

function openModelPicker(keyId) {
  if (savingModels) return;
  const site = sites.find((s) => s.keys.some((k) => k.id === keyId));
  const key = site?.keys.find((k) => k.id === keyId);
  if (!key) return;
  pickerKeyId = keyId;
  modelOptions = [];
  modelSelection.clear();
  discoveryError = "";
  $("modelPickerSearch").value = "";
  $("manualModelName").value = "";
  $("modelPickerMessage").textContent = "";
  $("modelPickerContext").textContent = `${site.name} / ${key.name}`;
  setPickerMode(false);
  picker.showModal();
  icons();
  $("modelPickerSearch").focus();
  discoverModels();
}

// Capture only model-add commands before the existing manual editor handler.
document.addEventListener(
  "click",
  (event) => {
    const button = event.target.closest("button[data-model]");
    if (!button || button.disabled) return;
    event.stopImmediatePropagation();
    event.preventDefault();
    openModelPicker(Number(button.dataset.model));
  },
  true,
);
$("closeModelPicker").onclick = () => {
  if (!savingModels) picker.close();
};
picker.addEventListener("cancel", (event) => {
  if (savingModels) event.preventDefault();
});
picker.addEventListener("close", () => {
  discoveryGeneration++;
});
$("discoverMode").onclick = () => setPickerMode(false);
$("manualMode").onclick = () => setPickerMode(true);
document.querySelector(".picker-modes").onkeydown = (event) => {
  if (["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) {
    event.preventDefault();
    const manual =
      event.key === "End" || (event.key !== "Home" && $("manualPanel").hidden);
    setPickerMode(manual);
    $(manual ? "manualMode" : "discoverMode").focus();
  }
};
$("reloadModels").onclick = discoverModels;
$("modelPickerSearch").oninput = renderModelOptions;
$("availableModels").onchange = (event) => {
  const input = event.target;
  if (!input.matches('input[type="checkbox"]') || savingModels) return;
  if (input.checked && modelSelection.size >= 500) {
    input.checked = false;
    $("modelPickerMessage").textContent = "每次最多添加 500 个模型";
    return;
  }
  if (input.checked) modelSelection.add(input.value);
  else modelSelection.delete(input.value);
  renderModelOptions();
};
$("selectVisibleModels").onchange = (event) => {
  const configured = configuredNames();
  for (const name of visibleModels().filter((name) => !configured.has(name))) {
    if (!event.target.checked) modelSelection.delete(name);
    else if (modelSelection.size < 500) modelSelection.add(name);
  }
  renderModelOptions();
};
async function saveModels(names) {
  if (savingModels) return;
  savingModels = true;
  $("modelPickerMessage").textContent = "";
  $("closeModelPicker").disabled = true;
  $("manualModelForm").querySelector("button").disabled = true;
  renderModelOptions();
  try {
    const result = await api(`/keys/${pickerKeyId}/models/batch`, "POST", {
      names,
    });
    picker.close();
    message(
      `已添加 ${result.added} 个模型${result.skipped ? `，${result.skipped} 个已存在` : ""}`,
    );
    await refresh();
  } catch (error) {
    if (picker.open) $("modelPickerMessage").textContent = error.message;
    else message(error.message);
  } finally {
    savingModels = false;
    $("closeModelPicker").disabled = false;
    $("manualModelForm").querySelector("button").disabled = false;
    renderModelOptions();
  }
}
$("addSelectedModels").onclick = () => saveModels([...modelSelection]);
$("manualModelForm").onsubmit = (event) => {
  event.preventDefault();
  const name = $("manualModelName").value.trim();
  if (name) saveModels([name]);
};
