const $ = (selector) => document.querySelector(selector);
const monthParts = Object.fromEntries(new Intl.DateTimeFormat('en-CA', { timeZone: 'Asia/Bangkok', year: 'numeric', month: '2-digit' }).formatToParts(new Date()).map((item) => [item.type, item.value]));
const currentBangkokMonth = `${monthParts.year}-${monthParts.month}`;
const state = { users: [], vehicles: [], bookings: [], bookingHistory: [], bookingConflicts: [], calendarBookings: [], blackouts: [], available: [], selectedVehicle: null, page: 'booking', viewMode: 'employee', requestKey: null, calendarMonth: currentBangkokMonth, calendarScale: 100, showBookingHistory: false, toastTimer: null, adminPollTimer: null, knownPendingIds: null, knownBookingStatuses: null, bookingStatusEmployeeId: null };
const servicePages = {
  leave: ['ยื่นวันลา', 'ระบุประเภทและช่วงวันที่ต้องการลา', [['ประเภทการลา', ['ลาป่วย', 'ลากิจ', 'ลาพักร้อน']], ['วันเริ่มต้น', 'date'], ['วันสิ้นสุด', 'date']]],
  time: ['ใบลงเวลา', 'เลือกวันที่เพื่อลองกรอกเวลาเข้าและออกงาน', [['วันทำงาน', 'date'], ['เวลาเข้า', 'time'], ['เวลาออก', 'time']]]
};

const statusText = { Pending: 'รออนุมัติ', Confirmed: 'ยืนยันแล้ว', Completed: 'ดำเนินการแล้ว', Rejected: 'ปฏิเสธ', Cancelled: 'ยกเลิก', Active: 'พร้อมใช้งาน', Retired: 'ปิดใช้งาน' };
const dateFormat = new Intl.DateTimeFormat('th-TH', { dateStyle: 'medium', timeStyle: 'short', timeZone: 'Asia/Bangkok' });
const timeFormat = new Intl.DateTimeFormat('th-TH', { hour: '2-digit', minute: '2-digit', hourCycle: 'h23', timeZone: 'Asia/Bangkok' });

function selectedUser() { return state.users.find((user) => user.employeeId === Number($('#demo-user').value)); }
function isAdminPage() { return state.page === 'booking' && state.viewMode === 'admin' && selectedUser()?.isFleetAdmin === true; }
function escapeText(value) { return String(value ?? ''); }
function bangkokIso(selector) {
  const value = $(selector).value;
  return value ? `${value.length === 16 ? `${value}:00` : value}+07:00` : '';
}
function formatDate(value) { return value ? dateFormat.format(new Date(value)) : '—'; }
function formatRange(start, end) { return `${formatDate(start)} – ${formatDate(end)}`; }
function toBangkokInput(value) {
  const parts = new Intl.DateTimeFormat('en-CA', { timeZone: 'Asia/Bangkok', year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }).formatToParts(new Date(value));
  const part = Object.fromEntries(parts.map((item) => [item.type, item.value]));
  return `${part.year}-${part.month}-${part.day}T${part.hour}:${part.minute}`;
}
function statusClass(status) { return `status status-${String(status).toLowerCase()}`; }
function toast(message, isError = false) {
  const element = $('#toast');
  element.textContent = message;
  element.className = `toast show${isError ? ' error' : ''}`;
  clearTimeout(state.toastTimer);
  state.toastTimer = setTimeout(() => element.classList.remove('show'), 4200);
}
async function api(path, options = {}) {
  const response = await fetch(`/api/booking${path}`, {
    ...options,
    headers: { 'Content-Type': 'application/json', ...(options.headers || {}) }
  });
  const data = response.status === 204 ? null : await response.json().catch(() => null);
  if (!response.ok) {
    const message = response.status === 404
      ? 'API ทดลองจองรถเปิดเฉพาะ Development กรุณาเริ่มระบบในโหมด Development'
      : data?.message || `บันทึกไม่สำเร็จ (${response.status})`;
    const error = new Error(message);
    error.code = data?.code;
    error.status = response.status;
    error.data = data;
    throw error;
  }
  return data;
}

async function apiWithAffectedBookingConfirmation(path, options, actionLabel) {
  const body = JSON.parse(options.body || '{}');
  for (let attempt = 0; attempt < 2; attempt++) {
    try { return await api(path, { ...options, body: JSON.stringify(body) }); }
    catch (error) {
      const affected = error.data?.affectedBookings || [];
      if (error.code === 'BOOKING_ALREADY_STARTED') {
        await showAffectedBookingsPopup(actionLabel, affected, { canConfirm: false, notice: error.message });
        return null;
      }
      if (error.code !== 'AFFECTED_BOOKINGS_CONFIRMATION_REQUIRED') throw error;
      const confirmed = await showAffectedBookingsPopup(actionLabel, affected);
      if (!confirmed) return null;
      body.confirmAffectedBookings = true;
      body.affectedBookingIds = affected.map((item) => item.bookingId);
    }
  }
  throw new Error('รายการจองเปลี่ยนแปลงระหว่างยืนยัน กรุณาตรวจสอบและลองใหม่');
}

function showAffectedBookingsPopup(actionLabel, affected, { canConfirm = true, notice = '' } = {}) {
  return new Promise((resolve) => {
    const dialog = document.createElement('dialog'); dialog.className = 'affected-bookings-dialog';
    dialog.setAttribute('aria-labelledby', 'affected-bookings-title');
    const panel = document.createElement('section'); panel.className = 'card affected-bookings-panel';
    const header = document.createElement('div'); header.className = 'section-heading compact affected-bookings-header';
    const headingGroup = document.createElement('div');
    const title = document.createElement('h2'); title.id = 'affected-bookings-title'; title.textContent = 'ตรวจสอบรายการจองที่ได้รับผลกระทบ';
    const subtitle = document.createElement('p'); subtitle.textContent = `${actionLabel} · ${affected.length} รายการ`;
    headingGroup.append(title, subtitle);
    const close = document.createElement('button'); close.type = 'button'; close.className = 'mini-button'; close.textContent = 'ปิด';
    header.append(headingGroup, close); panel.append(header);

    const explanation = document.createElement('p');
    explanation.className = canConfirm ? 'affected-bookings-explanation' : 'affected-bookings-blocked';
    explanation.textContent = notice || (affected.length
      ? `หากยืนยัน ระบบจะยกเลิกรายการด้านล่าง บันทึกประวัติการยกเลิก และแจ้งผู้จอง`
      : 'รายการที่ได้รับผลกระทบเปลี่ยนแปลงแล้ว กรุณายืนยันอีกครั้งเพื่อดำเนินการต่อ');
    panel.append(explanation);

    if (affected.length) {
      const scroll = document.createElement('div'); scroll.className = 'affected-bookings-table-scroll';
      const table = document.createElement('table'); table.className = 'affected-bookings-table';
      const thead = document.createElement('thead'); const headerRow = document.createElement('tr');
      for (const text of ['เลขที่จอง', 'สถานะ', 'รถ', 'ผู้จอง', 'เวลาเดินทาง']) {
        const cell = document.createElement('th'); cell.scope = 'col'; cell.textContent = text; headerRow.append(cell);
      }
      thead.append(headerRow); const tbody = document.createElement('tbody');
      for (const item of affected) {
        const row = document.createElement('tr');
        const values = [item.bookingNo, statusText[item.status] || item.status, `${item.plateNo} · ${item.vehicleName}`, item.employeeName, formatRange(item.startAt, item.endAt)];
        for (const value of values) { const cell = document.createElement('td'); cell.textContent = value || '—'; row.append(cell); }
        tbody.append(row);
      }
      table.append(thead, tbody); scroll.append(table); panel.append(scroll);
    } else {
      const empty = document.createElement('div'); empty.className = 'affected-bookings-empty'; empty.textContent = 'ไม่มีรายการที่ต้องยกเลิกในขณะนี้'; panel.append(empty);
    }

    const actions = document.createElement('div'); actions.className = 'affected-bookings-actions';
    const cancel = document.createElement('button'); cancel.type = 'button'; cancel.className = 'button secondary'; cancel.textContent = canConfirm ? 'กลับไปแก้ไข' : 'ปิด';
    actions.append(cancel);
    if (canConfirm) {
      const confirmButton = document.createElement('button'); confirmButton.type = 'button'; confirmButton.className = 'button primary';
      confirmButton.textContent = affected.length ? `${actionLabel}และยกเลิก ${affected.length} รายการ` : 'ยืนยันดำเนินการ';
      confirmButton.addEventListener('click', () => dialog.close('confirm')); actions.append(confirmButton);
    }
    panel.append(actions); dialog.append(panel); document.body.append(dialog);
    const finish = () => { const confirmed = dialog.returnValue === 'confirm'; dialog.remove(); resolve(confirmed); };
    dialog.addEventListener('close', finish, { once: true });
    dialog.addEventListener('cancel', (event) => { event.preventDefault(); dialog.close('cancel'); });
    close.addEventListener('click', () => dialog.close('cancel'));
    cancel.addEventListener('click', () => dialog.close('cancel'));
    dialog.showModal();
  });
}
function setStep(step) {
  $('#step-pill').textContent = `ขั้นที่ ${step} จาก 3`;
  document.querySelectorAll('.steps li').forEach((item, index) => {
    item.classList.toggle('current', index + 1 === step);
    item.classList.toggle('done', index + 1 < step);
  });
}
function clearBookingFlow() {
  state.available = [];
  state.selectedVehicle = null;
  state.requestKey = null;
  $('#availability').replaceChildren();
  $('#details-form').classList.add('hidden');
  $('#review').classList.add('hidden');
  $('#booking-result').classList.add('hidden');
  setStep(1);
}

async function initialize() {
  try {
    state.users = await api('/demo-users');
    if (!state.users.length) throw new Error('ยังไม่มีพนักงานตัวอย่างในฐานข้อมูล กรุณาเพิ่มข้อมูลพนักงานก่อน');
    $('#demo-user').replaceChildren(...state.users.map((user) => new Option(`${user.name} · ${user.employeeCode}`, user.employeeId)));
    render();
    syncAdminPolling();
    await refreshData();
  } catch (error) {
    render();
    toast(error.message, true);
    $('#availability').innerHTML = '<div class="empty-state"><strong>ยังเชื่อมต่อข้อมูลไม่ได้</strong><span>ตรวจการตั้งค่า SQL Server และติดตั้งตารางจาก database/dev/vehicle-booking.sql</span></div>';
  }
}

function render() {
  let page = location.hash.slice(1);
  if (!Object.hasOwn(servicePages, page) && page !== 'booking') page = 'booking';
  state.page = page;
  const isFleetAdmin = selectedUser()?.isFleetAdmin === true;
  if (!isFleetAdmin) state.viewMode = 'employee';
  document.querySelectorAll('nav a').forEach((link) => link.setAttribute('aria-current', link.hash === `#${page}` ? 'page' : 'false'));
  $('#legacy-panel').classList.toggle('hidden', page === 'booking');
  $('#booking-workflow').classList.toggle('hidden', page !== 'booking');
  $('#fleet-view-selector').classList.toggle('hidden', page !== 'booking' || !isFleetAdmin);
  $('#booking-view-mode').value = state.viewMode;
  $('#employee-booking-view').classList.toggle('hidden', page !== 'booking' || (isFleetAdmin && state.viewMode === 'admin'));
  $('#admin-view').classList.toggle('hidden', page !== 'booking' || !isFleetAdmin || state.viewMode !== 'admin');
  if (page === 'booking') return;

  const [title, description, fields] = servicePages[page];
  const examples = {
    leave: 'ลาพักร้อน · 1 วัน · รอหัวหน้าอนุมัติ',
    time: 'เวลาเข้า 08.45 น. · ขาดเวลาออก · รอพนักงานกรอก'
  };
  $('#heading').textContent = title;
  $('#description').textContent = description;
  $('#example').textContent = `รายการตัวอย่าง — ${examples[page]}`;
  $('#legacy-note').textContent = 'หน้านี้เป็นโครงตัวอย่างสำหรับทดลองกรอก ข้อมูลยังไม่ถูกบันทึก';
  $('#fields').replaceChildren(...fields.map(([name, type]) => {
    const label = document.createElement('label'); label.textContent = name;
    const input = document.createElement(Array.isArray(type) ? 'select' : 'input');
    if (Array.isArray(type)) type.forEach((value) => input.add(new Option(value)));
    else input.type = type;
    label.append(input); return label;
  }));
}

async function refreshData() {
  const user = selectedUser();
  const adminView = isAdminPage();
  const query = `?actorId=${encodeURIComponent(user?.employeeId ?? '')}`;
  const data = await api(`/snapshot${query}`);
  state.vehicles = data.vehicles || [];
  renderCalendarVehicleFilter();
  if (!adminView && state.bookingStatusEmployeeId === user?.employeeId && state.knownBookingStatuses) {
    const previous = state.knownBookingStatuses;
    for (const row of data.bookings || []) {
      const oldStatus = previous.get(row.BookingId);
      if (oldStatus && oldStatus !== row.Status && ['Cancelled', 'Rejected'].includes(row.Status))
        toast(`รายการจอง ${row.BookingNo} ถูก${statusText[row.Status] || row.Status} · ${row.StatusReason || ''}`, true);
    }
  }
  state.bookings = data.bookings || [];
  state.bookingHistory = data.bookingHistory || [];
  state.bookingConflicts = adminView ? (data.bookingConflicts || []) : [];
  if (adminView) { state.knownBookingStatuses = null; state.bookingStatusEmployeeId = null; }
  else {
    state.knownBookingStatuses = new Map(state.bookings.filter((row) => row.EmployeeId === user?.employeeId).map((row) => [row.BookingId, row.Status]));
    state.bookingStatusEmployeeId = user?.employeeId ?? null;
  }
  // Employee calendar needs availability only; booking details stay in the
  // owner's list and the FleetAdmin view.
  state.calendarBookings = adminView ? state.bookings : (data.calendarBookings || []);
  state.blackouts = data.blackouts || [];
  if (adminView) {
    const pendingIds = new Set(state.bookings.filter((item) => item.Status === 'Pending').map((item) => item.BookingId));
    if (state.knownPendingIds) {
      const added = [...pendingIds].filter((id) => !state.knownPendingIds.has(id));
      if (added.length) toast(`มีคำขอจองรถใหม่ ${added.length} รายการ`);
    }
    state.knownPendingIds = pendingIds;
  } else state.knownPendingIds = null;
  renderMine();
  renderAdmin();
  renderVehicleCalendar();
}

function syncAdminPolling() {
  clearInterval(state.adminPollTimer);
  state.adminPollTimer = null;
  state.knownPendingIds = null;
  if (state.page !== 'booking') return;
  const adminView = isAdminPage();
  state.adminPollTimer = setInterval(() => {
    if (document.visibilityState === 'visible') refreshData().catch((error) => toast(error.message, true));
  }, adminView ? 20000 : 60000);
}

function renderMine() {
  const host = $('#my-bookings');
  const user = selectedUser();
  const rows = state.bookings.filter((row) => row.EmployeeId === user?.employeeId).sort((a, b) => new Date(b.StartAt) - new Date(a.StartAt));
  if (!rows.length) {
    host.innerHTML = '<div class="empty-state"><strong>ยังไม่มีรายการจอง</strong><span>รายการที่ส่งคำขอแล้วจะแสดงที่นี่</span></div>';
    $('#my-booking-history').replaceChildren(); $('#booking-history-section').classList.add('hidden');
    return;
  }
  const cards = rows.map((row) => {
    const article = document.createElement('article'); article.className = 'booking-row';
    const main = document.createElement('div'); main.className = 'booking-row-main';
    const title = document.createElement('strong'); title.textContent = `${row.PlateNo} · ${row.VehicleName}`;
    const bookingNo = document.createElement('small'); bookingNo.textContent = row.BookingNo;
    main.append(title, bookingNo);
    const meta = document.createElement('div'); meta.className = 'booking-row-details';
    const trip = document.createElement('span'); trip.textContent = `${formatRange(row.StartAt, row.EndAt)} · ${row.Destination}`;
    const purpose = document.createElement('small'); purpose.textContent = `วัตถุประสงค์: ${row.Purpose}`;
    meta.append(trip, purpose);
    const end = document.createElement('div'); end.className = 'booking-row-end';
    const completed = row.Status === 'Confirmed' && new Date(row.EndAt) <= new Date();
    const displayStatus = completed ? 'Completed' : row.Status;
    const status = document.createElement('span'); status.className = statusClass(displayStatus); status.textContent = statusText[displayStatus] || displayStatus;
    end.append(status);
    if (row.Status === 'Pending' && row.ExpiresAt) {
      const expiry = document.createElement('small'); expiry.className = 'booking-expiry'; expiry.textContent = `หมดเวลารออนุมัติ ${formatDate(row.ExpiresAt)}`; end.append(expiry);
    }
    if ((row.Status === 'Pending' || row.Status === 'Confirmed') && new Date(row.StartAt) > new Date()) {
      const cancel = document.createElement('button'); cancel.className = 'text-button danger-text'; cancel.textContent = 'ยกเลิก';
      cancel.addEventListener('click', () => changeBookingStatus(row, 'Cancelled'));
      end.append(cancel);
    }
    article.append(main, meta, end);
    if (['Cancelled', 'Rejected'].includes(row.Status) && row.StatusReason) {
      const notice = document.createElement('p'); notice.className = 'booking-cancellation-notice';
      notice.setAttribute('role', 'status');
      notice.textContent = `${row.Status === 'Rejected' ? 'เหตุผลที่ปฏิเสธ' : 'เหตุผลที่ยกเลิก'}: ${row.StatusReason}`;
      if (row.Status === 'Cancelled' && row.StatusReason.includes('ได้รับอนุมัติ')) {
        const retry = document.createElement('button'); retry.type = 'button'; retry.className = 'text-button booking-find-alternative';
        retry.textContent = 'ค้นหารถคันอื่นในเวลาเดิม';
        retry.addEventListener('click', () => {
          $('#start-at').value = toBangkokInput(row.StartAt); $('#end-at').value = toBangkokInput(row.EndAt);
          $('#search-form').requestSubmit(); $('#booking-workflow').scrollIntoView({ behavior: 'smooth', block: 'start' });
        });
        notice.append(document.createElement('br'), retry);
      }
      article.append(notice);
    }
    return article;
  });
  const activeCards = [];
  const historyCards = [];
  cards.forEach((card, index) => {
    const row = rows[index];
    const completed = row.Status === 'Confirmed' && new Date(row.EndAt) <= new Date();
    const archived = completed || ['Rejected', 'Cancelled'].includes(row.Status);
    if (!archived) { activeCards.push(card); return; }
    historyCards.push(card);
    if (completed) {
      const badge = card.querySelector('.status');
      badge.className = statusClass('Completed'); badge.textContent = statusText.Completed;
      const ended = document.createElement('small'); ended.className = 'booking-history-date'; ended.textContent = `สิ้นสุดการเดินทาง ${formatDate(row.EndAt)}`;
      card.querySelector('.booking-row-end').append(ended);
    }
    if (state.showBookingHistory) appendBookingAudit(card, row);
  });
  const visibleCards = state.showBookingHistory ? historyCards : activeCards;
  host.replaceChildren(...visibleCards);
  $('#booking-history-section').classList.add('hidden');
  if (!visibleCards.length) {
    const empty = document.createElement('div'); empty.className = 'empty-state';
    const title = document.createElement('strong'); title.textContent = state.showBookingHistory ? 'ยังไม่มีประวัติการจอง' : 'ไม่มีรายการจองที่กำลังดำเนินการ';
    const detail = document.createElement('span'); detail.textContent = state.showBookingHistory ? 'รายการที่เสร็จสิ้น ปฏิเสธ หรือยกเลิกจะแสดงที่นี่' : 'กดปุ่มประวัติเพื่อดูรายการที่เสร็จสิ้น ปฏิเสธ หรือยกเลิก';
    empty.append(title, detail); host.replaceChildren(empty);
  }
}

function appendBookingAudit(card, booking) {
  const history = state.bookingHistory
    .filter((item) => Number(item.BookingId) === Number(booking.BookingId))
    .sort((a, b) => new Date(a.ChangedAt) - new Date(b.ChangedAt));
  if (!history.length) return;
  const panel = document.createElement('div'); panel.className = 'booking-audit-panel';
  const list = document.createElement('ol');
  for (const item of history) {
    const entry = document.createElement('li');
    const event = document.createElement('strong'); event.textContent = statusText[item.Status] || item.Status;
    const actor = document.createElement('span'); actor.textContent = `${formatDate(item.ChangedAt)} · ${item.ActorName || 'ระบบ'} · ${item.Reason || '—'}`;
    entry.append(event, actor); list.append(entry);
  }
  panel.append(list); card.append(panel);
}

async function searchAvailability(event) {
  event.preventDefault();
  $('#availability').replaceChildren();
  $('#details-form').classList.add('hidden');
  $('#review').classList.add('hidden');
  $('#booking-result').classList.add('hidden');
  state.selectedVehicle = null;
  const startAt = bangkokIso('#start-at'); const endAt = bangkokIso('#end-at');
  if (!startAt || !endAt || new Date(startAt) <= new Date() || new Date(startAt) >= new Date(endAt)) {
    toast('เวลาออกเดินทางต้องอยู่ในอนาคตและก่อนเวลาคืนรถ', true); return;
  }
  const host = $('#availability'); host.innerHTML = '<div class="loading"><span></span>กำลังค้นหารถว่าง…</div>';
  try {
    state.available = await api(`/availability?startAt=${encodeURIComponent(startAt)}&endAt=${encodeURIComponent(endAt)}`);
    if (!state.available.length) {
      host.innerHTML = '<div class="empty-state"><strong>ไม่พบรถว่างในช่วงนี้</strong><span>ลองเปลี่ยนเวลา หรือเลือกช่วงที่ไม่ติดตารางซ่อมบำรุง</span></div>';
      return;
    }
    const title = document.createElement('div'); title.className = 'availability-heading';
    title.innerHTML = `<div><strong>รถที่ว่าง ${state.available.length} คัน</strong><span>${formatRange(startAt, endAt)}</span></div>`;
    const grid = document.createElement('div'); grid.className = 'vehicle-options';
    for (const vehicle of state.available) {
      const label = document.createElement('label'); label.className = 'vehicle-option';
      const radio = document.createElement('input'); radio.type = 'radio'; radio.name = 'available-vehicle'; radio.value = vehicle.VehicleId;
      const visual = document.createElement('span'); visual.className = 'vehicle-option-body';
      const icon = document.createElement('span'); icon.className = 'vehicle-icon'; icon.textContent = '↗';
      const info = document.createElement('span'); info.className = 'vehicle-info';
      const name = document.createElement('strong'); name.textContent = vehicle.Name;
      const plate = document.createElement('small'); plate.textContent = `${vehicle.PlateNo} · ${vehicle.Seats} ที่นั่ง`;
      info.append(name, plate);
      visual.append(icon, info); label.append(radio, visual); grid.append(label);
      radio.addEventListener('change', () => selectVehicle(vehicle));
    }
    host.replaceChildren(title, grid);
  } catch (error) { host.innerHTML = `<div class="empty-state error-state">${escapeText(error.message)}</div>`; }
}

function selectVehicle(vehicle) {
  state.selectedVehicle = vehicle;
  $('#details-form').classList.remove('hidden');
  $('#details-form').scrollIntoView({ behavior: 'smooth', block: 'nearest' });
}

function buildReview() {
  const host = $('#review'); host.replaceChildren(); host.classList.remove('hidden');
  const heading = document.createElement('div'); heading.className = 'review-head';
  const title = document.createElement('strong'); title.textContent = 'ตรวจสอบข้อมูลก่อนส่งคำขอ';
  const note = document.createElement('span'); note.textContent = 'การจองจะสำเร็จเมื่อผู้ดูแลอนุมัติแล้ว'; heading.append(title, note);
  const list = document.createElement('dl'); list.className = 'summary-list';
  const values = [
    ['ผู้จอง', selectedUser()?.name || 'พนักงานตัวอย่าง'],
    ['รถ', `${state.selectedVehicle.PlateNo} · ${state.selectedVehicle.Name}`],
    ['ออกเดินทาง', formatDate(bangkokIso('#start-at'))], ['คืนรถ', formatDate(bangkokIso('#end-at'))],
    ['ปลายทาง', $('#destination').value.trim()], ['วัตถุประสงค์', $('#purpose').value.trim()]
  ];
  for (const [key, value] of values) { const dt = document.createElement('dt'); dt.textContent = key; const dd = document.createElement('dd'); dd.textContent = value; list.append(dt, dd); }
  const actions = document.createElement('div'); actions.className = 'form-actions';
  const edit = document.createElement('button'); edit.type = 'button'; edit.className = 'button secondary'; edit.textContent = 'กลับไปแก้ไข';
  edit.addEventListener('click', () => { host.classList.add('hidden'); $('#details-form').classList.remove('hidden'); setStep(2); });
  const submit = document.createElement('button'); submit.type = 'button'; submit.className = 'button primary'; submit.textContent = 'ยืนยันส่งคำขอ';
  submit.addEventListener('click', submitBooking);
  actions.append(edit, submit); host.append(heading, list, actions); $('#details-form').classList.add('hidden'); setStep(3);
}

async function submitBooking(event) {
  const button = event.currentTarget; button.disabled = true; button.textContent = 'กำลังบันทึก…';
  state.requestKey ||= crypto.randomUUID();
  try {
    const result = await api('', { method: 'POST', body: JSON.stringify({
      employeeId: selectedUser().employeeId, vehicleId: state.selectedVehicle.VehicleId,
      startAt: bangkokIso('#start-at'), endAt: bangkokIso('#end-at'),
      destination: $('#destination').value.trim(), purpose: $('#purpose').value.trim(), requestKey: state.requestKey
    }) });
    const host = $('#booking-result'); host.classList.remove('hidden'); host.replaceChildren();
    const mark = document.createElement('span'); mark.className = 'result-mark'; mark.textContent = '✓';
    const heading = document.createElement('strong'); heading.textContent = result.duplicate ? 'พบรายการจองเดิมของคำขอนี้' : 'ส่งคำขอจองแล้ว';
    const message = document.createElement('span'); message.textContent = `เลขที่จอง ${result.bookingNo} · สถานะ ${statusText[result.status] || result.status}`;
    const detail = document.createElement('small'); detail.textContent = result.message || (result.duplicate ? 'ระบบคืนผลของรายการเดิมโดยไม่สร้างรายการซ้ำ' : 'รอผู้ดูแลรถพิจารณา'); host.append(mark, heading, message, detail);
    $('#review').classList.add('hidden'); $('#details-form').classList.add('hidden'); $('#availability').replaceChildren(); $('#search-form').reset(); $('#destination').value = ''; $('#purpose').value = '';
    state.requestKey = null; state.selectedVehicle = null; setStep(1); await refreshData(); toast('บันทึกคำขอเรียบร้อย');
  } catch (error) {
    button.disabled = false; button.textContent = 'ยืนยันส่งคำขอ';
    if (error.code === 'BOOKED' || error.code === 'BLACKOUT' || error.code === 'VEHICLE_INACTIVE') {
      state.requestKey = null;
      const host = $('#booking-result'); host.classList.remove('hidden'); host.replaceChildren();
      const message = document.createElement('span'); message.textContent = 'รถคันนี้ไม่ว่างแล้วในช่วงเวลาที่เลือก กรุณาตรวจรถว่างอีกครั้งหรือเปลี่ยนเวลา';
      const retry = document.createElement('button'); retry.className = 'button primary'; retry.textContent = 'ตรวจรถว่างอีกครั้ง';
      retry.addEventListener('click', () => { host.classList.add('hidden'); searchAvailability({ preventDefault() {} }); });
      host.append(message, retry);
    } else toast(error.message, true);
  }
}

function renderAdmin() {
  const select = $('#blackout-vehicle');
  const selectedVehicleId = select.value;
  select.replaceChildren(...state.vehicles.map((item) => new Option(`${item.PlateNo} · ${item.Name}`, item.VehicleId)));
  if (state.vehicles.some((item) => String(item.VehicleId) === selectedVehicleId)) select.value = selectedVehicleId;
  renderVehicles(); renderRequests();
}

function renderCalendarVehicleFilter() {
  const select = $('#calendar-vehicle-filter');
  const selected = select.value;
  select.replaceChildren(new Option('รถทุกคัน', ''), ...state.vehicles.map((vehicle) => new Option(`${vehicle.PlateNo} · ${vehicle.Name}`, vehicle.VehicleId)));
  if (state.vehicles.some((vehicle) => String(vehicle.VehicleId) === selected)) select.value = selected;
}

function renderCalendar() {
  const [year, month] = state.calendarMonth.split('-').map(Number);
  $('#vehicle-calendar-month').textContent = new Intl.DateTimeFormat('th-TH', { month: 'long', year: 'numeric', timeZone: 'Asia/Bangkok' }).format(new Date(Date.UTC(year, month - 1, 15, 12)));
  const selectedVehicleId = $('#calendar-vehicle-filter').value ? Number($('#calendar-vehicle-filter').value) : null;
  const first = new Date(Date.UTC(year, month - 1, 1));
  const gridStart = new Date(Date.UTC(year, month - 1, 1 - first.getUTCDay()));
  const cells = [];
  for (const name of ['อา.', 'จ.', 'อ.', 'พ.', 'พฤ.', 'ศ.', 'ส.']) {
    const weekday = document.createElement('div'); weekday.className = 'calendar-weekday'; weekday.textContent = name; cells.push(weekday);
  }
  for (let index = 0; index < 42; index++) {
    const date = new Date(gridStart); date.setUTCDate(gridStart.getUTCDate() + index);
    const key = date.toISOString().slice(0, 10);
    const cell = document.createElement('div'); cell.className = `calendar-day${date.getUTCMonth() !== month - 1 ? ' other-month' : ''}`;
    const now = new Date();
    const cellStart = new Date(`${key}T00:00:00+07:00`);
    if (now >= cellStart && now < new Date(cellStart.getTime() + 86400000)) cell.classList.add('today');
    const number = document.createElement('span'); number.className = 'calendar-day-number'; number.textContent = String(date.getUTCDate()); cell.append(number);
    const dayStart = new Date(`${key}T00:00:00+07:00`); const dayEnd = new Date(dayStart.getTime() + 86400000);
    const events = [];
    for (const booking of state.calendarBookings.filter((item) => selectedVehicleId === null || item.VehicleId === selectedVehicleId)) {
      if (['Pending', 'Confirmed'].includes(booking.Status) && new Date(booking.StartAt) < dayEnd && new Date(booking.EndAt) > dayStart) {
        const visibleStart = new Date(Math.max(dayStart.getTime(), new Date(booking.StartAt).getTime()));
        const visibleEnd = new Date(Math.min(dayEnd.getTime(), new Date(booking.EndAt).getTime()));
        const busyTime = `${timeFormat.format(visibleStart)}–${timeFormat.format(visibleEnd)}`;
        events.push({
          kind: booking.Status === 'Pending' ? 'pending' : 'confirmed',
          label: `${booking.PlateNo} · ${busyTime}${booking.Status === 'Pending' ? ' · รออนุมัติ' : ''}`,
          title: `${booking.PlateNo} · ${booking.Status === 'Pending' ? 'รออนุมัติ' : 'จองแล้ว'} ${formatRange(booking.StartAt, booking.EndAt)}`,
          bookingId: booking.BookingId,
          booking
        });
      }
    }
    for (const blackout of state.blackouts.filter((item) => selectedVehicleId === null || item.VehicleId === selectedVehicleId)) {
      if (new Date(blackout.StartAt) < dayEnd && new Date(blackout.EndAt) > dayStart) {
        const visibleStart = new Date(Math.max(dayStart.getTime(), new Date(blackout.StartAt).getTime()));
        const visibleEnd = new Date(Math.min(dayEnd.getTime(), new Date(blackout.EndAt).getTime()));
        const busyTime = `${timeFormat.format(visibleStart)}–${timeFormat.format(visibleEnd)}`;
        events.push({ kind: 'blackout', label: `${blackout.PlateNo} · ซ่อม ${busyTime}`, title: `${blackout.PlateNo} · ซ่อมบำรุง ${formatRange(blackout.StartAt, blackout.EndAt)} · ${blackout.Reason}` });
      }
    }
    const shownEvents = events;
    const list = document.createElement('div'); list.className = 'calendar-events';
    const createEventNode = (event) => {
      const item = event.bookingId ? document.createElement('button') : document.createElement('span');
      item.className = `calendar-event ${event.kind}`; item.textContent = event.label; item.title = event.title || event.label;
      if (event.bookingId) item.addEventListener('click', () => {
        if (isAdminPage()) showCalendarBookingDetails(event.booking);
        else toast(event.title || event.label);
      });
      return item;
    };
    const initialEvents = shownEvents.slice(0, 2);
    list.replaceChildren(...initialEvents.map(createEventNode));
    if (shownEvents.length > 2) {
      const more = document.createElement('button'); more.type = 'button'; more.className = 'calendar-more'; more.setAttribute('aria-expanded', 'false');
      more.textContent = `ดูอีก ${shownEvents.length - 2} รายการ`;
      more.addEventListener('click', () => {
        const expanded = more.getAttribute('aria-expanded') !== 'true';
        more.setAttribute('aria-expanded', String(expanded));
        more.textContent = expanded ? 'ย่อรายการ' : `ดูอีก ${shownEvents.length - 2} รายการ`;
        list.classList.toggle('expanded', expanded);
        list.replaceChildren(...(expanded ? shownEvents : initialEvents).map(createEventNode), more);
      });
      list.append(more);
    }
    cell.append(list); cells.push(cell);
  }
  $('#vehicle-calendar-grid').replaceChildren(...cells);
}

function renderVehicleCalendar() { renderCalendar(); }

function showCalendarBookingDetails(booking) {
  if (!isAdminPage()) return;
  let dialog = $('#calendar-booking-detail-dialog');
  if (!dialog) {
    dialog = document.createElement('dialog'); dialog.id = 'calendar-booking-detail-dialog'; dialog.className = 'calendar-booking-detail-dialog';
    dialog.addEventListener('click', (event) => { if (event.target === dialog) dialog.close(); });
    document.body.append(dialog);
  }
  const card = document.createElement('section'); card.className = 'card calendar-booking-detail-card';
  const header = document.createElement('div'); header.className = 'section-heading compact';
  const titleGroup = document.createElement('div');
  const heading = document.createElement('h2'); heading.textContent = 'รายละเอียดการจอง';
  const bookingNo = document.createElement('p'); bookingNo.textContent = booking.BookingNo || `รายการ #${booking.BookingId}`;
  titleGroup.append(heading, bookingNo);
  const close = document.createElement('button'); close.type = 'button'; close.className = 'mini-button'; close.textContent = 'ปิด'; close.addEventListener('click', () => dialog.close());
  header.append(titleGroup, close);
  const list = document.createElement('dl'); list.className = 'calendar-booking-detail-list';
  const fields = [
    ['สถานะ', statusText[booking.Status] || booking.Status],
    ['รถ', `${booking.PlateNo} · ${booking.VehicleName || ''}`],
    ['ผู้จอง', booking.EmployeeName || 'ไม่พบข้อมูลผู้จอง'],
    ['เวลาเดินทาง', formatRange(booking.StartAt, booking.EndAt)],
    ['ปลายทาง', booking.Destination || '—'],
    ['วัตถุประสงค์', booking.Purpose || '—']
  ];
  for (const [label, value] of fields) {
    const term = document.createElement('dt'); term.textContent = label;
    const description = document.createElement('dd'); description.textContent = value;
    list.append(term, description);
  }
  card.append(header, list); dialog.replaceChildren(card); dialog.showModal();
}

function renderVehicles() {
  const host = $('#vehicles-list');
  if (!state.vehicles.length) { host.innerHTML = '<div class="empty-state small-empty">ยังไม่มีทะเบียนรถ</div>'; return; }
  const vehicleForm = $('#vehicle-form').classList.contains('hidden') ? null : $('#vehicle-form');
  const vehicleFormTarget = vehicleForm && $('#vehicle-id').value ? Number($('#vehicle-id').value) : null;
  const blackoutForm = $('#blackout-form').classList.contains('hidden') ? null : $('#blackout-form');
  const blackoutFormTarget = blackoutForm ? Number($('#blackout-vehicle').value) : null;
  const entries = state.vehicles.map((vehicle) => {
    const entry = document.createElement('div'); entry.className = 'vehicle-entry'; entry.dataset.vehicleId = vehicle.VehicleId;
    const row = document.createElement('div'); row.className = 'admin-row';
    const info = document.createElement('div'); info.className = 'admin-row-info';
    const title = document.createElement('strong'); title.textContent = `${vehicle.PlateNo} · ${vehicle.Name}`;
    const titleLine = document.createElement('div'); titleLine.className = 'vehicle-title-line'; titleLine.append(title);
    info.append(titleLine);
    const seats = document.createElement('span'); seats.className = 'vehicle-seats'; seats.textContent = `${vehicle.Seats} ที่นั่ง`;
    const stateBadge = document.createElement('span'); stateBadge.className = `vehicle-status ${vehicle.Status === 'Active' ? 'is-active' : 'is-retired'}`; stateBadge.textContent = statusText[vehicle.Status] || vehicle.Status;
    const actions = document.createElement('div'); actions.className = 'row-actions';
    if (vehicle.Status === 'Active') {
      const edit = document.createElement('button'); edit.className = 'mini-button'; edit.textContent = 'แก้ไข'; edit.addEventListener('click', () => editVehicle(vehicle)); actions.append(edit);
      const repair = document.createElement('button'); repair.className = 'mini-button'; repair.textContent = 'กำหนดซ่อม'; repair.addEventListener('click', () => openBlackoutForm(vehicle.VehicleId)); actions.append(repair);
    }
    const status = document.createElement('button'); status.className = `mini-button ${vehicle.Status === 'Active' ? 'danger-text' : ''}`; status.textContent = vehicle.Status === 'Active' ? 'ปิดรถ' : 'เปิดรถ';
    status.addEventListener('click', () => changeVehicleStatus(vehicle)); actions.append(status);
    row.append(info, seats, stateBadge, actions); entry.append(row);
    const blackouts = state.blackouts.filter((item) => item.VehicleId === vehicle.VehicleId);
    if (blackouts.length) {
      const maintenance = document.createElement('div'); maintenance.className = 'vehicle-maintenance-list';
      blackouts.forEach((item) => maintenance.append(createBlackoutRow(item)));
      entry.append(maintenance);
    }
    return entry;
  });
  const children = [];
  entries.forEach((entry) => {
    children.push(entry);
    const vehicleId = Number(entry.dataset.vehicleId);
    if (vehicleForm && vehicleFormTarget === vehicleId) children.push(vehicleForm);
    if (blackoutForm && blackoutFormTarget === vehicleId) children.push(blackoutForm);
  });
  host.replaceChildren(...children);
}

function editVehicle(vehicle) {
  resetBlackoutForm(); resetVehicleForm();
  $('#vehicle-id').value = vehicle.VehicleId; $('#plate-no').value = vehicle.PlateNo; $('#vehicle-name').value = vehicle.Name; $('#vehicle-seats').value = vehicle.Seats;
  positionFleetForm($('#vehicle-form'), vehicle.VehicleId);
  $('#vehicle-form').classList.remove('hidden'); $('#save-vehicle').textContent = 'บันทึกการแก้ไข'; $('#cancel-edit').classList.remove('hidden'); $('#plate-no').focus();
}
function resetVehicleForm() { $('#vehicle-form').reset(); $('#vehicle-id').value = ''; $('#vehicle-seats').value = '5'; $('#save-vehicle').textContent = 'เพิ่มรถ'; $('#cancel-edit').classList.add('hidden'); $('#vehicle-form').classList.add('hidden'); $('#fleet-form-host').append($('#vehicle-form')); }
function positionFleetForm(form, vehicleId) {
  const entry = [...document.querySelectorAll('.vehicle-entry')].find((item) => Number(item.dataset.vehicleId) === Number(vehicleId));
  if (entry) entry.after(form); else $('#fleet-form-host').append(form);
}

function createBlackoutRow(item) {
    const row = document.createElement('div'); row.className = 'admin-row';
    const info = document.createElement('div'); info.className = 'admin-row-info';
    const title = document.createElement('strong'); title.textContent = `ซ่อมบำรุง · ${formatRange(item.StartAt, item.EndAt)}`;
    const meta = document.createElement('span'); meta.textContent = item.Reason;
    info.append(title, meta);
    const actions = document.createElement('div'); actions.className = 'row-actions';
    const edit = document.createElement('button'); edit.className = 'mini-button'; edit.textContent = 'แก้ไข'; edit.addEventListener('click', () => editBlackout(item)); actions.append(edit);
    const remove = document.createElement('button'); remove.className = 'mini-button danger-text'; remove.textContent = 'ลบ'; remove.addEventListener('click', async () => {
      if (!confirm('ยืนยันลบช่วงซ่อมบำรุงนี้หรือไม่?')) return;
      try { await api(`/blackouts/${item.VehicleBlackoutId}?actorId=${encodeURIComponent(selectedUser().employeeId)}`, { method: 'DELETE' }); await refreshData(); toast('ลบช่วงซ่อมบำรุงแล้ว'); } catch (error) { toast(error.message, true); }
    }); actions.append(remove);
    row.append(info, actions); return row;
}

function editBlackout(item) {
  const vehicleEntry = [...document.querySelectorAll('.vehicle-entry')].find((entry) => Number(entry.dataset.vehicleId) === Number(item.VehicleId));
  resetVehicleForm(); resetBlackoutForm();
  $('#blackout-id').value = item.VehicleBlackoutId;
  $('#blackout-vehicle').value = item.VehicleId;
  $('#blackout-start').value = toBangkokInput(item.StartAt);
  $('#blackout-end').value = toBangkokInput(item.EndAt);
  $('#blackout-reason').value = item.Reason;
  $('#save-blackout').textContent = 'บันทึกการแก้ไข';
  if (vehicleEntry) vehicleEntry.after($('#blackout-form'));
  $('#blackout-form').classList.remove('hidden'); $('#cancel-blackout-edit').classList.remove('hidden');
  $('#blackout-form').scrollIntoView({ behavior: 'smooth', block: 'center' });
}
function openBlackoutForm(vehicleId) {
  resetVehicleForm(); resetBlackoutForm();
  $('#blackout-vehicle').value = String(vehicleId);
  positionFleetForm($('#blackout-form'), vehicleId);
  $('#blackout-form').classList.remove('hidden');
  $('#cancel-blackout-edit').classList.remove('hidden');
  $('#blackout-form').scrollIntoView({ behavior: 'smooth', block: 'center' });
  $('#blackout-start').focus();
}
function resetBlackoutForm() {
  $('#blackout-form').reset(); $('#blackout-id').value = ''; $('#save-blackout').textContent = 'เพิ่มช่วงซ่อม'; $('#cancel-blackout-edit').classList.add('hidden'); $('#blackout-form').classList.add('hidden'); $('#fleet-form-host').append($('#blackout-form'));
}

function renderRequests() {
  const host = $('#admin-bookings');
  const pendingCount = state.bookings.filter((item) => item.Status === 'Pending').length;
  $('#request-notice').classList.toggle('hidden', pendingCount === 0);
  $('#request-notice-text').textContent = pendingCount === 1
    ? 'มีคำขอจองรถรออนุมัติ 1 รายการ'
    : `มีคำขอจองรถรออนุมัติ ${pendingCount} รายการ`;
  const sortMode = $('#request-sort').value;
  const rows = [...state.bookings].sort((a, b) => {
    if (sortMode === 'recent') return new Date(b.CreatedAt) - new Date(a.CreatedAt);
    if (sortMode === 'upcoming') return new Date(a.StartAt) - new Date(b.StartAt);
    const priority = (item) => item.Status === 'Pending' ? 0 : 1;
    return priority(a) - priority(b) || new Date(a.StartAt) - new Date(b.StartAt);
  });
  if (!rows.length) { host.innerHTML = '<div class="empty-state"><strong>ยังไม่มีรายการจอง</strong><span>คำขอและรายการจองจะแสดงที่นี่</span></div>'; $('#admin-booking-history-section').classList.add('hidden'); return; }
  const cards = rows.map((booking) => {
    const article = document.createElement('article'); article.className = 'request-row';
    article.id = `request-${booking.BookingId}`;
    const top = document.createElement('div'); top.className = 'request-top';
    const no = document.createElement('div'); no.className = 'request-number'; no.textContent = booking.BookingNo;
    const completed = booking.Status === 'Confirmed' && new Date(booking.EndAt) <= new Date();
    const displayStatus = completed ? 'Completed' : booking.Status;
    const status = document.createElement('span'); status.className = statusClass(displayStatus); status.textContent = statusText[displayStatus] || displayStatus;
    top.append(no, status);
    const title = document.createElement('h3'); title.textContent = `${booking.PlateNo} · ${booking.VehicleName}`;
    const detail = document.createElement('div'); detail.className = 'request-details';
    const lines = [`${booking.EmployeeName} · ${formatRange(booking.StartAt, booking.EndAt)}`, `ปลายทาง: ${booking.Destination}`, `วัตถุประสงค์: ${booking.Purpose}`];
    if (booking.Status === 'Pending' && booking.ExpiresAt) lines.push(`คิวหมดอายุ: ${formatDate(booking.ExpiresAt)}`);
    if (booking.Status === 'Pending' && Number(booking.PendingConflictCount) > 0)
      lines.push(`มีคำขอเวลาเดียวกันอีก ${booking.PendingConflictCount} รายการ · อนุมัติรายการหนึ่งแล้ว ระบบจะยกเลิกคำขอที่ทับซ้อน`);
    for (const text of lines) {
      const line = document.createElement('span'); line.textContent = text;
      if (text.startsWith('มีคำขอเวลาเดียวกัน')) line.className = 'request-conflict-warning';
      detail.append(line);
    }
    article.append(top, title, detail);
    if (booking.Status === 'Pending') {
      const actions = document.createElement('div'); actions.className = 'request-actions';
      const reason = document.createElement('input'); reason.type = 'text'; reason.maxLength = 1000; reason.required = true;
      reason.placeholder = 'ระบุเหตุผลเพื่อปฏิเสธคำขอ'; reason.setAttribute('aria-label', `เหตุผลที่ปฏิเสธคำขอ ${booking.BookingNo}`); reason.classList.add('hidden');
      const approve = document.createElement('button'); approve.className = 'button primary small-button'; approve.textContent = 'อนุมัติ';
      approve.addEventListener('click', () => {
        const conflicts = state.bookingConflicts.filter((item) => Number(item.BookingId) === Number(booking.BookingId));
        if (conflicts.length) {
          const list = conflicts.map((item) => `${item.OtherBookingNo} · ${item.OtherEmployeeName} · ${formatRange(item.StartAt, item.EndAt)}`).join('\n');
          if (!confirm(`มีคำขอเวลาทับซ้อน ${conflicts.length} รายการ:\n${list}\n\nเมื่ออนุมัติ คำขอที่ทับซ้อนเหล่านี้จะถูกยกเลิกและผู้จองจะเห็นข้อความแจ้งในหน้าเว็บ ต้องการอนุมัติต่อหรือไม่?`)) return;
        }
        updateBooking(booking, 'Confirmed');
      });
      const reject = document.createElement('button'); reject.className = 'button secondary small-button'; reject.textContent = 'ปฏิเสธ';
      reject.addEventListener('click', () => {
        if (reason.classList.contains('hidden')) {
          reason.classList.remove('hidden'); reject.textContent = 'ยืนยันปฏิเสธ'; reason.focus(); return;
        }
        updateBooking(booking, 'Rejected', reason.value);
      });
      actions.append(reason, approve, reject); article.append(actions);
    } else if (booking.Status === 'Confirmed' && !completed && new Date(booking.StartAt) > new Date()) {
      const actions = document.createElement('div'); actions.className = 'request-actions';
      const reason = document.createElement('input'); reason.type = 'text'; reason.maxLength = 1000; reason.placeholder = 'เหตุผลการยกเลิก (จำเป็น)';
      const cancel = document.createElement('button'); cancel.className = 'button secondary small-button'; cancel.textContent = 'ยกเลิกการจอง';
      cancel.addEventListener('click', () => updateBooking(booking, 'Cancelled', reason.value));
      actions.append(reason, cancel); article.append(actions);
    }
    if (booking.StatusReason) { const reason = document.createElement('p'); reason.className = 'last-reason'; reason.textContent = `เหตุผลล่าสุด: ${booking.StatusReason}`; article.append(reason); }
    const archived = completed || ['Rejected', 'Cancelled'].includes(booking.Status);
    if (state.showBookingHistory && archived) appendBookingAudit(article, booking);
    return article;
  });
  const activeRows = rows.filter((booking) => booking.Status === 'Pending' || (booking.Status === 'Confirmed' && new Date(booking.EndAt) > new Date()));
  const historyRows = rows.filter((booking) => !activeRows.includes(booking));
  const visibleRows = state.showBookingHistory ? historyRows : activeRows;
  host.replaceChildren(...cards.filter((_, index) => visibleRows.includes(rows[index])));
  if (!visibleRows.length) {
    const empty = document.createElement('div'); empty.className = 'empty-state';
    const title = document.createElement('strong'); title.textContent = state.showBookingHistory ? 'ยังไม่มีประวัติการจอง' : 'ไม่มีคำขอหรือรายการจองปัจจุบัน';
    const detail = document.createElement('span'); detail.textContent = state.showBookingHistory ? 'รายการที่เสร็จสิ้น ปฏิเสธ หรือยกเลิกจะแสดงที่นี่' : 'กดปุ่มประวัติเพื่อดูรายการที่ผ่านมา';
    empty.append(title, detail); host.replaceChildren(empty);
  }
  $('#admin-booking-history-section').classList.add('hidden');
}

async function changeBookingStatus(booking, status) {
  const reason = status === 'Cancelled' ? prompt('ระบุเหตุผลการยกเลิก') : '';
  if (status === 'Cancelled' && !reason?.trim()) return;
  await updateBooking(booking, status, reason);
}
async function updateBooking(booking, status, reason = '') {
  const cleanReason = reason?.trim() || '';
  if (['Rejected', 'Cancelled'].includes(status) && !cleanReason) { toast('กรุณาระบุเหตุผลก่อนปฏิเสธหรือยกเลิก', true); return; }
  try {
    const result = await api(`/${booking.BookingId}/status`, { method: 'PATCH', body: JSON.stringify({ actorId: selectedUser().employeeId, isAdmin: isAdminPage(), status, reason: cleanReason || null }) });
    await refreshData();
    if (status === 'Confirmed') {
      const notices = [];
      if (result.cancelledRequests?.length) notices.push(`ยกเลิกคำขอเวลาทับซ้อน ${result.cancelledRequests.length} รายการแล้ว`);
      const vehicle = result.alternativeVehicles?.[0];
      if (vehicle) notices.push(`มีรถอื่นว่างช่วงเดิม: ${vehicle.PlateNo} · ${vehicle.VehicleName}`);
      else if (result.alternativeTime) notices.push(`รถคันนี้ว่างอีกครั้ง ${formatRange(result.alternativeTime.StartAt, result.alternativeTime.EndAt)}`);
      toast(notices.join(' · ') || 'อนุมัติคำขอแล้ว');
    } else toast('บันทึกสถานะและเหตุผลแล้ว');
  } catch (error) { toast(error.message, true); }
}

async function saveVehicle(event) {
  event.preventDefault(); const id = $('#vehicle-id').value;
  const body = JSON.stringify({ actorId: selectedUser().employeeId, plateNo: $('#plate-no').value.trim(), name: $('#vehicle-name').value.trim(), seats: Number($('#vehicle-seats').value) });
  try {
    await api(id ? `/vehicles/${id}` : '/vehicles', { method: id ? 'PUT' : 'POST', body });
    resetVehicleForm(); await refreshData(); toast(id ? 'แก้ไขข้อมูลรถแล้ว' : 'เพิ่มรถแล้ว');
  } catch (error) { toast(error.message, true); }
}
async function changeVehicleStatus(vehicle) {
  const status = vehicle.Status === 'Active' ? 'Retired' : 'Active';
  const reason = prompt(status === 'Retired' ? 'ระบุเหตุผลปิดใช้งานรถ' : 'ระบุเหตุผลเปิดใช้งานรถ');
  if (!reason?.trim()) return;
  try {
    const result = await apiWithAffectedBookingConfirmation(`/vehicles/${vehicle.VehicleId}/status`, {
      method: 'PATCH', body: JSON.stringify({ actorId: selectedUser().employeeId, status, reason: reason.trim() })
    }, 'ปิดรถ');
    if (!result) return;
    await refreshData();
    toast(result.cancelledBookings?.length ? `ปิดรถแล้ว ยกเลิกและแจ้งผู้จอง ${result.cancelledBookings.length} รายการ` : `เปลี่ยนสถานะรถเป็น${statusText[status]}แล้ว`);
  }
  catch (error) { toast(error.message, true); }
}
async function saveBlackout(event) {
  event.preventDefault();
  const id = $('#blackout-id').value;
  const startAt = bangkokIso('#blackout-start'); const endAt = bangkokIso('#blackout-end');
  if (new Date(startAt) >= new Date(endAt)) { toast('เวลาสิ้นสุดต้องอยู่หลังเวลาเริ่ม', true); return; }
  try {
    const result = await apiWithAffectedBookingConfirmation(id ? `/blackouts/${id}` : '/blackouts', {
      method: id ? 'PUT' : 'POST',
      body: JSON.stringify({ actorId: selectedUser().employeeId, vehicleId: Number($('#blackout-vehicle').value), startAt, endAt, reason: $('#blackout-reason').value.trim() })
    }, 'กำหนดช่วงซ่อมบำรุง');
    if (!result) return;
    resetBlackoutForm(); await refreshData();
    toast(result.cancelledBookings?.length ? `บันทึกช่วงซ่อมแล้ว ยกเลิกและแจ้งผู้จอง ${result.cancelledBookings.length} รายการ` : (id ? 'แก้ไขช่วงซ่อมบำรุงแล้ว' : 'เพิ่มช่วงซ่อมบำรุงแล้ว'));
  } catch (error) { toast(error.message, true); }
}

$('#search-form').addEventListener('submit', searchAvailability);
$('#details-form').addEventListener('submit', (event) => { event.preventDefault(); if (!state.selectedVehicle) return; buildReview(); });
$('#vehicle-form').addEventListener('submit', saveVehicle);
$('#add-vehicle').addEventListener('click', () => { resetVehicleForm(); $('#vehicle-form').classList.remove('hidden'); $('#cancel-edit').classList.remove('hidden'); $('#plate-no').focus(); });
$('#cancel-edit').addEventListener('click', resetVehicleForm);
$('#blackout-form').addEventListener('submit', saveBlackout);
$('#cancel-blackout-edit').addEventListener('click', resetBlackoutForm);
$('#demo-user').addEventListener('change', () => { state.viewMode = 'employee'; clearBookingFlow(); render(); syncAdminPolling(); refreshData().catch((error) => toast(error.message, true)); });
$('#booking-view-mode').addEventListener('change', () => { state.viewMode = $('#booking-view-mode').value; render(); syncAdminPolling(); refreshData().catch((error) => toast(error.message, true)); });
$('#refresh-mine').addEventListener('click', () => refreshData().catch((error) => toast(error.message, true)));
$('#refresh-admin').addEventListener('click', () => refreshData().catch((error) => toast(error.message, true)));
function shiftMonth(offset) {
  const [year, month] = state.calendarMonth.split('-').map(Number);
  state.calendarMonth = new Date(Date.UTC(year, month - 1 + offset, 1)).toISOString().slice(0, 7);
  renderVehicleCalendar();
}

function setupCalendarZoom() {
  const dialog = $('#vehicle-calendar-dialog');
  const scroll = dialog.querySelector('.calendar-scroll');
  const controls = document.createElement('div'); controls.className = 'calendar-zoom-controls';
  const zoomOut = document.createElement('button'); zoomOut.type = 'button'; zoomOut.className = 'mini-button'; zoomOut.textContent = '−'; zoomOut.setAttribute('aria-label', 'ย่อปฏิทิน');
  const reset = document.createElement('button'); reset.type = 'button'; reset.className = 'calendar-zoom-value'; reset.setAttribute('aria-label', 'คืนค่าขนาดปฏิทิน 100%');
  const zoomIn = document.createElement('button'); zoomIn.type = 'button'; zoomIn.className = 'mini-button'; zoomIn.textContent = '+'; zoomIn.setAttribute('aria-label', 'ขยายปฏิทิน');
  const applyScale = (nextScale) => {
    const oldMaxScroll = scroll.scrollWidth - scroll.clientWidth;
    const scrollRatio = oldMaxScroll > 0 ? scroll.scrollLeft / oldMaxScroll : 0;
    state.calendarScale = Math.max(70, Math.min(130, nextScale));
    $('#vehicle-calendar-grid').style.setProperty('--calendar-scale', String(state.calendarScale / 100));
    reset.textContent = `${state.calendarScale}%`;
    zoomOut.disabled = state.calendarScale <= 70;
    zoomIn.disabled = state.calendarScale >= 130;
    requestAnimationFrame(() => {
      const maxScroll = scroll.scrollWidth - scroll.clientWidth;
      scroll.scrollLeft = maxScroll * scrollRatio;
    });
  };
  zoomOut.addEventListener('click', () => applyScale(state.calendarScale - 10));
  zoomIn.addEventListener('click', () => applyScale(state.calendarScale + 10));
  reset.addEventListener('click', () => applyScale(100));
  controls.append(zoomOut, reset, zoomIn);
  scroll.before(controls);
  applyScale(state.calendarScale);
}

function setupBookingHistoryButtons() {
  const makeButton = (id, refreshButton, isAdmin) => {
    const button = document.createElement('button'); button.id = id; button.type = 'button'; button.className = 'text-button booking-history-mode-button';
    button.setAttribute('aria-pressed', 'false');
    const icon = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    icon.setAttribute('viewBox', '0 0 24 24'); icon.setAttribute('aria-hidden', 'true'); icon.classList.add('booking-history-icon');
    const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
    path.setAttribute('d', 'M3 11a9 9 0 1 1 2.7 6.4M3 4v7h7M12 7v5l3 2'); icon.append(path);
    const label = document.createElement('span'); label.textContent = 'ประวัติ';
    button.append(icon, label);
    button.addEventListener('click', () => {
      state.showBookingHistory = !state.showBookingHistory;
      for (const control of document.querySelectorAll('.booking-history-mode-button')) {
        control.setAttribute('aria-pressed', String(state.showBookingHistory));
        control.querySelector('span').textContent = state.showBookingHistory ? 'กลับรายการปัจจุบัน' : 'ประวัติ';
      }
      if (isAdmin) renderRequests(); else renderMine();
    });
    refreshButton.after(button);
  };
  makeButton('show-my-booking-history', $('#refresh-mine'), false);
  makeButton('show-admin-booking-history', $('#refresh-admin'), true);
}
$('#calendar-month-prev').addEventListener('click', () => shiftMonth(-1));
$('#calendar-month-next').addEventListener('click', () => shiftMonth(1));
$('#calendar-vehicle-filter').addEventListener('change', renderVehicleCalendar);
$('#request-sort').addEventListener('change', renderRequests);
$('#show-calendar').addEventListener('click', () => $('#vehicle-calendar-dialog').showModal());
$('#close-calendar').addEventListener('click', () => $('#vehicle-calendar-dialog').close());
$('#vehicle-calendar-dialog').addEventListener('click', (event) => {
  if (event.target === $('#vehicle-calendar-dialog')) $('#vehicle-calendar-dialog').close();
});
setupCalendarZoom();
setupBookingHistoryButtons();
window.addEventListener('hashchange', () => { render(); syncAdminPolling(); refreshData().catch((error) => toast(error.message, true)); });
render();
initialize();
