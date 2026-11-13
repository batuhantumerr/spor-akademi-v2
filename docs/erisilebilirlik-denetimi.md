# WCAG 2.2 AA erişilebilirlik denetimi (P3-11)

Tarih: 2026-11-12. Kapsam: yönetim paneli (`web/admin`, Okul Admini ve antrenör) ve veli uygulaması (`web/parent`). Kararlar DEC-436 – DEC-443 ([karar günlüğü](karar-gunlugu-v2.md)). Gereksinim: NFR-USA-03 (WCAG 2.2 AA, denetim raporu).

Kısa sonuç: listelenen bütün önemli ekranlarda, iki dilde, iki temada, masaüstü ve 360 px genişlikte axe'ın WCAG 2.0/2.1/2.2 A ve AA kurallarından **serious veya critical bulgu yok**; betikli klavye, reflow ve metin aralığı kontrolleri de geçiyor. Denetim sırasında bulunan 23 sorun düzeltildi (bölüm 4). Açık kalanlar küçük ve gerekçeli (bölüm 5); en önemlisi gerçek ekran okuyucuyla elle yapılacak kontrol, sahibe bırakıldı.

## 1. Yöntem

1. **Ekran listesi** (bölüm 2) gerçek veriyle açılır. Veri API üzerinden, açılan oturumun kendi token'ıyla hazırlanır (sentetik, `.test` alan adı). Okul Admini için tek giriş, antrenör için tek giriş (geçici şifre → şifre değiştirme), veli için tek giriş (davet e-postası → şifre belirleme) yapılır. Girişler IP başına sınırlı olduğu için (NFR-SEC-14) bütün ekranlar aynı oturumla gezilir.
2. **Sekiz varyant:** her ekran Türkçe ve İngilizce, açık ve koyu tema, masaüstü (1280×800) ve telefon (360×780) olarak taranır. Dil uygulamanın kendi seçicisiyle, tema `prefers-color-scheme` emülasyonuyla (uygulamaların varsayılanı "sistem"), genişlik sayfa yeniden açılmadan değiştirilir. Böylece dil ve tema değişiminin kendisi de sınanmış olur.
3. **axe** (`@axe-core/playwright`), etiketler `wcag2a`, `wcag2aa`, `wcag21a`, `wcag21aa`, `wcag22aa`. Kontrast (1.4.3) her iki temada axe'ın `color-contrast` kuralıyla, hedef boyutu (2.5.8) `target-size` kuralıyla ölçülür. Bütün etkiler (minor, moderate, serious, critical) rapora yazılır.
4. **Betikli kontroller** (axe'ın göremediği kriterler, [`e2e/support/wcag.ts`](../web/admin/e2e/support/wcag.ts)):
   - **Klavye ve görünür odak (2.1.1, 2.4.3, 2.4.7, 2.4.11):** sayfa başından Tab ile bütün odak durakları gezilir (masaüstü ve 360 px). Her durakta öğenin (görsel olarak gizli bir radyo düğmesinde etiketinin) stili odaklı ve odaksız hâlde karşılaştırılır; değişiklik yoksa bulgudur. Geçişler bu sırada kapatılır, yoksa yavaş beliren halka "değişiklik yok" diye okunur. Öğenin merkezi ve dört köşesi `elementFromPoint` ile yoklanır: tamamı başka bir öğenin (yapışkan başlık, alt sekme çubuğu, kaydet çubuğu) altındaysa 2.4.11 bulgusudur, bir kısmı örtülüyse yalnızca not düşülür (2.4.12, AAA). Odak başladığı yere dönünce tur biter (klavye tuzağı yok).
   - **Reflow (1.4.10):** 320×640'ta belge ve admin'in kaydırılan `main` alanı yatay taşmamalı. Kendi kutusunda kayan veri tabloları istisnadır.
   - **Metin aralığı (1.4.12):** 320 px'te kriterin değerleri (`line-height` 1,5; harf 0,12 em; kelime 0,16 em; paragraf sonu 2 em) zorla uygulanır. Ardından metin kesilmemeli ve sayfa taşmamalıdır.
   - **Odak yönetimi:** diyalog açılınca odak diyaloğa girer, Escape ile kapanınca açan düğmeye döner (grup formu, iade penceresi). Mobil menü açılınca odak kapat düğmesine geçer, kapanınca menü düğmesine döner. Sayfa değişince başlık duyurulur ve odak yeni sayfanın `main` alanına geçer. Form hatasında odak hata özetine gider ve her geçersiz alan hatasına `aria-describedby` ile bağlıdır.
5. **Tema token kontrastı:** [`scripts/check-contrast.mjs`](../scripts/check-contrast.mjs), iki temada 29 renk çiftini ölçer: metin 4,5:1, odak halkası, form kontrol kenarlığı ve grafik serileri 3:1. Yarı saydam zeminler kartın üzerine karıştırılarak hesaplanır. `repo-checks` job'unda çalışır.
6. **Koddan inceleme:** otomatik ölçülemeyen kriterler (2.5.7, 3.2.6, 3.3.7, 3.3.8, 3.1.2, 4.1.3, grafikler) iki uygulamanın kaynağından ve birim testlerinden değerlendirildi.

**CI eşiği (DEC-437):** testi yalnızca axe'ın serious ve critical bulguları ile betikli kontrollerin bulguları kırar. Bugün CI'ı kıran bir bulgu yok; moderate ve minor bulgular raporda görünür. Eşik yükseltilmedi: axe'ın WCAG etiketli moderate kuralları ya bu uygulamalarda tetiklenmiyor ya da (ör. `region`) best-practice etiketinde, sonuçlar her koşuda artefakt olarak görülebiliyor. Bir moderate bulgu ortaya çıkarsa önce rapora düşer, sonra karar verilir.

## 2. Ekranlar

Her biri 8 varyantta (TR/EN × açık/koyu × masaüstü/360 px) taranır. Klavye turu masaüstü ve 360 px'te, reflow ve metin aralığı 320 px'te yapılır.

| Uygulama | Ekran | Not |
| --- | --- | --- |
| Admin | Giriş, Şifremi unuttum | Oturumsuz |
| Admin | Panel | ECharts grafikleri ve veri tabloları |
| Admin | Mobil menü (çekmece) | Yalnız 360 px varyantları |
| Admin | Branşlar, Tesisler, Antrenörler | Okul kurulumu |
| Admin | Gruplar, grup formu diyaloğu, grup ayrıntısı | Haftalık program, kadro, duyurular, aidat |
| Admin | Öğrenciler, öğrenci ayrıntısı | Kayıtlar, yoklama, veliler, belgeler |
| Admin | Öğrenci kaydı (boş ve hatalı gönderim) | Hata özeti, alan hataları |
| Admin | Yoklama (aylık tablo) | |
| Admin | Ödemeler, iade diyaloğu | P3-13 iade penceresi |
| Admin | Ödenmemiş borçlar, Online ödemeler | |
| Admin | Raporlar: Devam, Doluluk, Gelir sekmeleri | P3-09; grafik, tablo ve CSV düğmesi |
| Admin | Ayarlar: Veri saklama, Aidat kuralları | |
| Admin (antrenör, telefon) | Şifre değiştirme (ilk giriş), Bugün, Yoklama al, Gruplarım, Kadro, Duyurular | |
| Veli | Şifremi unuttum, şifre belirleme (davet bağlantısı), Giriş | Oturumsuz |
| Veli | Çocuklarım, çocuk ayrıntısı | Aidat satırından online ödeme |
| Veli | Sandbox ödeme sayfası, ödeme sonucu | Fake sağlayıcı |
| Veli | Çevrimdışı durumu | Service worker kabuğu, ağ yok |

Toplam: admin 30 ekran ve 236 axe taraması, veli 8 ekran ve 64 axe taraması. Raporlar (P3-09) bu dal açıkken birleşti ve denetime eklendi.

## 3. Araçlar ve sürümler

| Araç | Sürüm |
| --- | --- |
| Playwright (`@playwright/test`) | 1.64.1, Chromium 1248 (masaüstü Chrome profili) |
| axe-core / `@axe-core/playwright` | 4.14.0 |
| Kontrast betiği | `scripts/check-contrast.mjs` (WCAG 2.x göreli parlaklık formülü) |
| Birim testleri | Angular (Vitest), veli uygulaması (Vitest + Testing Library) |

Elle ekran okuyucu (NVDA, VoiceOver, TalkBack) denetimi bu turda **yapılmadı**: agent'ın ortamında ekran okuyucu yok. Ekran okuyucu davranışı kodla ve erişilebilirlik ağacıyla değerlendirildi (rol, ad, canlı bölgeler). Gerçek cihazla kontrol sahibe bırakıldı (bölüm 5).

## 4. Sonuçlar (kriter bazında)

Durum: **Geçti** (bulgu yok), **Düzeltildi** (bu PR'da bulundu ve düzeltildi), **Açık** (bölüm 5), **Uygulanmaz** (ekranlarda karşılığı yok).

| Kriter | Durum | Kanıt / yapılan |
| --- | --- | --- |
| 1.1.1 Metin dışı içerik | Geçti | axe; ikonlar `aria-hidden`, ikon düğmelerinde `aria-label`. Grafiklerde `role="img"` ve özet, ayrıca açılır veri tablosu. |
| 1.3.1 Bilgi ve ilişkiler | Düzeltildi | Admin'in oturumsuz sayfalarında (giriş, şifremi unuttum, şifre sıfırlama, şifre değiştirme, 404) `main` işareti yoktu, eklendi. Formlarda `fieldset`/`legend` ve etiketler vardı. |
| 1.3.5 Girdi amacı | Geçti | Giriş ve şifre alanlarında `autocomplete` (`username`, `current-password`, `new-password`, `email`). |
| 1.4.1 Rengin tek başına kullanımı | Geçti | Yoklama seçiminde işaret ikonu ve kalın kenar; rozetlerde metin. |
| 1.4.3 Kontrast (metin) | Düzeltildi | Antrenörün "Gelmedi" seçimi açık temada 4,46:1'di (`bg-destructive/15`). Dört yoklama tonu `/10`'a indi (4,87:1 ve üstü). Token çiftleri `check-contrast.mjs` ile CI'da. |
| 1.4.4 Metni yeniden boyutlandırma | Geçti | Kök yazı boyutu yüzde ile (`87.5%`), kullanıcı ayarı ölçekler. Metin aralığı ve reflow kontrolleri taşma bulmadı. |
| 1.4.10 Reflow | Düzeltildi | Öğrenci ayrıntısı 320 px'te (aralık artınca) 330 px'e taşıyordu: dış ızgara `grid-cols-1` oldu. Panel grafikleri yeni genişliği alıyor. Veri tabloları kendi kutusunda kayar (istisna). Ödenmemiş borçlar listesinde, geniş tablonun hücrelerindeki görsel olarak gizli (sr-only) metin kaydırma kutusundan kaçıp sayfayı 334 px'e genişletiyordu; `.table-shell` ve admin `main` artık `relative`. |
| 1.4.11 Metin dışı kontrast | Düzeltildi | Form kontrol kenarlığı 1,2:1'di (`--border`). Yeni `--control-border` token'ı açık temada 3,6:1, koyu temada 3,4:1. Odak halkası %50 saydamdı (≈2,5:1); artık tam renk `ring` ve 2 px boşluk (açıkta 5,2:1, koyuda 6,4:1). Grafik serileri iki temada 3:1 üstünde. |
| 1.4.12 Metin aralığı | Düzeltildi | Panelde grup adları, kadroda öğrenci adı, tesis kartı başlığı ve belge adı `truncate` (üç nokta) ile kesiliyordu. Artık satır kırılıyor. |
| 1.4.13 Üzerine gelince açılan içerik | Geçti | Yalnız `title` ve ECharts ipuçları; ipucundaki bilgi veri tablosunda da var. |
| 2.1.1 Klavye / 2.1.2 Klavye tuzağı yok | Düzeltildi | Klavye turu her ekranda başa döndü. Telefonda yana kayan ve içinde odaklanacak öğe olmayan tablolar (ör. aidat kuralları sürümleri) klavyeyle kaydırılamıyordu (axe `scrollable-region-focusable`). Artık `ScrollRegions` böyle bir kutuyu tablonun başlığıyla adlandırılmış bir Tab durağı yapıyor; sığınca durak kalkıyor (DEC-442). Diyaloglar CDK odak tuzağıyla Escape'le kapanıyor. |
| 2.4.1 Blokları atlama | Düzeltildi | Admin kabuğunda "Ana içeriğe geç" bağlantısı yoktu, eklendi (ilk Tab durağı). Veli uygulamasında zaten vardı. |
| 2.4.2 Sayfa başlığı | Geçti | Her rotada "<sayfa> · Spor Akademi", dil değişince güncelleniyor. |
| 2.4.3 Odak sırası | Düzeltildi | (1) Sayfa değişince odak tıklanan menü bağlantısında kalıyordu: artık yeni sayfanın `main` alanına geçiyor (DEC-438). (2) Diyaloğu açan düğme, sayfa diyalog boyunca devre dışı tuttuğu için kapanışta odak alamıyordu ve odak sayfa başına düşüyordu: `DialogFocusRestorer` düğme yeniden etkinleşince odağı ona veriyor (DEC-441). (3) Mobil çekmece açılınca odak kapat düğmesine gidemiyordu: `.btn`'in `transition-all`'ı `visibility`'yi de geçişliyordu ve görünmez düğme odak almıyordu. Artık `transition` (DEC-441). (4) Veli uygulamasında dil değişimi sayfayı yeniden kurduğu için odak sayfa başına düşüyordu: artık dil seçicisine dönüyor (DEC-439). |
| 2.4.6 Başlıklar ve etiketler | Geçti | axe; her sayfada tek `h1`. |
| 2.4.7 Görünür odak | Düzeltildi | Odak halkası artık tam renk ve 2 px boşluklu; raporlar sekmelerinde de. Klavye turu her durakta görünür değişiklik buldu. Tek istisna yerel ay/tarih alanının Chromium'un çizdiği takvim düğmesi (bölüm 5). |
| 2.4.11 Odak gizlenmez (en az) | Düzeltildi | Veli uygulamasında yapışkan başlık ve alt sekme çubuğu için `scroll-padding`. Antrenörün sabit "kaydet" çubuğu için `main:has(.save-bar)` alt boşluğu. Admin bildirimleri (toast) odaklı öğenin üstüne gelebiliyordu: Escape artık bütün bildirimleri kapatıyor (diyalog açıkken Escape diyaloğundur). Klavye turunda tamamen örtülen durak yok. |
| 2.5.3 Addaki etiket | Düzeltildi | Veli uygulamasında İngilizce "Pay online" düğmesinin erişilebilir adı görünen metni içermiyordu ("Pay the … monthly fee online"). Ad artık "Pay online: …". |
| 2.5.7 Sürükleme hareketleri | Uygulanmaz | Sürükleyerek yapılan işlem yok (sıralama, kaydırıcı, grafik yakınlaştırma yok). |
| 2.5.8 Hedef boyutu (en az) | Düzeltildi | Admin bildirimlerinin kapat düğmesi 16×16 idi, 24×24 oldu. Öğrenci kaydı hata özetindeki bağlantılar 17,5 px yüksekliğindeydi, 24 px'in üstüne çıktı. axe `target-size` başka bulgu vermedi. |
| 3.1.1 Sayfanın dili | Geçti | `<html lang>` dil değişiminde hemen güncelleniyor (P3-10). |
| 3.1.2 Bölümlerin dili | Geçti | Dil seçici öbür dilin adını `lang` özniteliğiyle gösteriyor. Kişi ve grup adları özel addır. İngilizce arayüzde Türkçe kalan metin sözlük taramasıyla (`check-i18n.mjs`) engelleniyor. E-postalar ve PWA manifest'i bilerek Türkçe (DEC-431, DEC-428). |
| 3.2.1 / 3.2.2 Odakta ve girdide değişiklik | Geçti | Filtreler URL'yi güncelliyor ama odak filtrede kalıyor: rota duyurucusu yalnız yol değişiminde çalışır, birim testli. |
| 3.2.3 Tutarlı gezinme | Düzeltildi | Admin'de dil seçici yalnız giriş sayfasındaydı; e-posta bağlantısıyla açılan şifre sıfırlama, şifremi unuttum ve ilk girişteki şifre değiştirme sayfalarına da eklendi (DEC-439). |
| 3.2.6 Tutarlı yardım | Uygulanmaz | Uygulamalarda yardım mekanizması (iletişim, sohbet, SSS bağlantısı) yok. Eklenirse her sayfada aynı yerde olmalı (bölüm 5). |
| 3.3.1 Hata tanımlama / 3.3.3 Hata önerisi | Geçti | Hata özeti odak alıyor ve bağlantıları alanlara götürüyor. Her geçersiz alan `aria-invalid` ve `aria-describedby` ile hatasına bağlı (e2e'de kontrol ediliyor). |
| 3.3.2 Etiketler veya talimatlar | Düzeltildi | Öğrenci formunda yıldızın anlamı yazıyordu ama yıldızı anmıyordu; metin "Zorunlu alanlar * ile işaretlidir." oldu. |
| 3.3.7 Tekrarlı giriş | Geçti | Öğrenci kaydında acil durum kişisi veliden kopyalanabiliyor. Kayıtlı veli telefonla bulunup yeniden yazılmıyor. Çok adımlı süreçlerde aynı bilgi ikinci kez istenmiyor. |
| 3.3.8 Erişilebilir kimlik doğrulama (en az) | Düzeltildi | Bilişsel test (CAPTCHA, bulmaca) yok. Yapıştırma serbest, `autocomplete` ile parola yöneticisi çalışıyor, şifre göster düğmesi var. Düzeltme: veli davet ve sıfırlama sayfasında dil değiştirmek tek kullanımlık bağlantının token'ını kaybettiriyordu (sayfa yeniden kuruluyor, token adres çubuğundan silinmişti). Token artık o ziyaret boyunca bellekte tutuluyor (DEC-439). |
| 4.1.2 Ad, rol, değer | Geçti | axe. Diyaloglarda `aria-labelledby`, menü düğmesinde `aria-expanded`/`aria-controls`. |
| 4.1.3 Durum mesajları | Düzeltildi | Bildirimler: başarı ve bilgi tek `role="status"` canlı bölgede, hatalar `role="alert"`. Veli uygulamasının anlık bildirimleri `aria-live="polite"`. Eklenen: sayfa değişince yeni başlık kibar bir canlı bölgeyle duyuruluyor (iki uygulamada, DEC-438). |

Grafikler (panel ve raporlar): ECharts canvas'ı `role="img"` ve dil izleyen özetle sunuluyor; her grafiğin altında "Tabloyu göster" ile aynı verinin tablosu açılıyor. Seri renkleri iki temada karta karşı 3:1'in üstünde. Bilgi yalnız renge bağlı değil, eksen etiketleri ve tablo var.

## 5. Açık kalanlar

| # | Konu | Gerekçe | Takip |
| --- | --- | --- | --- |
| 1 | Gerçek ekran okuyucuyla elle kontrol (NVDA + Firefox/Chrome, VoiceOver iOS, TalkBack) | Agent ortamında ekran okuyucu yok. Erişilebilirlik ağacı ve canlı bölgeler test edildi, okunuş sırası ve tonlama edilmedi. | Sahip: Faz 3 geçişinden önce giriş, öğrenci kaydı (hata özeti), yoklama alma ve velinin online ödemesi için 30 dakikalık tur. |
| 2 | Yerel ay/tarih alanı (`input type="month"`, `date`) odak göstergesi | Alanın parçalarında stil değişiyor. Chromium'un kendi çizdiği takvim düğmesi durağı sayfa stilinden okunamıyor; betik bunu minor not olarak raporluyor. | Ayrı bir Chromium betiğiyle bakıldı: alanın ay ve yıl parçalarında `:focus-visible` stili uygulanıyor; takvim düğmesinin halkası tarayıcıya ait. Gerçek cihazda madde 1 ile birlikte bakılmalı. |
| 3 | Admin kenar çubuğu daraltma düğmesi masaüstünde kısmen başlığın altında | Yalnız 2.4.12 (AAA) notu, AA geçiyor (düğme görünür). | Düşük öncelik; kenar çubuğu tasarımı değişirse. |
| 4 | Şifre alanında "şifreyi göster" düğmesi alanın sağ ucunu örtüyor | Bilinçli tasarım, AAA notu. | — |
| 5 | Tutarlı yardım (3.2.6) | Yardım mekanizması yok. | Destek/iletişim bağlantısı eklenirse her sayfada aynı konumda olmalı. |
| 6 | Firefox ve WebKit'te axe | Matris yalnız Chromium'da. Mevcut okul yöneticisi ve antrenör e2e akışları Firefox/WebKit'te serious/critical kontrolünü zaten yapıyor. | Gerek görülürse matris başka tarayıcıya genişletilir (süre yaklaşık iki katına çıkar). |

## 6. Yeniden çalıştırma

```bash
docker compose up -d --wait            # PostgreSQL (bir kez)
bash scripts/e2e.sh --suite=a11y       # yalnız denetim (CI'daki a11y job'u, ~9 dk)
bash scripts/e2e.sh --suite=flows      # denetim hariç akışlar (CI'daki e2e job'u, ~6 dk)
bash scripts/e2e.sh                     # ikisi birden
node scripts/check-contrast.mjs --verbose   # tema token kontrastı, iki tema
```

Rapor her koşuda `web/admin/test-results/a11y/admin.md` ve `parent.md` (ayrıca `.json`) olarak yazılır, Playwright HTML raporuna eklenir. CI'da denetim ayrı `a11y` job'unda koşar (P4-11, DEC-591) ve raporu `a11y-report` artefaktı olarak her koşuda yükler. Yeni bir ekran eklemek için ilgili spec'e bir `test.step` ve `await audit.screen(page, '<ad>')` yeter. Seçenekler (`variants`, `keyboard`, `reflow`, `exclude`) [`wcag.ts`](../web/admin/e2e/support/wcag.ts)'de açıklanıyor.
