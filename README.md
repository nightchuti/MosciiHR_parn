# MosciiHR

เว็บตั้งต้น ASP.NET Core บน .NET 10 มีหน้าทดลองจองรถ ยื่นลา และใบลงเวลา พร้อมปุ่มตรวจ SQL Server ด้วย SELECT DB_NAME() แบบอ่านอย่างเดียว ฟอร์มทั้งสามเป็นต้นแบบและยังไม่บันทึกข้อมูล

## เริ่มบนเครื่องนักพัฒนา

ติดตั้ง .NET 10 SDK เปิด PowerShell ในโฟลเดอร์ที่มี MosciiHR.csproj และต่อเครือข่ายบริษัทหรือ VPN

~~~powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Database__Password = [System.Net.NetworkCredential]::new('', (Read-Host 'SQL password' -AsSecureString)).Password
dotnet run --urls http://localhost:5080
~~~

เปิด http://localhost:5080 แล้วกด **ตรวจการเชื่อมต่อ** ระบบใช้ mcb.moscii.com,1455 ฐาน HRMOSCII และบัญชีพัฒนา trainee ตาม appsettings.json รหัสผ่านอยู่ใน environment ของ session นี้ หลังเลิกใช้งานกด Ctrl+C และรัน `Remove-Item Env:Database__Password` หากไม่มีรหัสผ่าน ยังเปิดดูหน้าตัวอย่างได้

การเชื่อมต่อตรวจใบรับรอง SQL Server เครื่องพัฒนาต้องเชื่อถือ CA ที่ออกใบรับรอง โครงเริ่มต้นนี้ใช้สำหรับรันภายในเครื่อง ขั้นตอนถัดไปตาม [สเปก](MOSCIIHR_SPEC.md) ได้แก่ LDAPS สิทธิ์ผู้ใช้ การสร้างตาราง และการบันทึกคำขอ
