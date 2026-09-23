'use strict';
const fingerprintDialog = document.createElement('dialog');
fingerprintDialog.innerHTML = `<form id="fingerprintForm">
    <h2>采集模型指纹</h2>
    <p class="notice">请选择身份可信的模型作为参考。采集成功后保存为新库版本，同标识指纹在新版本中替换；保存后可设为当前库。新版本沿用基础库校准参数，概率仅供参考。</p>
    <label>来源模型<select name="source" required></select></label>
    <label>基础指纹库<select name="bank" required></select></label>
    <label>指纹模型标识<input name="model" maxlength="200" required></label>
    <label>显示名称<input name="displayName" maxlength="200" required></label>
    <label>家族（如 gpt、claude）<input name="family" maxlength="100" value="custom" required></label>
    <label>采集题数<select name="count"><option value="6">6</option><option value="3">3</option><option value="4">4</option><option value="5">5</option></select></label>
    <p id="fingerprintStatus" role="status" aria-live="polite"></p>
    <div class="actions"><button type="button" id="closeFingerprint">关闭</button><button type="submit" class="primary">采集并保存</button></div>
</form>`;
document.body.append(fingerprintDialog);
let fingerprintBusy = false;
const fingerprintForm = $('fingerprintForm');
const fingerprintFields = fingerprintForm.elements;
$('collectFingerprint').onclick = () => {
    fingerprintFields.source.innerHTML = sites.flatMap(s => s.keys.flatMap(k => k.models.map(m => `<option value="${m.id}" data-name="${esc(m.name)}">${esc(s.name)} / ${esc(k.name)} / ${esc(m.name)}</option>`))).join('');
    fingerprintFields.bank.innerHTML = banks.map(b => `<option value="${b.id}" ${b.active ? 'selected' : ''}>${esc(b.name)}</option>`).join('');
    fingerprintFields.source.onchange();
    $('fingerprintStatus').textContent = '';
    fingerprintDialog.showModal();
};
fingerprintFields.source.onchange = () => {
    const name = fingerprintFields.source.selectedOptions[0]?.dataset.name || '';
    fingerprintFields.model.value = name;
    fingerprintFields.displayName.value = name;
};
$('closeFingerprint').onclick = () => { if (!fingerprintBusy) fingerprintDialog.close(); };
fingerprintDialog.addEventListener('cancel', event => { if (fingerprintBusy) event.preventDefault(); });
fingerprintForm.onsubmit = async event => {
    event.preventDefault();
    if (fingerprintBusy) return;
    const source = Number(fingerprintFields.source.value);
    const input = { bankId: Number(fingerprintFields.bank.value), model: fingerprintFields.model.value.trim(), displayName: fingerprintFields.displayName.value.trim(), family: fingerprintFields.family.value.trim(), challengeCount: Number(fingerprintFields.count.value) };
    fingerprintBusy = true;
    for (const field of fingerprintFields) field.disabled = true;
    $('fingerprintStatus').textContent = '正在采集，请稍候。多道题可能需要数分钟。';
    try {
        const result = await api(`/models/${source}/fingerprint`, 'POST', input);
        $('fingerprintStatus').textContent = `已采集 ${result.collected} 份有效回答，保存为指纹库 #${result.id}。可关闭窗口并将其设为当前库。`;
        await refresh();
    } catch (error) { $('fingerprintStatus').textContent = error.message; }
    finally { fingerprintBusy = false; for (const field of fingerprintFields) field.disabled = false; }
};
