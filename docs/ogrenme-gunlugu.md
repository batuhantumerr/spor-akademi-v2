# Öğrenme günlüğü

Her maddeden sonra kendi cümlelerinle: kavram, projede nerede, alternatif, bedel, 30 saniyelik mülakat cevabı, hâlâ emin olmadığın. Agent sadece **taslak** önerir; taslaklar "Taslak" başlığıyla işaretlidir.

## Taslak: Secure cookie, `http://localhost` ve WebKit (P1-36, DEC-240)
- **Kavram:** `Secure` işaretli bir cookie sadece güvenli bağlantıda gönderilir. Chromium ve Firefox `http://localhost`'u "potansiyel olarak güvenilir" sayıp Secure cookie'yi gönderir; Playwright'ın WebKit'i cookie'yi saklar ama `http://localhost`'a **göndermez**.
- **Projede:** refresh cookie'si (`sa_refresh`) `Secure; HttpOnly; SameSite=Strict`. E2e ilk denemede düz HTTP'deydi: WebKit'te sayfa yenilenince `/auth/refresh` cookie'siz gidip `401` aldı, diğer tarayıcılar geçti. Çözüm uygulamayı değiştirmek değil, test sunucusunu production gibi HTTPS'e almak oldu (`e2e/serve.mjs`, çalıştırmaya özel self-signed sertifika, `ignoreHTTPSErrors`).
- **Alternatif:** e2e'de cookie'yi `Secure`'sız vermek (production'dan sapma, asıl korumayı test dışı bırakır) veya WebKit'te yenileme testini atlamak (Safari kullanıcıları için kör nokta).
- **Bedel:** e2e betiği `openssl` ile sertifika üretir; Git Bash'te `/CN=...` yol sanılmasın diye `MSYS_NO_PATHCONV=1` ve Windows biçimli yollar gerekir.
- **Ek not:** Windows'taki WebKit, cookie deposunda `SameSite`'ı `None` gösterir; bu yüzden test `SameSite=Strict`'i tarayıcıdan değil yanıtın `Set-Cookie` başlığından doğrular.
- **30 saniyelik cevap:** "Refresh token'ı HttpOnly + Secure + SameSite=Strict cookie'de tutuyoruz. E2e'yi üç tarayıcı motorunda HTTPS üzerinden koşturuyoruz; çünkü WebKit localhost'a bile Secure cookie göndermiyor ve HTTP'de oturum geri yükleme hatasını gizlerdik."
- **Hâlâ emin olmadığın:** gerçek Safari'nin (Playwright WebKit değil) localhost davranışı sürüme göre değişiyor mu?
