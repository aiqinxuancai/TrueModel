'use strict';

$('updateDefaultBank').onclick = async event => {
    const button = event.currentTarget;
    button.disabled = true;
    button.setAttribute('aria-busy', 'true');
    $('bankUpdateStatus').textContent = '正在从 ModelTrace GitHub 检查并下载最新指纹库…';
    try {
        const result = await api('/banks/update-default', 'POST', {});
        $('bankUpdateStatus').textContent = result.updated
            ? `默认库 #${result.id} 已更新（${result.commit.slice(0, 7)}）。`
            : '默认库已是最新版本。';
        await refresh();
    } catch (error) { $('bankUpdateStatus').textContent = error.message; }
    finally { button.disabled = false; button.removeAttribute('aria-busy'); }
};

const fingerprintForm = $('fingerprintForm');
const fingerprintFields = fingerprintForm.elements;
const stopRequests = new Set();

function showFingerprintSetup(open) {
    const section = $('fingerprintSetup');
    const restoreFocus = !open && section.contains(document.activeElement);
    section.hidden = !open;
    $('collectFingerprint').setAttribute('aria-expanded', String(open));
    if (restoreFocus) $('collectFingerprint').focus();
}

$('collectFingerprint').onclick = () => {
    if (!$('fingerprintSetup').hidden) {
        showFingerprintSetup(false);
        return;
    }
    const sources = sites.flatMap(s => s.keys.flatMap(k => k.models.map(m =>
        `<option value="${m.id}" data-name="${esc(m.name)}">${esc(s.name)} / ${esc(k.name)} / ${esc(m.name)}</option>`)));
    fingerprintFields.source.innerHTML = sources.join('') || '<option value="">请先在站点中添加模型</option>';
    fingerprintFields.bank.innerHTML = banks.map(b =>
        `<option value="${b.id}" ${b.isDefault ? 'selected' : ''}>${esc(b.name)}</option>`).join('');
    fingerprintFields.source.onchange();
    $('fingerprintStatus').textContent = '';
    showFingerprintSetup(true);
    fingerprintFields.source.focus();
};
fingerprintFields.source.onchange = () => {
    const name = fingerprintFields.source.selectedOptions[0]?.dataset.name || '';
    fingerprintFields.model.value = name;
    fingerprintFields.displayName.value = name;
};
$('closeFingerprint').onclick = () => showFingerprintSetup(false);
fingerprintForm.onsubmit = async event => {
    event.preventDefault();
    const button = $('submitFingerprint');
    if (button.disabled) return;
    const source = Number(fingerprintFields.source.value);
    const input = {
        bankId: Number(fingerprintFields.bank.value), model: fingerprintFields.model.value.trim(),
        displayName: fingerprintFields.displayName.value.trim(), family: fingerprintFields.family.value.trim(),
        challengeCount: Number(fingerprintFields.count.value)
    };
    button.disabled = true;
    $('fingerprintStatus').textContent = '正在提交采集任务…';
    try {
        const task = await api(`/models/${source}/fingerprint`, 'POST', input);
        showFingerprintSetup(false);
        message(`指纹采集任务 #${task.id} 已加入队列`);
        await refresh();
    } catch (error) {
        $('fingerprintStatus').textContent = error.message;
        if ($('fingerprintSetup').hidden || $('banks').hidden) message(error.message);
    } finally { button.disabled = false; }
};

$('importBank').onclick = () => edit('导入指纹库', [['name', '库名称'], ['file', 'ModelTrace JSON 文件', 'file']],
    async v => { await api('/banks', 'POST', { name: v.name, json: await v.file.text() }); });
$('createEmptyBank').onclick = () => edit('新建空指纹库', [['name', '指纹库名称']], async v => {
    await api('/banks/empty', 'POST', { name: v.name });
    return '空指纹库已创建，添加指纹后即可启用联合匹配';
});

const manualFingerprintDialog = document.createElement('dialog');
manualFingerprintDialog.setAttribute('aria-labelledby', 'manualFingerprintTitle');
manualFingerprintDialog.innerHTML = `<form id="manualFingerprintForm">
    <div class="heading"><h2 id="manualFingerprintTitle">手动新增指纹</h2></div>
    <div class="dialog-body">
        <p class="notice">填写模型信息，并粘贴 355 个非负整数频次的 JSON 数组。频次总数至少为 80。</p>
        <div class="form-grid">
            <label>模型标识<input name="model" required maxlength="200"></label>
            <label>显示名称<input name="displayName" required maxlength="200"></label>
        </div>
        <label>家族<input name="family" required maxlength="100" value="custom"></label>
        <label>counts（355 个整数）<textarea name="counts" rows="6" required placeholder="[0, 0, ...]"></textarea></label>
        <p id="manualFingerprintStatus" class="status-line" role="status"></p>
    </div>
    <div class="dialog-footer"><button type="button" id="closeManualFingerprint">关闭</button><button type="submit" class="primary">新增</button></div>
</form>`;
document.body.append(manualFingerprintDialog);
$('closeManualFingerprint').onclick = () => manualFingerprintDialog.close();
$('manualFingerprintForm').onsubmit = async event => {
    event.preventDefault();
    const form = event.currentTarget;
    const v = Object.fromEntries(new FormData(form));
    const status = $('manualFingerprintStatus');
    try {
        const counts = JSON.parse(v.counts);
        if (!Array.isArray(counts)) throw Error('counts 必须是 JSON 数组');
        await api(`/banks/${form.dataset.bankId}/models`, 'POST', { model: v.model, displayName: v.displayName, family: v.family, counts });
        manualFingerprintDialog.close();
        message('指纹已新增');
        await refresh();
    } catch (error) {
        if (manualFingerprintDialog.open) status.textContent = error.message;
        else message(error.message);
    }
};

const pendingFingerprint = task => task.status === 'Queued' || task.status === 'Running';

function fingerprintTaskRow(task) {
    const published = banks.some(b => b.id === task.resultBankId);
    const stop = stopRequests.has(task.id);
    return `<div class="row">
        <div class="row-main">
            <span class="row-icon"><i data-lucide="fingerprint"></i></span>
            <div>
                <div class="row-title"><b>采集指纹 · ${esc(task.displayName)}</b>${badge(task.status)}</div>
                <small>#${task.id} · ${esc(task.siteName)} / ${esc(task.keyName)} / ${esc(task.modelName)} · ${time(task.createdAt)}</small>
                ${task.error ? `<small class="inline-error">${esc(task.error)}</small>` : ''}
            </div>
        </div>
        <div class="actions">
            <div class="run-progress fingerprint-task-progress"><progress aria-label="采集任务 #${task.id} 进度" value="${task.collected}" max="${task.total}"></progress><span>${task.collected}/${task.total}</span></div>
            ${pendingFingerprint(task) ? `<button type="button" data-stop-fingerprint="${task.id}" ${stop ? 'disabled' : ''}><i data-lucide="square"></i>停止采集</button>` : ''}
            ${published ? `<button type="button" data-bank-action="detail" data-bank-id="${task.resultBankId}"><i data-lucide="eye"></i>查看指纹库</button>` : ''}
        </div>
    </div>`;
}

function renderFingerprints() {
    $('bankRows').innerHTML = banks.map(bank => `<div class="row ${bank.active ? 'is-active' : ''}">
        <div class="row-main">
            <span class="row-icon"><i data-lucide="database"></i></span>
            <div>
                <div class="row-title"><b>${esc(bank.name)}</b><span class="badge ${bank.active ? 'Success' : 'Pending'}">${bank.isDefault ? '默认库' : bank.active ? '已启用' : '未启用'}</span></div>
                <small>${time(bank.importedAt)} · SHA256 <code title="${esc(bank.sha256)}">${esc(String(bank.sha256 || '').slice(0, 16))}…</code></small>
            </div>
        </div>
        <div class="actions">
            <button type="button" data-bank-action="detail" data-bank-id="${bank.id}"><i data-lucide="eye"></i>查看</button>
            <a class="button" href="/api/banks/${bank.id}/export"><i data-lucide="download"></i>导出</a>
            ${bank.isDefault ? '' : `
                <button type="button" data-bank-action="manual" data-bank-id="${bank.id}"><i data-lucide="plus"></i>手动新增</button>
                <button type="button" data-bank-action="${bank.active ? 'deactivate' : 'activate'}" data-bank-id="${bank.id}"><i data-lucide="${bank.active ? 'circle-minus' : 'circle-check'}"></i>${bank.active ? '停用匹配' : '启用匹配'}</button>
                <button type="button" class="icon-button" data-bank-action="delete" data-bank-id="${bank.id}" aria-label="删除指纹库 ${esc(bank.name)}" title="删除"><i data-lucide="trash-2"></i></button>`}
        </div>
    </div>`).join('');
    $('fingerprintTaskRows').innerHTML = fingerprintTasks.map(fingerprintTaskRow).join('') || '<p class="empty">暂无指纹采集任务</p>';
    const pending = fingerprintTasks.filter(pendingFingerprint);
    $('overviewFingerprintTasks').innerHTML = pending.map(fingerprintTaskRow).join('');
    $('overviewFingerprintTasks').hidden = pending.length === 0;
}

async function showFingerprintBank(id) {
    const bank = await api(`/banks/${id}`);
    $('detailTitle').textContent = '指纹库详情';
    $('detailBody').innerHTML = `<div class="detail-head"><div>
        <div class="detail-model">${esc(bank.name)}</div>
        <div class="muted">版本：${esc(bank.builtAt || '—')} · ${bank.models.length} 个模型</div>
    </div></div>
    <p class="detail-hash">SHA256 <code>${esc(bank.sha256)}</code></p>
    <div class="table-wrap bordered"><table>
        <thead><tr><th>模型标识</th><th>名称</th><th>家族</th><th>有效数字</th></tr></thead>
        <tbody>${bank.models.map(m => `<tr><td><code>${esc(m.id)}</code></td><td>${esc(m.displayName)}</td><td><span class="pill">${esc(m.family)}</span></td><td>${m.validNumberCount}</td></tr>`).join('')}</tbody>
    </table></div>`;
    $('detail').showModal();
}

document.addEventListener('click', async event => {
    const button = event.target.closest('button[data-bank-action], button[data-stop-fingerprint]');
    if (!button) return;
    const { bankAction, bankId, stopFingerprint } = button.dataset;
    button.disabled = true;
    try {
        if (stopFingerprint) {
            const id = Number(stopFingerprint);
            if (stopRequests.has(id)) return;
            stopRequests.add(id);
            try {
                await api(`/fingerprint-tasks/${id}/cancel`, 'POST', {});
                message('指纹采集已停止');
                await refresh();
            } finally { stopRequests.delete(id); }
            return;
        }
        if (bankAction === 'detail') { await showFingerprintBank(bankId); return; }
        if (bankAction === 'manual') {
            $('manualFingerprintForm').dataset.bankId = bankId;
            $('manualFingerprintStatus').textContent = '';
            manualFingerprintDialog.showModal();
            return;
        }
        if (bankAction === 'delete') {
            if (!confirm('确认删除此指纹库？')) return;
            await api(`/banks/${bankId}`, 'DELETE');
        } else await api(`/banks/${bankId}/${bankAction}`, 'POST', {});
        await refresh();
    } catch (error) { message(error.message); }
    finally { button.disabled = false; }
});
