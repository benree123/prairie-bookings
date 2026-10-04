const $ = id => document.getElementById(id);
let catalog, selectedSlot = null, pendingCancel = null, appointmentsVersion = 0, blocksVersion = 0;
const timeLabel = value => new Date(value).toLocaleTimeString('en-CA',{hour:'numeric',minute:'2-digit'});
const dateLabel = value => new Date(value+'T12:00:00').toLocaleDateString('en-CA',{month:'short',day:'numeric',year:'numeric'});
function message(id,text,error=false){$(id).textContent=text;$(id).classList.toggle('error',error);}
function showLogin(){ $('staff-dashboard').hidden=true; $('staff-login').hidden=false; $('staff-logout').hidden=true; $('password').value=''; $('appointment-rows').replaceChildren(); $('block-list').replaceChildren(); $('cancel-dialog').close(); }
async function api(path,body){
 const response=await fetch('/api/staff'+path,body===undefined?{}:{method:'POST',headers:{'Content-Type':'application/json','X-Staff-Request':'1'},body:JSON.stringify(body)});
 let data; try{data=await response.json();}catch{data={};}
 if(response.status===401){showLogin();throw new Error(data.error||'Please sign in to continue.');}
 if(!response.ok)throw new Error(data.error||(response.status===429?'Too many requests. Wait a minute and try again.':'Unable to complete this request.'));
 return data;
}
async function openDashboard(session){$('staff-login').hidden=true;$('staff-dashboard').hidden=false;$('staff-logout').hidden=false;$('staff-user').textContent='Signed in as '+session.username;await loadAppointments();}
async function loadAppointments(){
 const version=++appointmentsVersion;
 const params=new URLSearchParams({from:$('filter-from').value,to:$('filter-to').value,status:$('filter-status').value});
 if($('filter-provider').value)params.set('providerId',$('filter-provider').value);
 message('appointments-message','Loading appointments…');$('appointment-rows').replaceChildren();$('empty-appointments').hidden=true;
 try{
  const data=await api('/appointments?'+params);if(version!==appointmentsVersion)return;
  for(const key of ['total','upcoming','completed','cancelled'])$('stat-'+key).textContent=data.counts[key];
  $('appointment-count').textContent=`Showing ${data.appointments.length} of ${data.totalMatches} matches · maximum 200 per view`;
  for(const appointment of data.appointments){
   const row=document.createElement('tr');
   const client=document.createElement('td');const name=document.createElement('strong');name.textContent=appointment.customerName;const email=document.createElement('small');email.textContent=appointment.email;client.append(name,email);row.append(client);
   for(const text of [appointment.service,appointment.provider,dateLabel(appointment.startsAt.slice(0,10))+' · '+timeLabel(appointment.startsAt)]){const cell=document.createElement('td');cell.textContent=text;row.append(cell);}
   const status=document.createElement('td');const badge=document.createElement('span');badge.className='status-badge '+appointment.status;badge.textContent=appointment.status==='active'?'Upcoming':appointment.status==='completed'?'Completed':'Cancelled';status.append(badge);row.append(status);
   const action=document.createElement('td');if(appointment.status==='active'){const button=document.createElement('button');button.type='button';button.className='button secondary';button.textContent='Cancel';button.setAttribute('aria-label','Cancel appointment for '+appointment.customerName);button.addEventListener('click',()=>{pendingCancel=appointment.id;$('cancel-description').textContent=appointment.customerName+' · '+dateLabel(appointment.startsAt.slice(0,10))+' at '+timeLabel(appointment.startsAt)+' MT';message('dialog-message','');$('cancel-dialog').showModal();});action.append(button);}else action.textContent='—';row.append(action);$('appointment-rows').append(row);
  }
  $('empty-appointments').hidden=data.appointments.length!==0;message('appointments-message','');
 }catch(error){if(version===appointmentsVersion)message('appointments-message',error.message,true);}
}
async function loadBlocks(){
 const version=++blocksVersion;selectedSlot=null;$('create-block').disabled=true;$('staff-slots').replaceChildren();$('block-list').replaceChildren();message('blocks-message','Loading availability…');
 const params=new URLSearchParams({providerId:$('block-provider').value,date:$('block-date').value});
 try{
  const [blocked,response]=await Promise.all([api('/blocks?'+params),fetch('/api/availability?'+params)]);
  const available=await response.json();if(!response.ok)throw new Error(available.error||'Unable to load slots.');if(version!==blocksVersion)return;
  for(const slot of available.slots){const button=document.createElement('button');button.type='button';button.className='slot';button.textContent=timeLabel(slot.startsAt);button.disabled=!slot.available;button.setAttribute('aria-pressed','false');button.addEventListener('click',()=>{selectedSlot=slot.startsAt;document.querySelectorAll('#staff-slots .slot').forEach(x=>{x.classList.remove('selected');x.setAttribute('aria-pressed','false');});button.classList.add('selected');button.setAttribute('aria-pressed','true');$('create-block').disabled=false;});$('staff-slots').append(button);}
  for(const block of blocked.blocks){const item=document.createElement('div');item.className='block-item';const info=document.createElement('div');const time=document.createElement('strong');time.textContent=timeLabel(block.startsAt)+' MT';const reason=document.createElement('p');reason.textContent=block.reason;info.append(time,reason);const button=document.createElement('button');button.type='button';button.className='button secondary';button.textContent='Remove block';button.setAttribute('aria-label','Remove block at '+timeLabel(block.startsAt));button.addEventListener('click',async()=>{button.disabled=true;try{const result=await api('/blocks/'+block.id+'/release',{});await loadBlocks();message('blocks-message',result.message);}catch(error){message('blocks-message',error.message,true);button.disabled=false;}});item.append(info,button);$('block-list').append(item);}
  if(!blocked.blocks.length){const empty=document.createElement('p');empty.className='muted';empty.textContent='No blocked time for this advisor and date.';$('block-list').append(empty);}
  message('blocks-message',available.slots.length?'Select an available slot to block.':'No future business-hour slots on this date.');
 }catch(error){if(version===blocksVersion)message('blocks-message',error.message,true);}
}
$('login-form').addEventListener('submit',async event=>{event.preventDefault();const button=event.currentTarget.querySelector('button');button.disabled=true;message('login-message','Signing in…');try{const session=await api('/login',{username:$('username').value,password:$('password').value});$('password').value='';message('login-message','');await openDashboard(session);}catch(error){message('login-message',error.message,true);}finally{button.disabled=false;}});
$('staff-logout').addEventListener('click',async()=>{try{await api('/logout',{});showLogin();message('login-message','Signed out.');}catch(error){message('appointments-message',error.message,true);}});
$('filter-form').addEventListener('submit',event=>{event.preventDefault();loadAppointments();});
$('appointments-tab').addEventListener('click',()=>{showTab('appointments');loadAppointments();});
$('availability-tab').addEventListener('click',()=>{showTab('availability');loadBlocks();});
function showTab(name){for(const value of ['appointments','availability']){$(value+'-view').hidden=value!==name;$(value+'-tab').classList.toggle('active',value===name);$(value+'-tab').setAttribute('aria-pressed',String(value===name));}}
$('availability-filter').addEventListener('submit',event=>{event.preventDefault();loadBlocks();});
for(const id of ['block-provider','block-date'])$(id).addEventListener('change',loadBlocks);
$('block-form').addEventListener('submit',async event=>{event.preventDefault();if(!selectedSlot)return;const body={providerId:Number($('block-provider').value),startsAt:selectedSlot,reason:$('block-reason').value};$('create-block').disabled=true;try{const result=await api('/blocks',body);$('block-reason').value='';await loadBlocks();message('blocks-message',result.message);}catch(error){await loadBlocks();message('blocks-message',error.message,true);}});
$('keep-appointment').addEventListener('click',()=>{$('cancel-dialog').close();pendingCancel=null;});
$('confirm-cancel').addEventListener('click',async()=>{if(pendingCancel===null)return;const button=$('confirm-cancel');button.disabled=true;try{const result=await api('/appointments/'+pendingCancel+'/cancel',{});$('cancel-dialog').close();pendingCancel=null;await loadAppointments();message('appointments-message',result.message);}catch(error){message('dialog-message',error.message,true);}finally{button.disabled=false;}});
async function init(){try{const response=await fetch('/api/catalog');if(!response.ok)throw new Error('Unable to connect to the booking service.');catalog=await response.json();for(const id of ['filter-provider','block-provider'])for(const provider of catalog.providers){const option=document.createElement('option');option.value=provider.id;option.textContent=provider.name;$(id).append(option);}$('filter-from').value=catalog.today;$('filter-to').value=catalog.lastDate;$('block-date').min=catalog.today;$('block-date').max=catalog.lastDate;let date=new Date(catalog.today+'T12:00:00');do{date.setDate(date.getDate()+1);}while([0,6].includes(date.getDay()));$('block-date').value=date.getFullYear()+'-'+String(date.getMonth()+1).padStart(2,'0')+'-'+String(date.getDate()).padStart(2,'0');try{await openDashboard(await api('/session'));}catch{showLogin();}}catch(error){message('login-message',error.message,true);}}
init();
