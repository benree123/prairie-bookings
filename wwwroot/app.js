const $ = id => document.getElementById(id);
let catalog, selectedSlot = null, availabilityVersion = 0, submitting = false;
const pages = ['home','services','team','about','booking','manage'];
const timeLabel = value => new Date(value).toLocaleTimeString('en-CA', {hour:'numeric',minute:'2-digit'});
const dateLabel = value => new Date(value + 'T12:00:00').toLocaleDateString('en-CA', {month:'short',day:'numeric',year:'numeric'});
async function api(path, options) {
  const response = await fetch(path, options);
  let data;
  try { data = await response.json(); } catch { throw new Error('The server is unavailable. Please try again.'); }
  if (!response.ok) throw new Error(data.error || (response.status === 429 ? 'Too many requests. Please wait a minute.' : 'Something went wrong. Please try again.'));
  return data;
}
function message(id, text, error = false) { $(id).textContent = text; $(id).classList.toggle('error', error); }
function panel(name, updateUrl = true) {
  [...pages,'confirmation'].forEach(value => $(value+'-panel').hidden = value !== name);
  const active = name === 'confirmation' ? 'booking' : name;
  document.querySelectorAll('[data-page]').forEach(link => {
    link.classList.toggle('active', link.dataset.page === active);
    if (link.dataset.page === active) link.setAttribute('aria-current','page');
    else link.removeAttribute('aria-current');
  });
  if (updateUrl && pages.includes(name) && location.hash !== '#'+name) history.pushState(null,'','#'+name);
  document.title = `${active === 'home' ? 'A little time. A clear next step.' : {services:'Our services',team:'Our team',about:'About us',booking:'Book an appointment',manage:'Manage your booking'}[active]} | Prairie Bookings`;
  window.scrollTo({top:0,behavior:'instant'});
}
function route() {
  const name = location.hash.slice(1);
  if (name === 'main') { $('main').setAttribute('tabindex','-1'); $('main').focus(); return; }
  panel(pages.includes(name) ? name : 'home',false);
}
window.addEventListener('hashchange',route);
window.addEventListener('popstate',route);
route();

function renderContent() {
  for (const target of ['home-services','all-services']) {
    for (const service of catalog.services) {
      const card = $('service-card-template').content.cloneNode(true);
      const photo = card.querySelector('img');
      photo.src = service.id === 1 ? '/images/consultation-pexels.jpg' : '/images/team-pexels.jpg';
      photo.alt = service.id === 1 ? 'Business partners in an office meeting' : 'Professionals discussing project plans at an office';
      photo.style.objectPosition = service.id === 3 ? '50% 35%' : '50% 50%';
      card.querySelector('.card-number').textContent = '0'+service.id+' / YOUR NEXT STEP';
      card.querySelector('h3').textContent = service.name;
      card.querySelector('p').textContent = service.description;
      card.querySelector('button').addEventListener('click', () => {
        $('service').value = String(service.id); summary(); panel('booking');
      });
      $(target).append(card);
    }
  }
  const bios = [
    'A friendly starting point for exploring your goals and finding a clear next step.',
    'A focused conversation partner for reviewing a project and shaping your direction.',
    'A thoughtful check-in for reflecting on progress and keeping your plans moving.'
  ];
  catalog.providers.forEach((provider,index) => {
    const card = document.createElement('article'); card.className = 'team-card';
    const avatar = document.createElement('div'); avatar.className = 'avatar'; avatar.textContent = provider.name.split(' ').map(x=>x[0]).join(''); avatar.setAttribute('aria-hidden','true');
    const name = document.createElement('h3'); name.textContent = provider.name;
    const role = document.createElement('span'); role.className = 'team-role'; role.textContent = provider.role;
    const bio = document.createElement('p'); bio.textContent = bios[index];
    const button = document.createElement('button'); button.type='button'; button.className='button secondary'; button.textContent='Book with '+provider.name.split(' ')[0]+' ↗';
    button.addEventListener('click',()=>{ $('provider').value=String(provider.id); panel('booking'); loadSlots(); });
    card.append(avatar,name,role,bio,button); $('team-list').append(card);
  });
}
function summary() {
  if (!catalog) return;
  const service = catalog.services.find(x => x.id === Number($('service').value));
  const provider = catalog.providers.find(x => x.id === Number($('provider').value));
  $('summary-service').textContent = service.name;
  $('service-description').textContent = service.description;
  $('summary-provider').textContent = provider.name;
  $('summary-date').textContent = $('date').value ? dateLabel($('date').value) : '—';
  $('summary-time').textContent = selectedSlot ? timeLabel(selectedSlot) + ' MT' : 'Select a time';
  $('submit-booking').disabled = !selectedSlot || submitting;
}
async function loadSlots() {
  const version = ++availabilityVersion;
  selectedSlot = null; summary(); $('slots').replaceChildren();
  message('availability-message', 'Checking availability…');
  try {
    const data = await api(`/api/availability?providerId=${$('provider').value}&date=${encodeURIComponent($('date').value)}`);
    if (version !== availabilityVersion) return;
    data.slots.forEach(slot => {
      const button = document.createElement('button');
      button.type = 'button'; button.className = 'slot'; button.textContent = timeLabel(slot.startsAt);
      button.disabled = !slot.available; button.setAttribute('aria-pressed', 'false');
      if (!slot.available) button.setAttribute('aria-label', `${timeLabel(slot.startsAt)}, unavailable`);
      button.addEventListener('click', () => {
        selectedSlot = slot.startsAt;
        document.querySelectorAll('.slot').forEach(x => { x.classList.remove('selected'); x.setAttribute('aria-pressed','false'); });
        button.classList.add('selected'); button.setAttribute('aria-pressed','true'); summary();
      });
      $('slots').append(button);
    });
    message('availability-message', data.slots.length ? 'Select an available time above. All times are Mountain Time.' : 'No slots on this date. Try another weekday.');
  } catch (error) { if (version === availabilityVersion) message('availability-message', error.message, true); }
}
$('service').addEventListener('change', summary);
['provider','date'].forEach(id => $(id).addEventListener('change', loadSlots));
$('booking-form').addEventListener('submit', async event => {
  event.preventDefault(); if (!selectedSlot || submitting) return;
  submitting = true;
  $('submit-booking').disabled = true; message('booking-message','Reserving your appointment…');
  // Snapshot the selection so changing controls during the request cannot alter confirmation details.
  const request = { providerId:Number($('provider').value), serviceId:Number($('service').value), startsAt:selectedSlot, customerName:$('name').value, email:$('email').value };
  try {
    const result = await api('/api/bookings', {method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(request)});
    $('reference').value = result.reference; $('cancel-reference').value = result.reference;
    const service = catalog.services.find(x => x.id === result.serviceId).name;
    const provider = catalog.providers.find(x => x.id === result.providerId).name;
    $('confirmation-details').textContent = `${service} with ${provider} · ${dateLabel(result.startsAt.slice(0,10))} at ${timeLabel(result.startsAt)} MT`;
    $('booking-form').reset(); message('booking-message',''); message('copy-message','');
    panel('confirmation'); $('confirmation-panel').focus(); await loadSlots();
  } catch (error) { message('booking-message',error.message,true); await loadSlots(); }
  finally { submitting = false; summary(); }
});
$('copy-reference').addEventListener('click', async () => {
  try { await navigator.clipboard.writeText($('reference').value); message('copy-message','Reference copied.'); }
  catch { $('reference').select(); message('copy-message','Select and copy the reference above.'); }
});
$('book-another').addEventListener('click', () => panel('booking'));
$('cancel-form').addEventListener('submit', async event => {
  event.preventDefault();
  const button = $('cancel-form').querySelector('button'); button.disabled = true;
  try {
    const result = await api('/api/bookings/cancel',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({reference:$('cancel-reference').value})});
    message('cancel-message',result.message); await loadSlots();
  } catch (error) { message('cancel-message',error.message,true); }
  finally { button.disabled = false; }
});
async function init() {
  try {
    catalog = await api('/api/catalog');
    [['service',catalog.services],['provider',catalog.providers]].forEach(([id,items]) => items.forEach(item => {
      const option = document.createElement('option'); option.value = item.id; option.textContent = item.name; $(id).append(option);
    }));
    $('date').min = catalog.today; $('date').max = catalog.lastDate;
    let day = new Date(catalog.today+'T12:00:00');
    // Begin with the next weekday; users can still select today when slots remain.
    do { day.setDate(day.getDate()+1); } while ([0,6].includes(day.getDay()));
    $('date').value = `${day.getFullYear()}-${String(day.getMonth()+1).padStart(2,'0')}-${String(day.getDate()).padStart(2,'0')}`;
    renderContent();
    await loadSlots();
  } catch (error) { message('availability-message',error.message,true); }
}
init();
