'use strict';
const startingCraneRides = new Set();
const craneLanguageNames = {zh:'中文', en:'英语', ja:'日语', ko:'韩语', fr:'法语', es:'西班牙语', de:'德语', pt:'葡萄牙语'};
const craneStatusNames = {Queued:'排队中', Running:'绘制中', Success:'已生成', Failed:'失败', Interrupted:'已中断'};
let craneDialogModel = null, craneSelectedRun = null, cranePollTimer = null, craneDialogRequest = 0;

function craneIsBusy(run) { return ['Queued', 'Running'].includes(run?.status); }
function craneSeconds(ms) { return ms == null ? '—' : (ms / 1000).toFixed(1) + 's'; }
function craneTooltip(run, count) {
    if (!run) return '尚未进行丹顶鹤骑行检测';
    const elapsed = craneIsBusy(run) ? `\n已等待：${relativeTime(run.createdAt).replace('前', '')}` : '';
    return `丹顶鹤骑行 · ${craneStatusNames[run.status] || run.status}${elapsed}\n时间：${time(run.createdAt)}\n语言：${craneLanguageNames[run.language] || run.language}\n模型用时：${craneSeconds(run.latencyMs)} · Reason Tok：${run.reasoningTokens ?? '—'}${run.error ? '\n' + run.error : ''}\n点击查看最近 ${count} 次记录`;
}
function craneRidingCell(model) {
    const entry = craneRides.find(r => r.modelId === model.id);
    const latest = entry?.latest;
    const busy = startingCraneRides.has(model.id) || craneIsBusy(latest);
    const imageId = latest?.status === 'Success' ? latest.id : entry?.latestSuccessId;
    const status = latest && latest.status !== 'Success' ? `<span class="crane-status probe-${esc(latest.status)}">${esc(craneStatusNames[latest.status] || latest.status)}</span>` : '';
    const preview = entry
        ? `<button type="button" class="crane-thumb${imageId ? '' : ' is-empty'}" data-crane-history="${model.id}" data-tooltip-key="crane-${model.id}" data-tooltip="${esc(craneTooltip(latest, entry.count))}" aria-label="查看 ${esc(model.name)} 的丹顶鹤骑行历史">${imageId ? `<img src="/api/crane-rides/${imageId}/image.svg" alt="丹顶鹤骑行检测结果">` : ''}${status}</button>`
        : '<span class="muted crane-none">未检测</span>';
    return `<span class="crane-cell">${preview}<button type="button" class="icon-button juice-refresh" data-crane-start="${model.id}" title="开始丹顶鹤骑行检测（手动触发）" aria-label="开始 ${esc(model.name)} 的丹顶鹤骑行检测" ${busy ? 'disabled aria-busy="true"' : ''}><i data-lucide="${busy ? 'loader' : 'bike'}"></i></button></span>`;
}
async function startCraneRiding(id) {
    if (startingCraneRides.has(id)) return;
    startingCraneRides.add(id);
    render();
    try {
        await api(`/models/${id}/crane-riding`, 'POST', {});
        message('丹顶鹤骑行检测已加入后台队列，刷新或关闭页面不会中断');
        await refresh();
        if ($('detail').open && $('detailBody').querySelector(`[data-crane-dialog="${id}"]`)) await openCraneHistory(id, null);
    } catch (error) { message(error.message); }
    finally { startingCraneRides.delete(id); render(); }
}
function craneHistoryItem(run) {
    const selected = run.id === craneSelectedRun;
    return `<button type="button" class="crane-history-item${selected ? ' is-selected' : ''}" data-crane-run="${run.id}" aria-pressed="${selected}">${run.status === 'Success' ? `<img src="/api/crane-rides/${run.id}/image.svg" alt="丹顶鹤骑行检测结果">` : `<span class="crane-history-placeholder probe-${esc(run.status)}">${esc(craneStatusNames[run.status] || run.status)}</span>`}<span class="crane-history-meta"><span>${esc(time(run.createdAt))}</span><span class="probe-${esc(run.status)}">${esc(craneStatusNames[run.status] || run.status)} · ${esc(craneLanguageNames[run.language] || run.language)}</span></span></button>`;
}
function craneRunDetail(run) {
    return `${run.status === 'Success' ? `<img class="crane-large" src="/api/crane-rides/${run.id}/image.svg" alt="${esc(run.modelName)} 绘制的丹顶鹤骑自行车"><div class="crane-downloads"><a href="/api/crane-rides/${run.id}/image.svg?download=true" download>下载 SVG</a></div>` : ''}
<div class="detail-metrics"><div><span>状态</span><strong class="probe-${esc(run.status)}">${esc(craneStatusNames[run.status] || run.status)}</strong></div><div><span>模型用时</span><strong>${craneSeconds(run.latencyMs)}</strong></div><div><span>Reason Tok</span><strong>${esc(run.reasoningTokens ?? '—')}</strong></div></div>
<p class="muted">${esc(time(run.createdAt))} · 题目语言：${esc(craneLanguageNames[run.language] || run.language)}${run.completedAt ? ' · 完成于 ' + esc(time(run.completedAt)) : ''}</p>
${run.error ? `<p class="inline-error">${esc(run.error)}</p>` : ''}
${run.svg ? `<details><summary>展示用 SVG 源代码</summary><pre>${esc(run.svg)}</pre></details>` : ''}
${run.prompt ? `<details><summary>题目</summary><pre>${esc(run.prompt)}</pre></details>` : ''}
${run.response ? `<details><summary>模型完整回复</summary><pre>${esc(run.response)}</pre></details>` : ''}`;
}
async function openCraneHistory(modelId, runId) {
    clearTimeout(cranePollTimer);
    const request = ++craneDialogRequest;
    const previousDialog = $('detailBody').querySelector('[data-crane-dialog]');
    const model = sites.flatMap(s => s.keys.flatMap(k => k.models)).find(m => m.id === modelId);
    const history = await api(`/models/${modelId}/crane-rides`);
    const selected = runId && history.some(r => r.id === runId) ? runId : history[0]?.id ?? null;
    const run = selected ? await api(`/crane-rides/${selected}`) : null;
    if (request !== craneDialogRequest || previousDialog && !previousDialog.isConnected) return;
    const open = [...$('detailBody').querySelectorAll('.crane-run details[open] summary')].map(s => s.textContent);
    craneDialogModel = modelId;
    craneSelectedRun = selected;
    $('detailTitle').textContent = '丹顶鹤骑行检测';
    $('detailBody').innerHTML = `<div data-crane-dialog="${modelId}"><div class="detail-head"><div><div class="detail-model">${esc(model?.name || run?.modelName || '')}</div><div class="muted">观察丹顶鹤、车架与骑行动作的空间关系 · 保留最近 ${history.length}/20 次</div></div><button type="button" class="primary" data-crane-start="${modelId}" ${history.some(craneIsBusy) || startingCraneRides.has(modelId) ? 'disabled' : ''}>开始新检测</button></div>
<p class="muted">参考“鹈鹕骑行”SVG 绘图测试；“已生成”表示取得有效图像，画面质量请结合实际绘图判断。</p>
<div class="crane-history">${history.map(craneHistoryItem).join('') || '<p class="muted">暂无记录</p>'}</div>
<div class="crane-run">${run ? craneRunDetail(run) : ''}</div></div>`;
    for (const summary of $('detailBody').querySelectorAll('.crane-run details summary'))
        if (open.includes(summary.textContent)) summary.parentElement.open = true;
    if (!$('detail').open) $('detail').showModal();
    window.lucide?.createIcons();
    if (history.some(craneIsBusy)) cranePollTimer = setTimeout(() => {
        if ($('detail').open && $('detailBody').querySelector(`[data-crane-dialog="${modelId}"]`)) openCraneHistory(modelId, craneSelectedRun).catch(error => message(error.message));
    }, 5000);
}
$('detail').addEventListener('close', () => { clearTimeout(cranePollTimer); craneDialogModel = null; craneDialogRequest++; });
document.addEventListener('click', event => {
    const start = event.target.closest('button[data-crane-start]');
    if (start && !start.disabled) { startCraneRiding(Number(start.dataset.craneStart)); return; }
    const history = event.target.closest('button[data-crane-history]');
    if (history) { openCraneHistory(Number(history.dataset.craneHistory), null).catch(error => message(error.message)); return; }
    const run = event.target.closest('button[data-crane-run]');
    if (run && craneDialogModel != null) openCraneHistory(craneDialogModel, Number(run.dataset.craneRun)).catch(error => message(error.message));
});
