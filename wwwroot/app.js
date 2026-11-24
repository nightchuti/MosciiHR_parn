const pages = {
  booking: ['จองรถบริษัท', 'ระบุช่วงเวลาและปลายทางที่ต้องการเดินทาง', [['เวลาออกเดินทาง', 'datetime-local'], ['เวลาคืนรถ', 'datetime-local'], ['ปลายทาง', 'text']]],
  leave: ['ยื่นวันลา', 'ระบุประเภทและช่วงวันที่ต้องการลา', [['ประเภทการลา', ['ลาป่วย', 'ลากิจ', 'ลาพักร้อน']], ['วันเริ่มต้น', 'date'], ['วันสิ้นสุด', 'date']]],
  time: ['ใบลงเวลา', 'เลือกวันที่เพื่อลองกรอกเวลาเข้าและออกงาน', [['วันทำงาน', 'date'], ['เวลาเข้า', 'time'], ['เวลาออก', 'time']]]
};
function render() {
  const key = Object.hasOwn(pages, location.hash.slice(1)) ? location.hash.slice(1) : 'booking';
  const examples = {
    booking: 'รถส่วนกลาง 01 · 09.00–11.00 น. · ไปสำนักงานลูกค้า',
    leave: 'ลาพักร้อน · 1 วัน · รอหัวหน้าอนุมัติ',
    time: 'เวลาเข้า 08.45 น. · ขาดเวลาออก · รอพนักงานกรอก'
  };
  document.querySelector('#example').textContent = 'รายการตัวอย่าง — ' + examples[key];
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
