'use strict';
// Manual-only world map probe: thumbnails in model tables plus a 20-run history dialog.
const startingWorldMaps = new Set();
const worldMapLanguageNames = {zh:'中文', en:'英语', ja:'日语', ko:'韩语', fr:'法语', es:'西班牙语', de:'德语', pt:'葡萄牙语'};
const worldMapStatusNames = {Queued:'排队中', Running:'绘制中', Success:'成功', Failed:'失败', Interrupted:'已中断'};
const worldMapPoints = 90 * 180;
let worldMapDialogModel = null, worldMapSelectedRun = null, worldMapPollTimer = null;

function worldMapIsBusy(run) { return ['Queued', 'Running'].includes(run?.status); }
function worldMapLandRatio(run) { return run?.landCount == null ? '—' : (run.landCount / worldMapPoints * 100).toFixed(1) + '%'; }
function worldMapSeconds(ms) { return ms == null ? '—' : (ms / 1000).toFixed(1) + 's'; }
function worldMapTooltip(run, count) {
    if (!run) return '尚未进行世界地图检测';
    const elapsed = worldMapIsBusy(run) ? `\n已等待：${relativeTime(run.createdAt).replace('前', '')}` : '';
    return `世界地图 · ${worldMapStatusNames[run.status] || run.status}${elapsed}\n时间：${time(run.createdAt)}\n语言：${worldMapLanguageNames[run.language] || run.language}\n模型用时：${worldMapSeconds(run.latencyMs)} · Reason Tok：${run.reasoningTokens ?? '—'}\n陆地占比：${worldMapLandRatio(run)}${run.error ? '\n' + run.error : ''}\n点击查看最近 ${count} 次记录`;
}
function worldMapCell(m) {
    const entry = worldMaps.find(w => w.modelId === m.id);
    const latest = entry?.latest;
    const busy = startingWorldMaps.has(m.id) || worldMapIsBusy(latest);
    const imageId = latest?.status === 'Success' ? latest.id : entry?.latestSuccessId;
    const status = latest && latest.status !== 'Success' ? `<span class="world-map-status probe-${esc(latest.status)}">${esc(worldMapStatusNames[latest.status] || latest.status)}</span>` : '';
    const preview = entry
        ? `<button type="button" class="world-map-thumb${imageId ? '' : ' is-empty'}" data-world-map-history="${m.id}" data-tooltip-key="world-map-${m.id}" data-tooltip="${esc(worldMapTooltip(latest, entry.count))}" aria-label="查看 ${esc(m.name)} 的世界地图历史">${imageId ? `<img src="/api/world-maps/${imageId}/image" width="90" height="45" alt="">` : ''}${status}</button>`
        : '<span class="muted world-map-none">未检测</span>';
    return `<span class="world-map-cell">${preview}<button type="button" class="icon-button juice-refresh" data-world-map-start="${m.id}" title="开始世界地图检测（手动触发，耗时可能较长）" aria-label="开始 ${esc(m.name)} 的世界地图检测" ${busy ? 'disabled aria-busy="true"' : ''}><i data-lucide="${busy ? 'loader' : 'map'}"></i></button></span>`;
}
async function startWorldMap(id) {
    if (startingWorldMaps.has(id)) return;
    startingWorldMaps.add(id);
    render();
    try {
        await api(`/models/${id}/world-map`, 'POST', {});
        message('世界地图检测已加入后台队列；模型编写脚本可能需要数分钟，刷新或关闭页面不会中断');
        await refresh();
        if ($('detail').open && worldMapDialogModel === id) await openWorldMapHistory(id, null);
    } catch (error) { message(error.message); }
    finally { startingWorldMaps.delete(id); render(); }
}
function worldMapHistoryItem(run) {
    const selected = run.id === worldMapSelectedRun;
    return `<button type="button" class="world-map-history-item${selected ? ' is-selected' : ''}" data-world-map-run="${run.id}" aria-pressed="${selected}">${run.status === 'Success' ? `<img src="/api/world-maps/${run.id}/image" width="180" height="90" alt="">` : `<span class="world-map-history-placeholder probe-${esc(run.status)}">${esc(worldMapStatusNames[run.status] || run.status)}</span>`}<span class="world-map-history-meta"><span>${esc(time(run.createdAt))}</span><span class="probe-${esc(run.status)}">${esc(worldMapStatusNames[run.status] || run.status)}${run.status === 'Success' ? ' · 陆地 ' + worldMapLandRatio(run) : ''}</span></span></button>`;
}
function worldMapRunDetail(run) {
    const links = run.status === 'Success' ? `<div class="world-map-downloads"><a href="/api/world-maps/${run.id}/image?scale=8" download>1440×720 PNG</a><a href="/api/world-maps/${run.id}/image" download>180×90 PNG</a><a href="/api/world-maps/${run.id}/matrix.csv" download>判断矩阵 CSV</a>${run.script ? `<a href="/api/world-maps/${run.id}/script.py" download>Python 脚本</a>` : ''}</div>` : run.script ? `<div class="world-map-downloads"><a href="/api/world-maps/${run.id}/script.py" download>Python 脚本</a></div>` : '';
    return `${run.status === 'Success' ? `<img class="world-map-large" src="/api/world-maps/${run.id}/image?scale=8" width="1440" height="720" alt="${esc(run.modelName)} 的世界地图（白色为陆地，黑色为水域）">` : ''}
<div class="detail-metrics"><div><span>状态</span><strong class="probe-${esc(run.status)}">${esc(worldMapStatusNames[run.status] || run.status)}</strong></div><div><span>陆地占比（16,200 个采样点）</span><strong>${worldMapLandRatio(run)}</strong></div><div><span>模型用时</span><strong>${worldMapSeconds(run.latencyMs)}</strong></div><div><span>Reason Tok</span><strong>${esc(run.reasoningTokens ?? '—')}</strong></div></div>
<p class="muted">${esc(time(run.createdAt))} · 题目语言：${esc(worldMapLanguageNames[run.language] || run.language)}${run.completedAt ? ' · 完成于 ' + esc(time(run.completedAt)) : ''}</p>
${run.error ? `<p class="inline-error">${esc(run.error)}</p>` : ''}${links}
${run.output ? `<details><summary>脚本运行输出</summary><pre>${esc(run.output)}</pre></details>` : ''}
${run.script ? `<details><summary>模型生成的 Python 脚本</summary><pre>${esc(run.script)}</pre></details>` : ''}
${run.prompt ? `<details><summary>题目</summary><pre>${esc(run.prompt)}</pre></details>` : ''}
${run.response ? `<details><summary>模型完整回复</summary><pre>${esc(run.response)}</pre></details>` : ''}`;
}
async function openWorldMapHistory(modelId, runId) {
    clearTimeout(worldMapPollTimer);
    const model = sites.flatMap(s => s.keys.flatMap(k => k.models)).find(m => m.id === modelId);
    const history = await api(`/models/${modelId}/world-maps`);
    worldMapDialogModel = modelId;
    worldMapSelectedRun = runId && history.some(r => r.id === runId) ? runId : history[0]?.id ?? null;
    const run = worldMapSelectedRun ? await api(`/world-maps/${worldMapSelectedRun}`) : null;
    // Keep open <details> while polling refreshes the dialog.
    const open = [...$('detailBody').querySelectorAll('.world-map-run details[open] summary')].map(s => s.textContent);
    $('detailTitle').textContent = '世界地图检测';
    $('detailBody').innerHTML = `<div class="detail-head"><div><div class="detail-model">${esc(model?.name || run?.modelName || '')}</div><div class="muted">仅凭模型自身地理知识绘制的海陆图（白色为陆地，黑色为水域）· 保留最近 ${history.length}/20 次</div></div><button type="button" class="primary" data-world-map-start="${modelId}" ${history.some(worldMapIsBusy) || startingWorldMaps.has(modelId) ? 'disabled' : ''}>开始新检测</button></div>
<div class="world-map-history" role="list">${history.map(worldMapHistoryItem).join('') || '<p class="muted">暂无记录</p>'}</div>
<div class="world-map-run">${run ? worldMapRunDetail(run) : ''}</div>`;
    for (const summary of $('detailBody').querySelectorAll('.world-map-run details summary'))
        if (open.includes(summary.textContent)) summary.parentElement.open = true;
    if (!$('detail').open) $('detail').showModal();
    window.lucide?.createIcons();
    if (history.some(worldMapIsBusy)) worldMapPollTimer = setTimeout(() => {
        if ($('detail').open && worldMapDialogModel === modelId) openWorldMapHistory(modelId, worldMapSelectedRun).catch(() => {});
    }, 5000);
}
$('detail').addEventListener('close', () => { clearTimeout(worldMapPollTimer); worldMapDialogModel = null; });
document.addEventListener('click', event => {
    const start = event.target.closest('button[data-world-map-start]');
    if (start && !start.disabled) { startWorldMap(Number(start.dataset.worldMapStart)); return; }
    const history = event.target.closest('button[data-world-map-history]');
    if (history) { openWorldMapHistory(Number(history.dataset.worldMapHistory), null).catch(error => message(error.message)); return; }
    const run = event.target.closest('button[data-world-map-run]');
    if (run && worldMapDialogModel != null) openWorldMapHistory(worldMapDialogModel, Number(run.dataset.worldMapRun)).catch(error => message(error.message));
});
