# การเชื่อมต่อ Active Directory สำหรับทดสอบ MosciiHR

ค่าด้านล่างมาจากเอกสารเซิร์ฟเวอร์ทดสอบ StarCat ใช้สำหรับตั้งค่าและตรวจรับการเข้าสู่ระบบใน [สเปก MosciiHR](../MOSCIIHR_SPEC.md)

| รายการ | ค่าทดสอบ |
|---|---|
| ที่อยู่หลัก | `ldaps://ldap.moscii.com:636` |
| เครือข่าย | ภายในออฟฟิศหรือผ่าน VPN |
| TLS | ตรวจชื่อโฮสต์และสายใบรับรองด้วย trust store ของระบบ เอกสารต้นทางระบุว่าใช้ Let's Encrypt |
| พอร์ตสำรอง | 389 โดยต้องเปิด StartTLS สำเร็จก่อน bind |
| Domain / NetBIOS | `CORP.STARCAT.TEST` / `CORP` |
| Base DN | `DC=corp,DC=starcat,DC=test` |
| User search base | `OU=StarCat,DC=corp,DC=starcat,DC=test` |
| กลุ่มทดสอบ | `CN=StarCat-Users,OU=StarCat,DC=corp,DC=starcat,DC=test` |

## ลำดับการเข้าสู่ระบบที่เสนอ

1. เชื่อมผ่าน LDAPS แล้วใช้บัญชีค้นหา `svc-starcat@corp.starcat.test` โดยอ่านรหัสผ่านจาก secret store
2. ค้นหาด้วย `sAMAccountName` หรือ `userPrincipalName` ภายใน OU ทดสอบ โดย escape ค่าที่ผู้ใช้กรอกตาม RFC 4515 และรับผลตรงเพียงหนึ่งรายการ
3. ปฏิเสธรหัสผ่านว่างและบัญชีที่มีบิต ACCOUNTDISABLE (0x0002) ใน `userAccountControl` จากนั้น bind ด้วย DN ของผู้ใช้และรหัสผ่านที่กรอก
4. เมื่อยืนยันตัวตนสำเร็จ ใช้ `employeeID` จับคู่พนักงานกับวันลาและใบลงเวลา หากรหัสว่างหรือซ้ำให้ส่งผู้ดูแลตรวจสอบก่อนเปิดสิทธิ์

## เช็กลิสต์ตรวจรับบนเครือข่ายทดสอบ

- [ ] ตรวจ TLS ผ่านชื่อ ldap.moscii.com บนพอร์ต 636 จากเครื่องที่รัน MosciiHR
- [ ] บัญชี alice เข้าสู่ระบบด้วยรหัสผ่านที่ถูกต้องและอ่าน employeeID ได้ E1001
- [ ] บัญชี bob มี employeeID E1002 และถูกปิดใช้งาน ระบบต้องปฏิเสธการเข้าสู่ระบบแม้อยู่ในกลุ่มทดสอบ
- [ ] รหัสผ่านผิด รหัสผ่านว่าง หรือใบรับรองไม่ผ่านการตรวจสอบ ต้องไม่มี session ใหม่
- [ ] ยืนยันกับ IT และใบเตยว่ารหัสในไฟล์ลงเวลาจับคู่กับ employeeID ได้ก่อนนำเข้าข้อมูลจริง
