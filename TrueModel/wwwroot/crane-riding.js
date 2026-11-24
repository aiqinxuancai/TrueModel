'use strict';
const startingCraneRides = new Set();
const craneLanguageNames = {zh:'中文', en:'英语', ja:'日语', ko:'韩语', fr:'法语', es:'西班牙语', de:'德语', pt:'葡萄牙语'};
const craneStatusNames = {Queued:'排队中', Running:'绘制中', Success:'已生成', Failed:'失败', Interrupted:'已中断'};
const craneEffortNames = {default:'接口默认', none:'none · 不思考', minimal:'minimal · 最低', low:'low · 低', medium:'medium · 中', high:'high · 高', xhigh:'xhigh · 更高'};
let craneDialogModel = null, craneSelectedRun = null, cranePollTimer = null, craneDialogRequest = 0, craneStartModelId = null;

const craneStartPicker = document.createElement('dialog');
craneStartPicker.id = 'craneStartPicker';
craneStartPicker.setAttribute('aria-labelledby', 'craneStartTitle');
craneStartPicker.innerHTML = `
<form id="craneStartForm">
    <div class="heading">
        <div><h2 id="craneStartTitle">丹顶鹤骑行检测</h2><p id="craneStartModel"></p></div>
        <button type="button" id="craneStartClose" class="icon-button ghost" aria-label="关闭" title="关闭"><i data-lucide="x"></i></button>
    </div>
    <div class="crane-start-body">
        <fieldset class="crane-mode-group">
            <legend>选择检测类型</legend>
            <label class="crane-mode-option">
                <input type="radio" name="craneContextMode" value="standard" checked />
                <span class="crane-mode-content">
                    <span class="crane-mode-title">普通上下文 <span class="pill">默认</span></span>
                    <span class="crane-mode-desc">沿用一轮随机问候后直接发送丹顶鹤骑行题目，输入用量低、响应更快。</span>
                </span>
            </label>
            <label class="crane-mode-option">
                <input type="radio" name="craneContextMode" value="simulated" />
                <span class="crane-mode-content">
                    <span class="crane-mode-title">10k+ 模拟上下文</span>
                    <span class="crane-mode-desc">先在本地生成至少 64 轮交替 U/A 历史对话（约 15k+ tokens 估算），再追加题目一次发送；会增加输入用量和请求耗时。</span>
                </span>
            </label>
        </fieldset>
        <p id="craneStartHint" class="muted"></p>
    </div>
    <div class="dialog-footer">
        <button type="button" id="craneStartCancel">取消</button>
        <button type="submit" class="primary">开始检测</button>
    </div>
</form>`;
document.body.append(craneStartPicker);
$('craneStartCancel').onclick = $('craneStartClose').onclick = () => craneStartPicker.close();
craneStartPicker.addEventListener('close', () => { craneStartModelId = null; });
$('craneStartForm').onsubmit = async event => {
    event.preventDefault();
    const id = craneStartModelId;
    if (id == null) return;
    const simulatedContext = $('craneStartForm').elements.craneContextMode.value === 'simulated';
    craneStartPicker.close();
    await startCraneRiding(id, simulatedContext);
};

function craneIsBusy(run) { return ['Queued', 'Running'].includes(run?.status); }
function craneSeconds(ms) { return ms == null ? '—' : (ms / 1000).toFixed(1) + 's'; }
function craneTooltip(run, count) {
    if (!run) return '尚未进行丹顶鹤骑行检测';
    const elapsed = craneIsBusy(run) ? `\n已等待：${relativeTime(run.createdAt).replace('前', '')}` : '';
    return `丹顶鹤骑行 · ${craneStatusNames[run.status] || run.status}${elapsed}\n时间：${time(run.createdAt)}\n语言：${craneLanguageNames[run.language] || run.language}\n思考等级：${craneEffortNames[run.reasoningEffort ?? 'default'] || run.reasoningEffort}\n上下文类型：${run.simulatedContext ? '10k+ 模拟上下文' : '普通上下文'}\n模型用时：${craneSeconds(run.latencyMs)} · Reason Tok：${run.reasoningTokens ?? '—'}${run.error ? '\n' + run.error : ''}\n点击查看最近 ${count} 次记录`;
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
    return `<span class="crane-cell">${preview}<button type="button" class="icon-button juice-refresh" data-crane-start="${model.id}" title="开始丹顶鹤骑行检测（选择上下文类型）" aria-label="开始 ${esc(model.name)} 的丹顶鹤骑行检测" ${busy ? 'disabled aria-busy="true"' : ''}><i data-lucide="${busy ? 'loader' : 'bike'}"></i></button></span>`;
}
function openCraneStartPicker(id) {
    const entry = craneRides.find(r => r.modelId === id);
    if (startingCraneRides.has(id) || craneIsBusy(entry?.latest)) return;
    craneStartModelId = id;
    const info = sites.flatMap(s => s.keys.flatMap(k => k.models.map(m => ({ s, k, m })))).find(x => x.m.id === id);
    $('craneStartModel').textContent = info ? `${info.m.name} · ${info.s.name} / ${info.k.name}` : '';
    $('craneStartForm').elements.craneContextMode.value = 'standard';
    $('craneStartHint').textContent = `当前题目语言：${craneLanguageNames[craneLanguage] || craneLanguage} · 思考等级：${craneEffortNames[craneEffort] || craneEffort}（可在检测设置中修改）。`;
    if (!craneStartPicker.open) craneStartPicker.showModal();
    window.lucide?.createIcons();
}
async function startCraneRiding(id, simulatedContext = false) {
    if (startingCraneRides.has(id)) return;
    startingCraneRides.add(id);
    render();
    try {
        await api(`/models/${id}/crane-riding`, 'POST', { simulatedContext });
        message(`丹顶鹤骑行检测（${simulatedContext ? '10k+ 模拟上下文' : '普通上下文'}）已加入后台队列，刷新或关闭页面不会中断`);
        await refresh();
        if ($('detail').open && $('detailBody').querySelector(`[data-crane-dialog="${id}"]`)) await openCraneHistory(id, null);
    } catch (error) { message(error.message); }
    finally { startingCraneRides.delete(id); render(); }
}
function craneHistoryItem(run) {
    const selected = run.id === craneSelectedRun;
    return `<button type="button" class="crane-history-item${selected ? ' is-selected' : ''}" data-crane-run="${run.id}" aria-pressed="${selected}">${run.status === 'Success' ? `<img src="/api/crane-rides/${run.id}/image.svg" alt="丹顶鹤骑行检测结果">` : `<span class="crane-history-placeholder probe-${esc(run.status)}">${esc(craneStatusNames[run.status] || run.status)}</span>`}<span class="crane-history-meta"><span>${esc(time(run.createdAt))}</span><span class="probe-${esc(run.status)}">${esc(craneStatusNames[run.status] || run.status)} · ${esc(craneLanguageNames[run.language] || run.language)}${run.simulatedContext ? ' · 10k+' : ''}</span></span></button>`;
}
function craneConversationDetail(run) {
    if (!run.conversationJson) return '';
    const messages = JSON.parse(run.conversationJson);
    const history = messages.slice(0, -1);
    const estimatedTokens = history.reduce((sum, m) => sum + m.content.length, 0) / 4;
    const text = messages.map(m => `${m.role === 'user' ? 'U' : 'A'}:\n${m.content}`).join('\n\n');
    return `<details><summary>请求对话（${history.length / 2} 轮 U/A 上下文 · 约 ${(estimatedTokens / 1000).toFixed(1)}k tokens，估算）</summary><pre>${esc(text)}</pre></details>`;
}
function craneRunDetail(run) {
    return `${run.status === 'Success' ? `<img class="crane-large" src="/api/crane-rides/${run.id}/image.svg" alt="${esc(run.modelName)} 绘制的丹顶鹤骑自行车"><div class="crane-downloads"><a href="/api/crane-rides/${run.id}/image.svg?download=true" download>下载 SVG</a></div>` : ''}
<div class="detail-metrics"><div><span>状态</span><strong class="probe-${esc(run.status)}">${esc(craneStatusNames[run.status] || run.status)}</strong></div><div><span>模型用时</span><strong>${craneSeconds(run.latencyMs)}</strong></div><div><span>Reason Tok</span><strong>${esc(run.reasoningTokens ?? '—')}</strong></div></div>
<p class="muted">${esc(time(run.createdAt))} · 题目语言：${esc(craneLanguageNames[run.language] || run.language)}${run.completedAt ? ' · 完成于 ' + esc(time(run.completedAt)) : ''}</p>
<p class="muted">思考等级：${esc(craneEffortNames[run.reasoningEffort ?? 'default'] || run.reasoningEffort)}</p>
<p class="muted">上下文类型：${run.simulatedContext ? '10k+ 模拟上下文（先模拟多轮 U/A 对话再提问）' : '普通上下文（一轮随机问候后提问）'}</p>
${run.error ? `<p class="inline-error">${esc(run.error)}</p>` : ''}
${run.svg ? `<details><summary>展示用 SVG 源代码</summary><pre>${esc(run.svg)}</pre></details>` : ''}
${run.prompt ? `<details><summary>题目</summary><pre>${esc(run.prompt)}</pre></details>` : ''}
${craneConversationDetail(run)}
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
    if (start && !start.disabled) { openCraneStartPicker(Number(start.dataset.craneStart)); return; }
    const history = event.target.closest('button[data-crane-history]');
    if (history) { openCraneHistory(Number(history.dataset.craneHistory), null).catch(error => message(error.message)); return; }
    const run = event.target.closest('button[data-crane-run]');
    if (run && craneDialogModel != null) openCraneHistory(craneDialogModel, Number(run.dataset.craneRun)).catch(error => message(error.message));
});
