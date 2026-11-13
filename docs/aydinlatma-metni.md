# Kişisel Verilerin İşlenmesine İlişkin Aydınlatma Metni (taslak)

> **Durum:** Taslak şablon (P4-06, NFR-PRV-06, DEC-543). Hukuki teyit sahiptedir; gerçek veriyle kullanılmadan önce bir
> hukukçu tarafından gözden geçirilmeli ve her okul kendi bilgileriyle doldurmalıdır. Köşeli parantez içindeki alanlar
> okul tarafından doldurulur. Örnekteki okul adı ve adresler sentetiktir.
>
> Uygulamalardaki bağlantı (`/aydinlatma-metni.html`) bu metnin HTML hâlini gösterir. HTML sayfaları bu dosyadan
> `node scripts/privacy-notice.mjs` ile üretilir; elle düzenlenmez, CI (`repo-checks`) farkı yakalar.

## 1. Veri sorumlusu

6698 sayılı Kişisel Verilerin Korunması Kanunu ("KVKK") uyarınca kişisel verileriniz, veri sorumlusu sıfatıyla
**[Örnek Spor Kulübü]** ("Okul") tarafından aşağıda açıklanan kapsamda işlenir.

- Adres: [Örnek Mah. Deneme Sok. No: 1, Kadıköy / İstanbul]
- E-posta: [kvkk@ornek-spor.example]
- Telefon: [0 (216) 000 00 00]

Spor Akademi yazılımı, Okul adına veri işleyen konumundadır; verileri yalnız Okul'un talimatıyla ve bu metindeki
amaçlarla işler.

## 2. İşlenen kişisel veriler

| Kişi | Veri kategorisi | Veriler |
| --- | --- | --- |
| Öğrenci | Kimlik | Ad, soyad, doğum tarihi, cinsiyet |
| Öğrenci | İletişim | Telefon, e-posta (yetişkin öğrenci için) |
| Öğrenci | Acil durum | Acil durumda aranacak kişinin adı, telefonu, yakınlığı |
| Öğrenci | Sağlık (özel nitelikli) | Sağlık raporu (spor yapmaya engel durumu yoktur belgesi) |
| Öğrenci | Görsel | Fotoğraf (antrenörün öğrenciyi tanıması için) |
| Öğrenci | Okul kayıtları | Grup kayıtları, yoklama, aidat ve ödeme bilgileri |
| Veli | Kimlik ve iletişim | Ad, soyad, telefon, e-posta, adres; öğrenciyle yakınlığı |
| Veli | Hesap | Veli uygulaması hesabı (e-posta, şifrenin özeti) |
| Antrenör ve okul yöneticisi | Kimlik, iletişim, hesap | Ad, soyad, e-posta, telefon, şifrenin özeti |
| Tüm kullanıcılar | İşlem güvenliği | Giriş kayıtları; kişisel verilere erişim kayıtları (kim, ne zaman, hangi kayıt; değerler değil) |

Ödeme kartı bilgileri Okul'a veya Spor Akademi'ye ulaşmaz; online ödemede doğrudan ödeme kuruluşuna girilir.

## 3. İşleme amaçları

- Öğrenci kaydının, grup ve antrenman programının yürütülmesi,
- Yoklamanın alınması ve velinin bilgilendirilmesi,
- Aidatların takibi, ödemelerin alınması ve iadesi, yasal muhasebe yükümlülüklerinin yerine getirilmesi,
- Acil durumda ilgili kişiye ulaşılması,
- Spor faaliyetine katılım için sağlık uygunluğunun belgelenmesi,
- Veli ve antrenörlere duyuru ve bildirim gönderilmesi,
- Bilgi güvenliğinin sağlanması ve kişisel verilere erişimin denetlenmesi.

## 4. Hukuki sebepler

- Sözleşmenin kurulması ve ifası (KVKK md. 5/2-c): kayıt, program, yoklama, aidat ve iletişim bilgileri.
- Hukuki yükümlülük (md. 5/2-ç): ödeme ve muhasebe kayıtlarının yasal süre boyunca saklanması.
- Meşru menfaat (md. 5/2-f): erişim kayıtları ve bilgi güvenliği, acil durum kişisi.
- Sağlık verisi ve fotoğraf için açık rıza (md. 6/2) [hukuki teyit: sağlık raporu bir mevzuat gereği isteniyorsa md. 6/3
  kapsamındaki istisna değerlendirilmelidir].

## 5. Aktarım

Kişisel veriler, yukarıdaki amaçlarla sınırlı olarak:

- Barındırma ve yedekleme hizmeti sağlayıcısına [sağlayıcı ve konum ADR-0014 ile kesinleşecek; yurt dışında
  barındırılırsa KVKK md. 9 koşulları sağlanır],
- E-posta gönderim hizmeti sağlayıcısına (yalnız bildirim e-postaları için ad ve e-posta adresi),
- Online ödeme kuruluşuna (yalnız ödeme işlemi için gereken bilgiler),
- Yasal olarak yetkili kamu kurum ve kuruluşlarına (talep hâlinde)

aktarılabilir. Veriler pazarlama amacıyla üçüncü kişilerle paylaşılmaz.

## 6. Toplama yöntemi

Veriler; kayıt sırasında veliden veya yetişkin öğrenciden alınarak Okul yöneticisi tarafından Spor Akademi'ye
girilir; yoklama antrenör tarafından, ödemeler Okul tarafından veya online ödeme ile elektronik ortamda toplanır.

## 7. Saklama süreleri

- Öğrencinin ve velinin kişisel verileri, öğrencinin ayrılmasından sonra Okul'un belirlediği süre sonunda
  ([varsayılan: 24 ay]) otomatik olarak anonimleştirilir.
- Ödeme kayıtları (tutar, dönem, ödeme tarihi, yöntem) yasal süre boyunca (10 yıl) saklanır. Öğrencinin verileri
  anonimleştirildiğinde bu kayıtların öğrenciyle bağı isimsiz hâle gelir; ancak Okul'un bir ödemeyi iptal ederken veya
  iade ederken yazdığı açıklamalar serbest metindir ve silinmez. Okul bu açıklamalara kişisel veri yazmamaya özen
  gösterir.
- Kişisel verilere erişim kayıtları 2 yıl saklanır, sonra silinir; bu kayıtlar verinin kendisini içermez.
- Silme talebiniz saklama süresinin dolmasını beklemeden karşılanır (Bölüm 8).

## 8. Haklarınız (KVKK md. 11)

Okul'a başvurarak:

- Kişisel verilerinizin işlenip işlenmediğini öğrenme, işlenmişse bilgi talep etme,
- İşlenme amacını ve amacına uygun kullanılıp kullanılmadığını öğrenme,
- Aktarıldığı üçüncü kişileri bilme,
- Eksik veya yanlış işlenmişse düzeltilmesini isteme,
- KVKK md. 7 çerçevesinde silinmesini veya yok edilmesini isteme,
- Düzeltme ve silme işlemlerinin aktarılan üçüncü kişilere bildirilmesini isteme,
- Münhasıran otomatik sistemlerle analiz edilmesi sonucu aleyhinize bir sonuç çıkmasına itiraz etme,
- Kanuna aykırı işleme sebebiyle zarara uğramanız hâlinde zararın giderilmesini talep etme

haklarına sahipsiniz. Verilerinizin bir kopyasını makinece okunabilir biçimde (JSON) isteyebilirsiniz.

## 9. Başvuru

Başvurunuzu yazılı olarak [Okul adresi] adresine veya [kvkk@ornek-spor.example] adresine, kimliğinizi doğrulayan
bilgilerle iletebilirsiniz. Okul, başvurunuzu en geç 30 gün içinde ücretsiz olarak sonuçlandırır. Reşit olmayan
öğrenciler adına başvuruyu velisi yapar.

*Son güncelleme: [gg.aa.yyyy]*
