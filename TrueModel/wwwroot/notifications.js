'use strict';
async function refreshNotifications(){
  const settings=await api('/notifications');
  const form=$('notificationForm');
  if(!form.matches(':focus-within')){
    for(const name of ['webhookEnabled','pushDeerEnabled','onlyFailures'])form.elements[name].checked=settings[name];
    form.elements.pushDeerEndpoint.value=settings.pushDeerEndpoint;
  }
  $('notificationConfig').textContent=`Webhook：${settings.webhookConfigured?'已配置':'未配置'} · PushKey：${settings.pushKeyConfigured?'已配置':'未配置'}`;
  const deliveries=await api('/notifications/deliveries');
  $('deliveryRows').innerHTML=deliveries.map(d=>`<div class="row"><span>${time(d.createdAt)} · ${esc(d.channel)} · ${d.runId?'批次 #'+d.runId:'测试通知'}</span><span>${badge(d.success?'Success':'Failed')} ${esc(d.message)}</span></div>`).join('')||'<p>暂无发送记录</p>';
}
$('notificationForm').onsubmit=async e=>{
  e.preventDefault();const f=e.target.elements;
  try{
    await api('/notifications','PUT',{webhookEnabled:f.webhookEnabled.checked,webhookUrl:f.webhookUrl.value,clearWebhook:f.clearWebhook.checked,pushDeerEnabled:f.pushDeerEnabled.checked,pushDeerEndpoint:f.pushDeerEndpoint.value,pushKey:f.pushKey.value,clearPushKey:f.clearPushKey.checked,onlyFailures:f.onlyFailures.checked});
    f.webhookUrl.value='';f.pushKey.value='';f.clearWebhook.checked=false;f.clearPushKey.checked=false;
    message('通知设置已保存');await refreshNotifications();
  }catch(error){message(error.message);}
};
$('testNotification').onclick=async()=>{
  const button=$('testNotification');button.disabled=true;
  try{await api('/notifications/test','POST',{});await refreshNotifications();message('测试完成，请查看发送记录');}catch(error){message(error.message);}finally{button.disabled=false;}
};
document.querySelector('[data-tab="notifications"]').addEventListener('click',()=>refreshNotifications().catch(e=>message(e.message)));
setInterval(()=>{if(!$('notifications').hidden&&!$('application').hidden)refreshNotifications().catch(e=>message(e.message));},5000);
