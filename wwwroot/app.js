const pages = {
  booking: ['จองรถบริษัท', 'ระบุช่วงเวลาและปลายทางที่ต้องการเดินทาง', [['เวลาออกเดินทาง', 'datetime-local'], ['เวลาคืนรถ', 'datetime-local'], ['ปลายทาง', 'text']]],
  leave: ['ยื่นวันลา', 'ระบุประเภทและช่วงวันที่ต้องการลา', [['ประเภทการลา', ['ลาป่วย', 'ลากิจ', 'ลาพักร้อน']], ['วันเริ่มต้น', 'date'], ['วันสิ้นสุด', 'date']]],
  time: ['ใบลงเวลา', 'เลือกวันที่เพื่อลองกรอกเวลาเข้าและออกงาน', [['วันทำงาน', 'date'], ['เวลาเข้า', 'time'], ['เวลาออก', 'time']]]
};
function render() {
  const key = Object.hasOwn(pages, location.hash.slice(1)) ? location.hash.slice(1) : 'booking';
  const [title, description, fields] = pages[key];
  document.querySelectorAll('nav a').forEach(link => link.setAttribute('aria-current', link.hash === '#' + key ? 'page' : 'false'));
  document.querySelector('#heading').textContent = title;
  document.querySelector('#description').textContent = description;
  document.querySelector('#fields').replaceChildren(...fields.map(([name, type]) => {
    const label = document.createElement('label');
    label.textContent = name;
    const input = document.createElement(Array.isArray(type) ? 'select' : 'input');
    if (Array.isArray(type)) type.forEach(value => input.add(new Option(value)));
    else input.type = type;
    label.append(input);
    return label;
  }));
}
window.addEventListener('hashchange', render);
render();
document.querySelector('#check').onclick = async function () {
  const status = document.querySelector('#status');
  this.disabled = true;
  status.textContent = 'กำลังตรวจสอบ…';
  try {
    const response = await fetch('/api/database', {cache: 'no-store'});
    status.textContent = (await response.json()).message;
  } catch {
    status.textContent = 'ติดต่อเว็บเซิร์ฟเวอร์ไม่ได้';
  } finally {
    this.disabled = false;
  }
};
