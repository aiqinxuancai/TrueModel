'use strict';
const $ = id => document.getElementById(id);
const esc = x => String(x ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
let setupRequired = false;
let token = '', sites = [], results = [], runs = [], banks = [], editAction;
let recentModelResults = [];
function juiceLabel(r) {
    if (r?.juiceValue != null) return String(r.juiceValue);
    return probeLabel(r?.juiceStatus);
}
function juiceTooltip(r) {
    return `Juice 为模型自报值，仅供参考\n${r?.juicePrompt ? '本次请求 Prompt：\n' + r.juicePrompt : '未记录 Prompt（旧记录或尚未检测）'}`;
}
const refreshingJuice = new Set();
function juiceCell(m, r) {
    const latest = m.juiceCheckedAt && (!r || new Date(m.juiceCheckedAt).getTime() > new Date(r.startedAt).getTime() + r.latencyMs) ? m : r;
    const supported = /(?:^|[/\s:_-])(?:chat)?gpt(?:$|[-_.\s\d])/i.test(m.name);
    return `<span class="juice-cell"><span class="probe-${esc(latest?.juiceStatus || 'pending')}" tabindex="0" data-tooltip="${esc(juiceTooltip(latest))}">${juiceLabel(latest)}</span>${supported ? `<button class="icon-button juice-refresh" data-juice="${m.id}" title="单独刷新 Juice" aria-label="刷新 ${esc(m.name)} 的 Juice" ${refreshingJuice.has(m.id) ? 'disabled aria-busy="true"' : ''}><i data-lucide="refresh-cw"></i></button>` : ''}</span>`;
}
function probeLabel(status) {
    return {Success:'通过', Unavailable:'未通过', Failed:'错误'}[status] || '—';
}
function probeBadge(status) {
    return `<span class="probe-${esc(status || 'pending')}">${probeLabel(status)}</span>`;
}
function instructionTooltip(r) {
    return `是/否检测：${probeLabel(r?.instructionStatus)}\n判定：仅“是”“否”“是。”“否。”通过（忽略首尾空白）\n题目：${r?.instructionPrompt || '尚未检测'}\n回复：${r?.instructionResponse ?? '—'}${r?.instructionError ? '\n错误：' + r.instructionError : ''}`;
}
const refreshingInstruction = new Set();
function instructionCell(m, r) {
    const latest = m.instructionCheckedAt && (!r || new Date(m.instructionCheckedAt).getTime() > new Date(r.startedAt).getTime() + r.latencyMs) ? m : r;
    return `<span class="juice-cell"><span tabindex="0" data-tooltip="${esc(instructionTooltip(latest))}">${probeBadge(latest?.instructionStatus)}</span><button class="icon-button juice-refresh" data-instruction="${m.id}" title="单独刷新是/否检测" aria-label="刷新 ${esc(m.name)} 的是/否检测" ${refreshingInstruction.has(m.id) ? 'disabled aria-busy="true"' : ''}><i data-lucide="refresh-cw"></i></button></span>`;
}
async function refreshProbe(id, kind) {
    const busy = kind === 'juice' ? refreshingJuice : refreshingInstruction;
    if (busy.has(id)) return;
    busy.add(id);
    render();
    try {
        const result = await api(`/models/${id}/${kind}`, 'POST', {});
        const model = sites.flatMap(s => s.keys.flatMap(k => k.models)).find(m => m.id === id);
        if (model) Object.assign(model, result);
        return result;
    } finally { busy.delete(id); render(); }
}
document.addEventListener('click', async event => {
    const button = event.target.closest('button[data-instruction]');
    if (!button) return;
    try { const result = await refreshProbe(Number(button.dataset.instruction), 'instruction'); if (result) message('是/否检测：' + probeLabel(result.instructionStatus)); }
    catch (error) { message(error.message); }
});
async function refreshAllProbes(kind, button) {
    if (button.disabled) return;
    const models = sites.flatMap(s => s.keys.flatMap(k => k.models)).filter(m => kind !== 'juice' || /(?:^|[/\s:_-])(?:chat)?gpt(?:$|[-_.\s\d])/i.test(m.name));
    const busy = kind === 'juice' ? refreshingJuice : refreshingInstruction;
    const pending = models.filter(m => !busy.has(m.id));
    button.disabled = true;
    button.setAttribute('aria-busy', 'true');
    let errors = 0, completed = 0;
    try {
        // Limit concurrency so a bulk refresh does not flood upstream services.
        await Promise.all(Array.from({length: Math.min(2, pending.length)}, async () => {
            while (pending.length) {
                const model = pending.shift();
                try { const result = await refreshProbe(model.id, kind); if (result) { completed++; if (result[kind + 'Status'] === 'Failed') errors++; } }
                catch { errors++; }
            }
        }));
        message(`${kind === 'juice' ? 'Juice' : '是/否检测'}全部刷新完成：已更新 ${completed} 个，错误 ${errors} 个`);
    } finally { button.disabled = false; button.removeAttribute('aria-busy'); }
}
$('refreshAllJuice').onclick = event => refreshAllProbes('juice', event.currentTarget);
$('refreshAllInstruction').onclick = event => refreshAllProbes('instruction', event.currentTarget);
const juicePicker = document.createElement('dialog');
juicePicker.id = 'juicePromptPicker';
juicePicker.setAttribute('aria-labelledby', 'juicePickerTitle');
juicePicker.innerHTML = `
<form id="juicePickerForm">
    <div class="juice-picker-header">
        <div><h2 id="juicePickerTitle">获取 Juice</h2><p id="juicePickerModel"></p></div>
        <button type="button" id="juicePickerClose" aria-label="关闭" title="关闭">×</button>
    </div>
    <div class="juice-picker-body">
        <div class="juice-picker-field"><label for="juiceMethod">检测方法</label><select id="juiceMethod" aria-describedby="juicePickerHint"></select><p id="juicePickerHint"></p></div>
        <div class="juice-picker-preview"><div id="juicePreviewLabel">检测策略</div><pre id="juicePromptPreview" aria-labelledby="juicePreviewLabel"></pre></div>
    </div>
    <div class="juice-picker-footer"><button type="button" id="juicePickerCancel">取消</button><button type="submit" class="primary">获取 Juice</button></div>
</form>`;
document.body.append(juicePicker);
let juicePickerModelId, juiceMethods = [];
const juiceMethodNames = {xml:'XML 算式', direct:'英文直问', 'direct-instant':'英文立即回答', 'direct-only':'英文仅数字', 'direct-spaced':'英文分隔拼写', arithmetic:'普通算式', chinese:'中文 Juice 询问', 'chinese-direct':'中文果汁直问', 'chinese-only':'中文仅数字'};
$('juicePickerCancel').onclick = $('juicePickerClose').onclick = () => juicePicker.close();
$('juiceMethod').onchange = () => {
    const method = juiceMethods.find(m => m.id === $('juiceMethod').value);
    $('juicePreviewLabel').textContent = method ? '请求文案 · Prompt' : '检测策略';
    $('juicePromptPreview').textContent = method?.prompt || '按历史正常率优先选择文案，遇到拒答或无效回复自动切换。';
    $('juicePickerHint').textContent = method ? '仅使用所选文案请求一次，结果计入该文案的正常率。' : '取得有效整数即停止。';
};
document.addEventListener('click', async event => {
    const button = event.target.closest('button[data-juice]');
    if (!button) return;
    const id = Number(button.dataset.juice);
    if (refreshingJuice.has(id)) return;
    try {
        if (!juiceMethods.length) juiceMethods = await api('/juice-methods');
        juicePickerModelId = id;
        const model = sites.flatMap(s => s.keys.flatMap(k => k.models)).find(m => m.id === id);
        $('juicePickerModel').textContent = model?.name || '';
        $('juiceMethod').innerHTML = '<option value="">自动择优（拒答时自动切换）</option>' + juiceMethods.map(m => `<option value="${esc(m.id)}">${esc(juiceMethodNames[m.id] || m.id)}</option>`).join('');
        $('juiceMethod').onchange();
        if (!juicePicker.open) juicePicker.showModal();
    } catch (error) { message(error.message); }
});
$('juicePickerForm').onsubmit = async event => {
    event.preventDefault();
    const id = juicePickerModelId;
    if (refreshingJuice.has(id)) return;
    const methodId = $('juiceMethod').value;
    juicePicker.close();
    refreshingJuice.add(id);
    render();
    try {
        const result = await api(`/models/${id}/juice${methodId ? '?methodId=' + encodeURIComponent(methodId) : ''}`, 'POST', {});
        message('Juice：' + juiceLabel(result));
        await refresh();
    } catch (error) { message(error.message); }
    finally { refreshingJuice.delete(id); render(); }
};
function recentModelHistory(modelId) {
    const recent = recentModelResults.filter(r => r.modelId === modelId).slice(0, 5);
    if (!recent.length) return '<span class="muted">暂无检测</span>';
    const dots = recent.map(r => {
        let winner;
        try { winner = JSON.parse(r.attributionJson || 'null')?.Candidates?.[0]; } catch {}
        const normalize = value => String(value || '').trim().toLowerCase();
        const type = r.status !== 'Success' ? 'failure' : !winner ? 'unknown' : normalize(winner.Model || winner.DisplayName) === normalize(r.modelName) ? 'success' : 'mismatch';
        const meaning = {success:'归因一致',mismatch:'归因不符',failure:'检测失败',unknown:'暂无归因结果'}[type];
        const info = `${r.modelName} · ${meaning}（${statuses[r.status] || r.status}）\n${time(r.startedAt)} · 批次 #${r.runId} · ${(r.latencyMs / 1000).toFixed(1)}s${winner ? `\n归因：${winner.DisplayName || winner.Model} · ${(winner.Probability * 100).toFixed(1)}%` : ''}\nJuice（模型自报）：${juiceLabel(r)}${r.juicePrompt ? `\nJuice Prompt：${r.juicePrompt}` : ''}\n${instructionTooltip(r)}${type === 'failure' ? `\n${failureReason(r)}` : ''}`;
        return `<span class="recent-dot ${type}" tabindex="0" data-tooltip="${esc(info)}" aria-label="${esc(info)}"></span>`;
    }).join('');
    return `<div class="recent-dots" aria-label="最近五次检测，从左到右由新到旧">${dots}</div><small class="recent-model-time" title="${esc(time(recent[0].startedAt))}">${relativeTime(recent[0].startedAt)}</small>`;
}
const statuses = {Success:'成功',Failed:'失败',Timeout:'超时',Running:'运行中',Queued:'排队中',Completed:'已完成',Cancelled:'已取消',Interrupted:'已中断',Pending:'待检测'};
const badge = s => `<span class="badge ${esc(s)}">${esc(statuses[s] || s)}</span>`;
const time = v => v ? new Date(v).toLocaleString('zh-CN') : '—';
function relativeTime(value) {
    if (!value) return time(value);
    const seconds = Math.max(0, Math.floor((Date.now() - new Date(value).getTime()) / 1000));
    if (seconds < 60) return `${seconds}秒前`;
    if (seconds < 3600) return `${Math.floor(seconds / 60)}分钟前`;
    if (seconds < 86400) return `${Math.floor(seconds / 3600)}小时前`;
    return time(value);
}
function failureReason(result) {
    const reasons = [];
    try { const responses = JSON.parse(result.responsesJson || '[]'); if (Array.isArray(responses)) for (const response of responses) if (typeof response?.Error === 'string' && response.Error.trim()) reasons.push(response.Error); } catch {}
    if (result.error) reasons.push(({Timeout:'请求超时',Cancelled:'检测已取消',Interrupted:'检测因服务中断而停止'})[result.error] || result.error);
    return [...new Set(reasons)].join('\n') || ({Timeout:'请求超时',Cancelled:'检测已取消',Interrupted:'检测因服务中断而停止'})[result.status] || (result.statusCode >= 400 ? `接口返回 HTTP ${result.statusCode}` : '未记录具体失败原因');
}
function reasonContent(reason, id) {
    return `<details class="failure-reason" data-reason-id="${esc(id)}"><summary>${esc(reason.split('\n')[0])}</summary><pre>${esc(reason)}</pre></details>`;
}
function runTargets(run) {
    const targets = run.targets || [];
    if (!targets.length) return '<p class="run-target-empty">未记录检测对象</p>';
    const rows = items => items.map(t => `<div class="run-target"><span class="run-target-model">${esc(t.modelName)}</span><span class="run-target-location">${esc(t.siteName)} <span>/</span> ${esc(t.keyName)}</span></div>`).join('');
    return `<div class="run-targets"><div class="run-target-heading">检测对象 <span>${targets.length} 个模型 · ${new Set(targets.map(t => t.siteName)).size} 个站点</span></div>${rows(targets.slice(0,3))}${targets.length>3?`<details class="run-target-more" data-reason-id="targets-${run.id}"><summary>查看其余 ${targets.length-3} 个模型</summary>${rows(targets.slice(3))}</details>`:''}</div>`;
}
function attributionLabel(winner, modelName) {
    if (!winner) return '—';
    const normalize = value => String(value || '').trim().toLowerCase();
    const mismatch = normalize(winner.Model || winner.DisplayName) !== normalize(modelName);
    const label = esc(winner.DisplayName || winner.Model || '—') + ` · ${(winner.Probability * 100).toFixed(1)}%`;
    return mismatch ? `<span class="attribution-mismatch" title="归因候选与配置模型不一致；归因概率不代表身份已验证" aria-label="${label}，与配置模型不一致">${label}</span>` : `<span class="attribution-match" title="归因候选与配置模型一致；归因概率不代表身份已验证" aria-label="${label}，与配置模型一致">${label}</span>`;
}
function modelStatus(modelId, latestResult) {
    let queued = false;
    for (const run of runs) {
        if (!['Running', 'Queued'].includes(run.status) || !run.targets?.some(t => t.modelId === modelId)) continue;
        if (run.completedModelIds?.includes(modelId) || results.some(r => r.runId === run.id && r.modelId === modelId)) continue;
        if (run.activeModelIds?.includes(modelId)) return 'Running';
        queued = true;
    }
    return queued ? 'Queued' : latestResult?.status || 'Pending';
}
function message(text){$('message').textContent=text;setTimeout(()=>{$('message').textContent='';},7000);}
async function api(path,method='GET',body){const response=await fetch('/api'+path,{method,headers:{'Content-Type':'application/json','X-CSRF-TOKEN':token},body:body===undefined?undefined:JSON.stringify(body)});if(!response.ok){if(response.status===401)showLogin();const error=await response.json().catch(()=>({}));throw Error(error.error||`请求失败 (${response.status})`);}return response.status===204?null:response.json();}
function showLogin(){ $('login').hidden=false;$('application').hidden=true;$('logout').hidden=true;$('sidebar').hidden=true; }
async function session(){const data=await api('/session');token=data.token;setupRequired=(await api('/setup')).required;$('login').querySelector('h1').textContent=setupRequired?'首次启动：创建管理员':'管理员登录';if(data.authenticated){$('login').hidden=true;$('application').hidden=false;$('logout').hidden=false;$('sidebar').hidden=false;await refresh();}else showLogin();}
$('loginForm').onsubmit=async e=>{e.preventDefault();try{const credentials=Object.fromEntries(new FormData(e.target));if(setupRequired)await api('/setup','POST',credentials);await api('/login','POST',credentials);await session();}catch(e){message(e.message);}};
$('logout').onclick=async()=>{await api('/logout','POST',{});await session();};
document.querySelectorAll('[data-tab]').forEach(b=>b.onclick=()=>{document.querySelectorAll('.view').forEach(v=>v.hidden=v.id!==b.dataset.tab);document.querySelectorAll('[data-tab]').forEach(x=>x.classList.toggle('selected',x===b));});
async function refresh(){[sites,results,runs,banks,recentModelResults]=await Promise.all(['/sites','/results','/runs','/banks','/results?perModel=true'].map(p=>api(p)));render();const settings=await api('/settings');if(!document.querySelector('#settingsForm:focus-within'))for(const name of ['challengeCount','intervalMinutes','maxConcurrency','timeoutSeconds'])$('settingsForm').elements[name].value=name==='timeoutSeconds'?240:settings[name];$('nextRun').textContent=`每次 ${settings.challengeCount} 个挑战 · 下次定时检测：${time(settings.nextRunAt)}`;}
function render(){const models=sites.flatMap(s=>s.keys.flatMap(k=>k.models.map(m=>({s,k,m,r:results.find(r=>r.modelId===m.id)}))));const latest=models.filter(x=>x.r);$('stats').innerHTML=[['站点',sites.length],['Key',sites.reduce((n,s)=>n+s.keys.length,0)],['模型',models.length],['最近成功',latest.filter(x=>x.r.status==='Success').length],['最近失败',latest.filter(x=>['Failed','Timeout'].includes(x.r.status)).length]].map(([k,v])=>`<div class="stat"><span>${k}</span><strong>${v}</strong></div>`).join('');
const query=$('search').value.toLowerCase();$('modelRows').innerHTML=models.filter(x=>[x.s.name,x.k.name,x.m.name].join(' ').toLowerCase().includes(query)).map(({s,k,m,r})=>{const winner=r?.attributionJson?JSON.parse(r.attributionJson).Candidates[0]:null;return `<tr><td>${esc(s.name)}</td><td>${esc(k.name)}</td><td>${esc(m.name)}</td><td>${badge(modelStatus(m.id,r))}</td><td>${r?(r.latencyMs/1000).toFixed(1)+'s':'—'}</td><td>${attributionLabel(winner,m.name)}</td><td>${juiceCell(m,r)}</td><td>${instructionCell(m,r)}</td><td class="recent-model-cell">${recentModelHistory(m.id)}</td><td><button data-detect-model="${m.id}">检测</button>${r?` <button data-detail="${r.id}">详情</button>`:''}</td></tr>`;}).join('')||'<tr><td colspan="10" class="empty">暂无模型</td></tr>';
$('runRows').innerHTML=runs.slice(0,12).map(r=>`<div class="run-entry"><div class="run-summary"><strong>#${r.id}</strong><span class="run-time" title="${esc(time(r.startedAt))}">${relativeTime(r.startedAt)}</span>${badge(r.status)}<span class="run-source">${r.source==='Manual'?'手动':'定时'}</span><span class="run-duration">${((new Date(r.completedAt||Date.now())-new Date(r.startedAt))/1000).toFixed(1)}s</span><div class="run-progress"><progress aria-label="任务 #${r.id} 检测进度" value="${r.completed}" max="${r.total||1}"></progress><span>${r.completed}/${r.total}</span></div><div class="run-actions">${['Queued','Running'].includes(r.status)?`<button class="icon-button" data-cancel="${r.id}" title="取消任务" aria-label="取消任务 #${r.id}"><i data-lucide="square"></i></button>`:''}</div></div><details class="run-details" data-reason-id="run-details-${r.id}"><summary><span>${r.total} 个检测 · ${new Set((r.targets||[]).map(t=>t.siteName)).size} 个站点</span>${r.failures?.length?`<span class="run-error-count">${r.failures.length} 项异常</span>`:''}</summary><div class="run-details-body">${runTargets(r)}${(r.failures||[]).map(f=>`<div class="run-failure"><span>${esc(f.siteName)} / ${esc(f.keyName)} / ${esc(f.modelName)}</span>${reasonContent(f.reason,`run-${r.id}-${f.id}`)}</div>`).join('')}${!(r.failures?.length)&&['Cancelled','Interrupted'].includes(r.status)?`<p class="run-failure">${r.status==='Cancelled'?'任务已取消，未完成的模型未产生检测结果':'服务中断，部分模型未完成检测'}</p>`:''}</div></details></div>`).join('')||'<p class="empty">暂无检测任务</p>';
$('siteRows').innerHTML=sites.map(s=>`<div class="row"><div><b>${esc(s.name)}</b><small>${esc(s.baseUrl)}</small></div><div class="actions"><button data-key="${s.id}">添加 Key</button><button data-detect-site="${s.id}">检测站点</button><button data-delete="sites/${s.id}">删除</button></div></div><div class="keys">${s.keys.map(k=>`<div class="row"><div><b>${esc(k.name)}</b><small>${esc(k.mask)}</small>${k.models.map(m=>`<div>${esc(m.name)} <button data-delete="models/${m.id}">删除模型</button></div>`).join('')}</div><div class="actions"><button data-model="${k.id}">添加模型</button><button data-detect-key="${k.id}">检测 Key</button><button data-delete="keys/${k.id}">删除 Key</button></div></div>`).join('')}</div>`).join('')||'<p class="empty">暂无站点</p>';
$('resultRows').innerHTML=results.map(r=>`<tr><td>${time(r.startedAt)}</td><td>#${r.runId}</td><td>${esc(r.siteName)} / ${esc(r.keyName)}</td><td>${esc(r.modelName)}</td><td>${badge(r.status)}</td><td>${r.statusCode||'—'}</td><td>${(r.latencyMs/1000).toFixed(1)}s</td><td class="history-reason">${['Failed','Timeout','Cancelled','Interrupted'].includes(r.status)?reasonContent(failureReason(r),`result-${r.id}`):'—'}</td><td><button data-detail="${r.id}">详情</button><button data-detect-model="${r.modelId}">重测</button></td></tr>`).join('')||'<tr><td colspan="9" class="empty">暂无记录</td></tr>';
$('bankRows').innerHTML=banks.map(b=>`<div class="row"><div><b>${esc(b.name)}</b> ${b.active?badge('Success'):''}<small>${time(b.importedAt)} · SHA256 ${esc(b.sha256)}</small></div><div class="actions"><a href="/api/banks/${b.id}/export">导出</a>${!b.active?`<button data-activate="${b.id}">设为当前库</button>`:''}</div></div>`).join('');}
$('search').oninput=render;
function edit(title,fields,action){$('editorTitle').textContent=title;$('editorFields').innerHTML=fields.map(([name,label,type='text'])=>`<label>${label}<input name="${name}" type="${type}" required ${type==='password'?'autocomplete="new-password"':''}></label>`).join('');editAction=action;$('editor').showModal();}
$('editorForm').onsubmit=async e=>{e.preventDefault();try{const values=Object.fromEntries(new FormData(e.target));await editAction(values);$('editor').close();await refresh();}catch(e){message(e.message);}};
$('closeEditor').onclick=()=>$('editor').close();$('closeDetail').onclick=()=>$('detail').close();
$('addSite').onclick=()=>edit('添加站点',[['name','名称'],['baseUrl','Base URL','url']],v=>api('/sites','POST',v));
$('importBank').onclick=()=>edit('导入指纹库',[['name','版本名称'],['file','ModelTrace JSON 文件','file']],async v=>{await api('/banks','POST',{name:v.name,json:await v.file.text()});});
$('settingsForm').onsubmit=async e=>{e.preventDefault();try{await api('/settings','PUT',Object.fromEntries([...new FormData(e.target)].map(([k,v])=>[k,Number(v)])));message('设置已保存');await refresh();}catch(e){message(e.message);}};
document.addEventListener('click',async e=>{const b=e.target.closest('button');if(!b)return;const d=b.dataset;if(!['key','model','delete','detect','detectModel','detectKey','detectSite','cancel','activate','detail'].some(k=>d[k]!==undefined))return;try{if(d.key)edit('添加 Key',[['name','Key 名称'],['value','API Key','password']],v=>api(`/sites/${d.key}/keys`,'POST',v));if(d.model)edit('添加模型',[['name','模型 ID']],v=>api(`/keys/${d.model}/models`,'POST',v));if(d.delete&&confirm('确认删除？此操作会删除下级配置，历史记录仍然保留。')){await api('/'+d.delete,'DELETE');await refresh();}if(d.detect||d.detectModel||d.detectKey||d.detectSite){b.disabled=true;await api('/detect','POST',{modelId:d.detectModel?Number(d.detectModel):null,keyId:d.detectKey?Number(d.detectKey):null,siteId:d.detectSite?Number(d.detectSite):null});message('检测任务已加入队列');await refresh();}if(d.cancel){await api(`/runs/${d.cancel}/cancel`,'POST',{});message('已请求取消');}if(d.activate){await api(`/banks/${d.activate}/activate`,'POST',{});await refresh();}if(d.detail){const r=results.find(x=>x.id===Number(d.detail));const report=r.attributionJson?JSON.parse(r.attributionJson):null;$('detailBody').innerHTML=`<p>${esc(r.siteName)} / ${esc(r.keyName)} / ${esc(r.modelName)} · ${badge(r.status)} · 总耗时 ${(r.latencyMs/1000).toFixed(1)}s</p><p>Juice（模型自报）：<span class="probe-${esc(r.juiceStatus || 'pending')}" title="${esc(juiceTooltip(r))}">${juiceLabel(r)}</span></p><p>是/否检测：${probeBadge(r.instructionStatus)}</p><details><summary>是/否检测题目与回复</summary><pre>${esc(instructionTooltip(r))}</pre></details>${r.error?`<p>${esc(r.error)}</p>`:''}${report?`<h2>候选模型</h2><p>有效回答 ${report.UsedOutputs} · 校准 β ${report.Beta.toFixed(2)}</p><table><thead><tr><th>模型</th><th>家族</th><th>概率</th></tr></thead><tbody>${report.Candidates.map(c=>`<tr><td>${esc(c.DisplayName)}</td><td>${esc(c.Family)}</td><td>${(c.Probability*100).toFixed(2)}%</td></tr>`).join('')}</tbody></table><h2>家族概率</h2>${Object.entries(report.Families).map(([k,v])=>`<p>${esc(k)}: ${(v*100).toFixed(2)}%</p>`).join('')}`:''}<h2>挑战与响应</h2>${JSON.parse(r.responsesJson).map(o=>`<details><summary>${o.ExpectedCount} 个整数 · ${((o.DurationMs||0)/1000).toFixed(1)}s</summary>${o.Error?`<p>${esc(o.Error)}</p>`:''}<p>${esc(o.Prompt)}</p><pre>${esc(o.Text)}</pre></details>`).join('')}`;$('detail').showModal();}}catch(e){message(e.message);}finally{b.disabled=false;}});
session().catch(e=>message(e.message));setInterval(()=>{if(!$('application').hidden)refresh().catch(e=>message(e.message));},5000);
