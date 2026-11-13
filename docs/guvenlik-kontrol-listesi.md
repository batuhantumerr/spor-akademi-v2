# MVP güvenlik kontrol listesi (P1-37)

Kapsam: NFR-SEC-01 (OWASP ASVS 4.0.3 Seviye 1), NFR-SEC-03 – NFR-SEC-12, NFR-PRV-03. Her madde için repodaki kanıt (dosya veya test) ya da açık bir eksik yazılıdır. Eksikler "Fikirler" bölümünde `type:idea` adayı olarak listelenir; issue açılmadı.

Durum: ✅ karşılanıyor (otomatik test veya CI ile) · 🟡 kısmen / elle doğrulanıyor · ⛔ eksik (Deploy bloğu veya sonraki faz) · — MVP'de kapsam dışı.

Test sınıfları `tests/` altındaki yollarıyla verilir (`IntegrationTests/...` = `tests/SporAkademi.IntegrationTests/...`).

## 1. Fonksiyonel olmayan gereksinimler

| ID | Gereksinim | Durum | Kanıt / not |
| --- | --- | --- | --- |
| NFR-SEC-02 | Sadece HTTPS, HSTS, TLS ≥ 1.2 | ⛔ | Uygulama TLS'i reverse proxy'ye bırakır; HTTPS yönlendirmesi ve HSTS Deploy bloğunda proxy'de yapılacak. Refresh cookie'si zaten `Secure` (`IntegrationTests/Identity/LoginTests`). |
| NFR-SEC-03 | Şifre saklama (PBKDF2, salt) | ✅ | `src/SporAkademi.Infrastructure/Identity/Credentials.cs` (`PasswordHasher<T>`); `IntegrationTests/Security/PasswordStorageTests` hash'in V3 biçiminde (PBKDF2-HMAC-SHA512, ≥100.000 tekrar, ≥16 bayt salt) olduğunu ve aynı şifrenin farklı hash ürettiğini doğrular. Eski parametreli hash girişte yenilenir (`Application.UnitTests/Identity/IdentityHandlerTests`). |
| NFR-SEC-03 | Şifre ve hash asla dönülmez/loglanmaz | ✅ | `PasswordStorageTests.No_response_schema_exposes_a_password_or_hash` OpenAPI dokümanındaki her yanıt şemasını tarar (tek istisna: bir kez dönen `temporaryPassword`). Log: `IntegrationTests/Security/LogPrivacyTests`. |
| NFR-SEC-04 | Şifre politikası (≥12, yaygın şifre listesi) | ✅ | `src/SporAkademi.Application/Identity/PasswordPolicy.cs`, `common-passwords.txt`; `Application.UnitTests/Identity/PasswordPolicyTests`, `IntegrationTests/Identity/PasswordTests`. Aynı şifreye değiştirme reddedilir (`Auth.PasswordUnchanged`, `AuthHardeningTests`). |
| NFR-SEC-05 | Token süreleri (15 dk / 7 gün rotation / mutlak 30 gün) | ✅ | `IntegrationTests/Identity/SessionTests`, `LoginTests` (saat ilerletilerek, DEC-107); reuse detection `SessionTests`, `Domain.UnitTests/Identity/RefreshTokenTests`. Kaybolan yanıt için 30 sn tolerans (DEC-446): rotasyondan sonraki 30 sn içinde, halef hiç kullanılmamışsa eski token aynı ailede yeni bir token alır ve hiçbir şey iptal edilmez; pencere dışında ya da halef kullanılmışsa bütün oturumlar kapanır (`SessionTests`, `AuthHardeningTests`). |
| NFR-SEC-06 | Tarayıcıda token saklama | ✅ | Refresh token `HttpOnly; Secure; SameSite=Strict; Path=/api/v1/auth` (`LoginTests`); access token sadece yanıt gövdesinde, istemci bellekte tutar (`web/admin/src/app/core/auth/auth.service.ts`; veli uygulamasında `web/parent/src/auth/session.ts`, e2e `parent-mobile-chromium` Web Storage ve `document.cookie`'de JWT olmadığını doğrular, DEC-389). |
| NFR-SEC-07 | Varsayılan olarak yetki | ✅ | `tests/SporAkademi.ArchitectureTests/EndpointSecurityTests` (fallback policy + gözden geçirilmiş anonim endpoint listesi); `IntegrationTests/Platform/ErrorHandlingTests`. |
| NFR-SEC-08 | Tenant izolasyonu | ✅ | `ModuleRulesTests` (tenant'a bağlı aggregate'lar), `IntegrationTests/Tenancy/TenantIsolationTests` ve her modülün `*TenantIsolationTests` sınıfı; yazma koruması `TenantWriteGuardInterceptor`. Tam izolasyon paketi P1-35'te. |
| NFR-SEC-08 (RLS) | Veritabanında tenant izolasyonu (Row Level Security, P4-04) | ✅ | `tenant_id`'li her tabloda `ENABLE` + `FORCE ROW LEVEL SECURITY` ve `tenant_isolation` politikası (migration `AddRowLevelSecurity`, `src/SporAkademi.Infrastructure/Persistence/RowLevelSecurity.cs`); okul bağlamı her bağlantı açılışında `TenantSessionInterceptor` ile. `IntegrationTests/Tenancy/RowLevelSecurityTests`: filtresiz (`IgnoreQueryFilters`, ham SQL) başka okulun satırı okunamaz, eklenemez, değiştirilemez, taşınamaz; yeni `tenant_id`'li tablo politikasızsa test kırılır; uygulama rolünde `BYPASSRLS` yok; havuzdaki bağlantı önceki okulu taşımaz. İstisnalar DEC-521 – DEC-523. |
| NFR-SEC-09 | Repoda / imajda / logda gizli bilgi yok | 🟡 | **Repo (otomatik):** CI `repo-checks` → gitleaks varsayılan kurallarıyla tüm geçmiş taraması (DEC-200). İzin listesi (`.gitleaks.toml`, DEC-250) her sentetik test değerini sadece onu tutan dosyalarda ve sadece `generic-api-key` kuralı için kabul eder (yol **ve** değer eşleşmeli); yol glob'u veya repo genelinde geçerli değer kalıbı yok. gitleaks'in tanımadığı biçimdeki bir sır yakalanmaz. `.env` git-ignored ve hiçbir betikçe yazdırılmaz (`scripts/lib/env.sh`). **İmaj (elle):** Dockerfile'lar gizli bilgi kopyalamaz, yapılandırma ortam değişkeninden gelir (`docker/*/Dockerfile`); otomatik bir imaj sır taraması iddia edilmez. **Log:** `LogPrivacyTests`'in kapsadığı yollar (NFR-PRV-03). |
| NFR-SEC-10 | Veritabanı yetkileri (uygulama ≠ migrator) | ✅ | `docker/postgres/initdb/10-create-roles.sh`; CI `images` smoke testi uygulama rolünün migration geçmişine erişemediğini ve tüm modül tablolarını kullanabildiğini doğrular; `DatabaseFixture` testleri uygulama rolüyle çalıştırır. |
| NFR-SEC-11 | Bağımlılık açıkları | ✅ | CI: NuGet audit `NuGetAuditMode=all` + high/critical hata (`Directory.Build.props`, DEC-080), `npm audit --audit-level=high` (`frontend` işi), Trivy imaj taraması (`images` işi), Dependabot. |
| NFR-SEC-12 | Girdi sınırları (1 MB, sınırda doğrulama) | ✅ | Kestrel limiti + `RequestSizeLimitMiddleware`; `ErrorHandlingTests.Bodies_over_one_megabyte_are_rejected`; FluentValidation decorator'ı (`Decorators.cs`, `PipelineTests`). |
| NFR-SEC-13 | Güvenlik başlıkları (CSP vb.) | ⛔ | Faz 4. Şimdilik sadece `Cache-Control: no-store` (`NoStoreMiddleware`, `AuthHardeningTests`). Bkz. Fikirler 2. |
| NFR-SEC-14 | Rate limiting (giriş) | ✅ | `AuthRateLimiting.cs` (giriş, refresh, şifre değiştirme; IP + yol başına 10/dk), hesap kilidi (DEC-010); `LoginTests`, `AuthHardeningTests`. Proxy arkasında forwarded header Deploy bloğunda. |
| NFR-PRV-03 | Loglarda kişisel veri yok | 🟡 | Test edilen yollar için ✅, genel iddia değil. `LogPrivacyTests` (DEC-201) tüm yapılandırılabilir log seviyeleri en ayrıntılıyken (ASP.NET Core ve EF Core için de `Verbose` istenir) çalışır: (1) giriş (hatalı/başarılı), refresh, kişisel verili öğrenci kaydı, doğrulama hatası, arama, grup/aidat/kayıt, ödeme ve serbest metinli iptal gerekçesi, borç listesi ve şifre değiştirme; (2) **500 yolu:** gövdesinde, sorgu dizesinde, cookie'sinde ve token'ında kişisel veri olan bir istek gerçek bir veritabanı hatasıyla (e-posta unique ihlali) düşer; loglanan exception ve ProblemDetails yanıtı bunların hiçbirini içermez (Npgsql `Include Error Detail` kapalı; açılınca test kırmızı). Her iki testte de olayların gerçekten loglandığı (ör. `Unhandled exception` + `23505`) doğrulanır. ASP.NET Core istek logları (`Request starting ...?search=`) yapılandırmayla açılamaz: `Logging.cs` `Microsoft.AspNetCore` ve EF Core'u kodda `Warning`'e sabitler, test bunu doğrular. Kapsam dışı: yoklama ve duyuru uç noktaları (Fikirler 9) ve geliştiricinin kendi yazdığı exception mesajları (kod incelemesiyle). Decorator'lar sadece istek tipi ve hata kodu loglar (`Decorators.cs`). |
| DEC-054 | Aynı origin, CORS yok | ✅ | `IntegrationTests/Security/CorsTests`: CORS servisi kayıtlı değil; yabancı origin'den gelen istek ve preflight'larda hiçbir `Access-Control-*` başlığı yok. **Pozitif kontroller:** CORS yabancı origin için açılmış bir test host'unda aynı kontrol preflight'ta (`204`) ve gerçek istekte `Access-Control-Allow-Origin` başlığını bulur (negatif test boşa geçemez); admin uygulamasının kendi origin'inden (`Origin` = API origin'i) giriş ve `GET /me` normal yanıt alır. Aynı origin'de tarayıcı preflight göndermez, bu yüzden "izinli admin origin'i" için ayrı bir CORS politikası yoktur. |

## 2. OWASP ASVS 4.0.3 Seviye 1

Sadece bu uygulamaya uygulanabilen maddeler listelenir (SOAP, GraphQL, OAuth/OIDC sağlayıcısı yok; dosya yükleme P2-F6'dan, WebSocket/SignalR P3-08'den beri var).

### V1 Mimari (L1'de sadece kod kalitesi beklentileri)
| Madde | Durum | Kanıt / not |
| --- | --- | --- |
| Katman ve modül sınırları | ✅ | `LayerDependencyTests`, `ModuleRulesTests`. |

### V2 Kimlik doğrulama
| Madde | Durum | Kanıt / not |
| --- | --- | --- |
| 2.1.1 Şifre ≥ 12 karakter | ✅ | `PasswordPolicy.MinLength = 12` (NFR-SEC-04, DEC-369); `PasswordPolicyTests`, `PasswordTests`. |
| 2.1.2 – 2.1.4 Uzun şifreler, kırpılmaz, her karakter | ✅ | En fazla 128 karakter, kırpma ve karakter sınıfı kuralı yok (`PasswordPolicy`, `PasswordPolicyTests`). |
| 2.1.5 – 2.1.6 Kullanıcı şifresini değiştirebilir, mevcut şifre istenir | ✅ | `POST /auth/change-password`; `PasswordTests`. |
| 2.1.7 İhlal edilmiş/yaygın şifre kontrolü | 🟡 | Gömülü yaygın şifre listesi (Türkçe olanlar dahil); büyük bir ihlal listesine (k-anonymity) karşı kontrol yok. Fikirler 4. |
| 2.1.9 Karakter türü kuralı yok | ✅ | `PasswordPolicy`. |
| 2.2.1 Otomatik saldırılara karşı koruma | ✅ | Hesap kilidi 15 dk'da 5 hata (`LoginLockoutTests`, `LoginTests`), şifre değiştirmede de sayılır (`AuthHardeningTests`); IP rate limit. |
| 2.2.2 Zayıf kimlik doğrulayıcı (SMS/e-posta) | — | Yok; MVP'de e-postayla sıfırlama yok (admin sıfırlar, US-05). |
| 2.3.1 Geçici şifre rastgele, ilk girişte değiştirilir | ✅ | `TemporaryPassword` (CSPRNG, 16 karakter), `MustChangePassword` + `PasswordChangeRequiredMiddleware`; `PasswordTests`, `CoachTests`, `SchoolTests`. |
| 2.3.1, 2.5.x Veli daveti (P3-02) | ✅ | Veli hesabı kimsenin bilmediği rastgele şifreyle açılır; ilk şifre 72 saat geçerli, tek kullanımlık, sadece SHA-256 hash'i saklanan davet bağlantısıyla belirlenir; yeniden gönderme ve iptal önceki bağlantıyı hemen geçersiz kılar; kabul `password-reset/confirm` ile aynı rate limit'le (`ParentInvitationTests`, DEC-383). |
| 2.4.1 Şifreler tuzlu ve yavaş hash ile | ✅ | NFR-SEC-03. |
| 2.5.1 – 2.5.4 Kurtarma | 🟡 | Sadece admin sıfırlaması; varsayılan hesap yok (ilk Platform Admin seed'i zorunlu değişimle, `PlatformAdminSeedTests`). |
| 2.7 – 2.9 OTP / MFA | — | MVP'de yok; Fikirler 5. |
| Kullanıcı sayımı engeli | ✅ | Bilinmeyen e-posta ile yanlış şifre aynı yanıt + benzer süre (`LoginTests`, `IdentityHandlerTests`, DEC-115). |

### V3 Oturum yönetimi
| Madde | Durum | Kanıt / not |
| --- | --- | --- |
| 3.1.1 Oturum token'ı URL'de yok | 🟡 | Access token `Authorization` başlığında, refresh token cookie'de. **İstisna (P3-08, DEC-399):** tarayıcı WebSocket'i başlık taşıyamadığı için SignalR token'ı `access_token` sorgu parametresinde gönderir; API bunu yalnız `/hubs/notifications` yolunda ve `Authorization` başlığı yokken kabul eder (`/api`'de yok sayılır, `RealtimeNotificationTests`), Serilog'da her olayda maskelenir, URL-kodlu biçimi ve SignalR bağlantı token'ı (`?id=`) dahil (`AccessTokenRedaction`, `LogPrivacyTests.The_hub_access_token_*`, `Hub_secrets_are_masked_in_any_text`; DEC-405). Token 15 dakikalıktır. Açık nokta: Deploy bloğunda Caddy erişim logundan `access_token` silinmeli. |
| 3.2.1 – 3.2.3 Yeni token girişte, ≥ 64 bit entropi, güvenli saklama | ✅ | Refresh token 256 bit CSPRNG, DB'de SHA-256 hash (`Credentials.cs`, `LoginTests`). |
| 3.3.1 Çıkışta oturum biter | ✅ | `SessionTests.Logout_*`, `AuthHardeningTests.Logout_clears_the_cookie_even_when_revoking_fails`. |
| 3.3.2 Mutlak süre | ✅ | 30 gün (`SessionTests`). |
| Çok sekmede refresh ve açık sekmenin oturumu (P3-08) | ✅ | Sekmelerin refresh'i `navigator.locks` (`sa-refresh`) ile sıraya girer, aynı anda refresh yeniden kullanım tespitiyle bütün oturumları kapatmaz; gizli sekme realtime için refresh etmez. Açık sekme oturumu mutlak 30 güne kadar uzatabilir (bilinçli kabul, DEC-405). Admin açılışı yalnız `401`/`403`'te oturumsuz açılır, geçici hatada bir kez yeniden dener ve 5 sn beklemede refresh isteğini iptal etmez (DEC-447). Refresh sonrası token'ın `sub`'ı oturumdaki kullanıcıdan farklıysa oturum biter; eski oturum döneminde gönderilmiş isteğin `401`'i yeni oturumu etkilemez (DEC-447). |
| 3.4.1 – 3.4.3 Cookie `Secure`, `HttpOnly`, `SameSite` | ✅ | `LoginTests.Valid_credentials_*`. |
| 3.5.2 – 3.5.3 Token imza/süre doğrulaması | ✅ | HS256, issuer/audience/lifetime, tutarsız rol/tenant reddi (`JwtSetup.cs`, `ErrorHandlingTests.Invalid_tokens_are_rejected`). |
| 3.7.1 Hassas işlemde yeniden doğrulama | ✅ | Şifre değişimi mevcut şifreyi ister. |
| İş paneli oturumu (`/jobs`, DEC-332) | ✅ | Platform Admin'in bearer çağrısıyla (`POST /api/v1/platform/jobs/dashboard-session`) üretilen 256 bit anahtar, sunucuda SHA-256; çerez `HttpOnly; Secure; SameSite=Strict; Path=/jobs`, sabit 15 dk, yeni oturum eskisini bitirir; çıkış, şifre değişikliği/sıfırlama ve token yeniden kullanımı panel oturumunu da bitirir; süresi dolan/sahte çerez `401` (`IntegrationTests/Jobs/JobsDashboardTests`). Oturumlar süreç belleğinde (çok örnekli kurulum Deploy bloğunda); üretim nginx'i `/jobs`'u yönlendirmez, panel Deploy bloğuna kadar sadece dev proxy'siyle çalışır. |
| Pasif kullanıcının açık access token'ı | 🟡 | En fazla 15 dk geçerli (DEC-056, kabul edilen sınır). |

### V4 Erişim kontrolü
| Madde | Durum | Kanıt / not |
| --- | --- | --- |
| 4.1.1 – 4.1.3 Sunucu tarafında, en az yetki, varsayılan red | ✅ | Fallback policy, rol grupları (`EndpointConventions.RequireRole`), `EndpointSecurityTests`. |
| 4.2.1 IDOR / başka okulun kaydı | ✅ | Tenant filtresi + bileşik FK + yazma koruması; başka okulun kaydı `404` (her modülün `*TenantIsolationTests` sınıfı). |
| 4.2.2 CSRF | ✅ | Bearer token cookie'de değil; cookie ile çalışan refresh/çıkış `SameSite=Strict` + `X-Requested-With` ister (`SessionTests`), CORS yok (`CorsTests`). |
| İş paneli (Hangfire Dashboard, tüm okulların işleri) | ✅ | Sadece aktif Platform Admin, sadece panel çereziyle: anonim, bearer token, Okul Admini ve Antrenör `401`/`403`; kullanıcı her istekte yeniden kontrol edilir (devre dışı bırakılan admin anında düşer); Hangfire'ın kendi filtresi de rol ister; varsayılan salt okunur, işlemler açıksa antiforgery (`sa_jobs_af`, `X-Jobs-Antiforgery`); `no-store`, `X-Frame-Options: DENY`; bağlantı dizesi gösterilmez (`JobsDashboardTests`, `EndpointSecurityTests.The_job_panel_requires_its_platform_admin_policy`). İş argümanları sadece ID (`ArchitectureTests/JobsRulesTests`). |
| 4.3.1 Yönetim arayüzlerinde ek koruma | 🟡 | Platform Admin rolüyle ayrılmış; MFA yok (Fikirler 5). |
| Antrenör görünümü (NFR-PRV-02) | ✅ | Roster yanıt şekli sabit (`IntegrationTests/Enrollments/RosterTests`). |
| 4.1.3, 4.2.1 Veli sınırı (P3-01, ikinci yetki sınırı) | ✅ | `Parent` rolü sadece `/api/v1/parent/...` uç noktalarına açık; her uç nokta `IParentChildren` ile çocuğun çağıranın aktif çocuğu olduğunu ve hesabın aktif olduğunu doğrular, başka aile ve başka okul `404` (DEC-382, DEC-385). `IntegrationTests/Parents/ParentIsolationTests`: her veli uç noktası aynı okuldaki başka ailenin ve başka okulun ID'leriyle `404`, yazma yok, kendi listelerinde başka ailenin verisi ve acil durum kişisi yok, bütün Okul Admini/antrenör/Platform Admin uç noktaları veliye `403`; uç nokta listesi sabit (DEC-387). |
| Mevcut veliyi telefonla bağlamak | 🟡 | Okul Admini bir öğrenciye mevcut bir veliyi telefonla bağladığında (kardeş, US-13), o velinin veli hesabı yeni öğrenciyi de hemen görür. Bilinçli: veli hesabı veli kaydına bağlıdır. Okul Admininin doğru veliyi seçmesi gerekir; yanlış bağlantı veli çıkarılarak geri alınır (DEC-382, DEC-384). |
| Veli e-postasının değişmesi | ✅ | Değişen veya silinen e-posta bekleyen daveti ve aktif hesabı kapatır; yanlış adrese giden bağlantı düzeltmeden sonra çalışmaz (`ParentInvitationTests.Changing_*`, DEC-384). |
| Veli API'si maliyeti ve davet sıklığı | ✅ | Hesap başına dakikada 60 istek; borçlar yalnız ailenin kayıtlarını okur; aynı hesaba iki dakikada en fazla bir davet (`ParentInvitationTests`, DEC-388). |
| Veli verisinin en aza indirilmesi (NFR-PRV-01) | ✅ | Veli yanıtlarında acil durum kişisi, belgeler, diğer velilerin iletişim bilgisi, iptal edilen ödemeler ve iptal gerekçesi, iade gerekçesi (P3-13, DEC-423; `ParentIsolationTests`), duyuru yazarının ID'si, antrenörün iletişim bilgisi yok (`ParentApiTests`, DEC-385). |
| Online ödeme iadesi (P3-13) | ✅ | Yalnız Okul Admini: `POST /payments/{id}/refund`'da veli `403`, başka okulun ödemesi `404` alır (iki izolasyon paketinde). Para hareketi korumalı: yalnız tam iade; `Idempotency-Key` (aynı anahtar sağlayıcıyı tekrar çağırmaz); kayıt kilidi ve kısmi benzersiz index ile ödeme başına tek açık iade (paralel istek testleri); sağlayıcı çağrısı transaction dışında; belirsiz sonuç mutabakat işinde aynı iade ID'siyle yeniden gönderilir (DEC-416 – DEC-419). Veritabanı tetikleyicisi iade kaydını ve kesin sonucu değiştirilemez kılar, yalnız online ödeme `Refunded` olabilir. Gerekçe okulda kalır: veliye, sağlayıcıya, outbox'a ve loglara gitmez (`LogPrivacyTests`); sağlayıcıya yalnız ID ve tutar gider, IP yerine sabit yer tutucu (DEC-424). |
| 4.1.1, 4.2.1 Gerçek zamanlı bildirimlerde okul ve aile sınırı (P3-08) | ✅ | Hub grupları sunucuda token'dan atanır (`t:{okul}:u:{kullanıcı}`, `t:{okul}:r:{rol}`); istemci gruba katılamaz. Alıcılar gönderim anında hesaplanır: yoklama ve ödeme yalnız o çocuğun aktif veli hesaplarına, duyuru grubun antrenörüne, gruba bugün kayıtlı çocukların velilerine ve okulun Okul Adminlerine. `RealtimeNotificationTests`: A ailesinin velisi B'nin çocuğunun olayını, X grubunun antrenörü Y grubununkini, başka okulun admini ve başka okul token'ındaki aynı kullanıcı ID'si hiçbir şeyi almaz; iptal edilen veli hesabı token'ı yaşarken de almaz. Backplane'de aynı kural (`RealtimeBackplaneTests`). Yalnız outbox handler'ları bildirim gönderir (`NotificationRulesTests`), geri alınan değişiklik bildirim üretmez (DEC-396, DEC-398). |
| Veli hesabının kapanması | ✅ | Okul Admini iptali, son aktif çocuğun ayrılması ve velinin silinmesi hesabı aynı transaction'da kapatır, refresh token'ları iptal eder; iptal edilen hesap açık access token'la da hemen hiçbir çocuk görmez; silinen velinin hesabındaki kişisel veri silinir (`ParentAccountClosingTests`, `ParentInvitationTests`, DEC-384). |

### V5 Doğrulama, temizleme, kodlama
| Madde | Durum | Kanıt / not |
| --- | --- | --- |
| 5.1.1 – 5.1.4 Sınırda pozitif doğrulama, toplu atama yok | ✅ | Her command için FluentValidation; istek kayıtları (record) sadece izinli alanları içerir; value object'ler (`Domain.UnitTests/SharedKernel/*`). |
| 5.2 / 5.3 Çıktı kodlaması, XSS | ✅ | API sadece JSON döner; Angular şablonları varsayılan olarak kodlar (uygulama kodunda `innerHTML` / `bypassSecurityTrust*` yok). |
| 5.3.4 SQL injection | ✅ | EF Core parametreli sorgular; ham SQL sadece `ExecuteSqlAsync` interpolasyonuyla (parametreye dönüşür, `EnrollmentLocks`, `GroupLocks`, `PlatformAdminSeed`) ve sabit `SET LOCAL lock_timeout` metniyle. |
| 5.2 / 5.3 CSV (formül) enjeksiyonu | ✅ | P3-09 CSV dışa aktarmaları (DEC-411): `=`, `+`, `-`, `@`, sekme veya CR ile başlayan her hücreye `'` öneki, `;`/`"`/satır sonu içeren hücre tırnaklı ve tırnaklar ikili (`Application/Reports/ReportCsv.cs`). Kanıt: `Application.UnitTests/Reports/ReportCsvTests`, `IntegrationTests/Reports/ReportsTests.A_csv_cell_can_never_start_a_formula`. Dışa aktarma yalnız Okul Admini, hesap başına dakikada 10 istek, yalnız toplam veri (öğrenci adı yok), `no-store` ve `nosniff`. |
| 5.5 Deserialization | ✅ | System.Text.Json, polimorfik tip çözümü yok; para metin olarak (DEC-066). |

### V7 Hata yönetimi ve loglama
| Madde | Durum | Kanıt / not |
| --- | --- | --- |
| 7.1.1 – 7.1.2 Loglarda kimlik bilgisi/kişisel veri yok | 🟡 | `LogPrivacyTests`; kapsamı NFR-PRV-03'te. |
| 7.4.1 Genel hata mesajı, iz kimliği | ✅ | `ErrorHandlingTests.Unexpected_exceptions_become_a_bare_500_problem` (sadece `Unexpected.Error` + `traceId`); veritabanı hatasında da yanıt kişisel veri, constraint adı veya `duplicate key` içermez (`LogPrivacyTests.Unhandled_exceptions_log_no_personal_data`). |
| Güvenlik olaylarının loglanması (giriş hatası, kilit) | 🟡 | Hata kodları loglanıyor (`LoginCommand failed with Auth.InvalidCredentials`), ayrı bir denetim (audit) kaydı yok; Fikirler 6. |

### V8 Veri koruma
| Madde | Durum | Kanıt / not |
| --- | --- | --- |
| 8.2.1 Hassas veriler önbelleğe alınmaz | ✅ | `/api` yanıtlarında `Cache-Control: no-store` (`AuthHardeningTests.Api_responses_are_never_cached`). |
| 8.2.1 Veli uygulamasının service worker'ı kişisel veri önbelleğe almaz (P3-07) | ✅ | Worker yalnız build çıktısını (uygulama kabuğu, hash'li dosyalar, manifest, ikonlar) önbelleğe alır; `/api/*` (kimlik doğrulama dahil), `/health*`, GET dışı, başka origin'li, `Authorization`/`Range` başlıklı isteklere hiç cevap vermez ve saklamaz (`web/parent/sw/policy.test.ts`). CI `images` işi precache listesinde `/api`/`/health` olmadığını, e2e (`parent-mobile-chromium`) oturum sonrası önbelleklerde hiçbir API yolu olmadığını ve çevrimdışı yenilemede çocuğun verisinin görünmediğini doğrular (DEC-394, DEC-395). |
| 8.3.1 Hassas veri URL'de taşınmaz | 🟡 | Öğrenci araması isim/telefon parçasını sorgu dizesinde taşır (`?search=`); proxy erişim loglarında görünebilir. Fikirler 7. |
| 8.3.4 Kişisel veri envanteri | ⛔ | NFR-PRV-01 envanteri henüz yok; Fikirler 8. |

### V9 İletişim
| Madde | Durum | Kanıt / not |
| --- | --- | --- |
| 9.1.1 – 9.1.3 TLS | ⛔ | Deploy bloğu (NFR-SEC-02). |

### V10 Kötü amaçlı kod / tedarik zinciri
| Madde | Durum | Kanıt / not |
| --- | --- | --- |
| 10.3.2 Bağımlılık bütünlüğü | ✅ | NuGet lock dosyaları + `--locked-mode`, `npm ci`, GitHub Actions SHA ile, imajlar digest ile, Trivy ve gitleaks checksum ile sabit. |

### V12 Dosyalar ve kaynaklar
| Madde | Durum | Kanıt / not |
| --- | --- | --- |
| 12.1.1 (L1) Dosya boyutu sınırı; 12.1.3 (L2) kullanıcı başına kota | ✅ | P2-F6: türe göre 5 MB (rapor) / 2 MB (fotoğraf), istek sınırı yalnız yükleme uç noktasında 5 MB + 64 KB, diğer her yerde 1 MB (`RequestSizeLimitMiddleware`, `IRequestSizeLimitMetadata`); `IntegrationTests/Students/DocumentTests` (`Bodies_over_the_upload_limit...`, `jpeg-over-2mb`), `Application.UnitTests/Students/DocumentContentTests`. Öğrenci başına en fazla 20 rapor (DEC-322). |
| 12.2.1 (L2) İçerik türü doğrulaması (uzantıya/beyana güvenmeden) | ✅ | Magic bytes + yapısal ayrıştırma (`DocumentContent`); `Content-Type` yok sayılır; çift uzantı (`foto.jpg.html`) reddedilir; SVG/HTML/EXE/GIF reddedilir; görsellerde EXIF/GPS/XMP/metin chunk'ları ve dosya sonu sonrası baytlar silinir; etkin içerikli PDF (`/JavaScript`, `/Launch`, gömülü dosya; `#xx` kaçışlı adlar dahil) reddedilir (DEC-322). Testler: `DocumentContentTests`, `DocumentTests.Hostile_files_are_refused_and_nothing_is_stored`. Sınır: sıkıştırılmış PDF nesne akışları taranmaz, antivirüs yok (Fikirler 12). |
| 12.3.1, 12.3.2 (L1) Kullanıcı dosya adı yol/anahtar olarak kullanılmaz | ✅ | Nesne anahtarı `{okul}/{öğrenci}/{128 bit rastgele}`; dosya adı yalnız veritabanında, temizlenmiş; indirme adı ve imzalı URL kullanıcı adını içermez (DEC-322, DEC-323; `DocumentTests`). |
| 12.4.1 (L2) web kökü dışında, kısıtlı izinle saklama; 12.5.1, 12.5.2 (L1) yüklenen dosya HTML/JS olarak çalıştırılmaz; dosyalar herkese açık değil, yetkiyle ve süreli sunulur | ✅ | Silinen belgenin dosyası outbox ile silinir, hata yeniden denenir (`DocumentTests.A_failed_object_delete_is_retried...`, DEC-325). Bucket private; imzasız veya imzalı parametresi değiştirilmiş istek `403`; 5 dakikalık imzalı GET, süresi dolunca `403` (`DocumentTests.Download_links_...`). PDF her zaman `attachment`, `Content-Type` imzayla sabit (DEC-323). |
| 4.1.1, 4.2.1 (L1) Dosya erişim yetkisi, IDOR ve tenant izolasyonu | ✅ | Sağlık raporu yalnız Okul Admini; antrenör yalnız kendi aktif grubundaki öğrencinin fotoğrafı (DEC-324; `DocumentTests.A_coach_sees_only...`); başka okul `404` (`CrossTenantEndpointTests`, multipart probe ve depo anahtarlarıyla parmak izi). |
| Yüklenen dosya meta verisi loglanmaz | ✅ | `LogPrivacyTests` kişi adı içeren dosya adıyla yükleme ve reddedilen yüklemeyi kapsar; nesne silme outbox mesajı ve olası yetim nesne logu sadece üretilmiş anahtarı içerir (DEC-325). |
| 12.4.2 (L2) Yüklenen dosyalarda antivirüs taraması | ⛔ | Yok; sadece yapısal kontrol ve etkin içerik reddi (DEC-322). Fikirler 12. |

### V13 API
| Madde | Durum | Kanıt / not |
| --- | --- | --- |
| 13.1.3 URL'de anahtar/token yok | 🟡 | V3.1.1 (hub istisnası ve log maskelemesi dahil). |
| 13.2.1 – 13.2.3 Yöntem kısıtı, JSON şema, CSRF | ✅ | Rotalar sadece tanımlı yöntemleri kabul eder (`405`, `IntegrationTests/Security/ApiSurfaceTests`); OpenAPI sözleşme testi (`OpenApiContractTests`); CSRF V4.2.2. |
| 13.2.5 `Content-Type` kontrolü | ✅ | JSON gövdeli endpoint'ler başka içerik türünü `415` ile reddeder (`ApiSurfaceTests`). |
| 13.5 WebSocket / SignalR (P3-08) kimlik doğrulama ve oturum | ✅ | Hub yalnız geçerli JWT ile (`401` anonim, geçersiz ve süresi dolmuş token; Platform Admin ve şifre değiştirmesi gereken token `403`); bağlantı token süresi bitince sunucuca kapatılır (`CloseOnAuthenticationExpiration`), istemci taze token'la yeniden bağlanır (`RealtimeNotificationTests.A_hub_connection_needs_*`, DEC-399). Aynı origin, CORS yok (DEC-054); WebSocket upgrade'i Deploy bloğunda Caddy'de, imajlar `/hubs`'a `404` döner (`scripts/check-api-paths.sh`, DEC-402). |
| 13.5 WebSocket / SignalR girdi ve kaynak sınırları | ✅ | İstemci çağırabilen hub metodu yok; alınan mesaj ve tampon 4 KB; paralel çağrı 1; ayrıntılı hata kapalı; kullanıcı başına instance başına 10 bağlantı (testli); negotiate kullanıcı başına dakikada 30 (DEC-400). Bağlantı sayacı instance başına (çok instance'ta sınır instance başına). |
| 13.5 WebSocket mesajlarında veri en aza indirme | ✅ | Bildirim yalnız tür, ID, tarih ve durum taşır; isim, başlık, tutar, serbest metin yok; istemci ayrıntıyı yetkili REST uç noktalarından okur (`RealtimeNotificationTests`, DEC-397). |

### V14 Yapılandırma
| Madde | Durum | Kanıt / not |
| --- | --- | --- |
| 14.1 Tekrarlanabilir derleme, uyarılar hata | ✅ | `TreatWarningsAsErrors`, analyzer'lar, `dotnet format` CI'da. |
| 14.2.1 Bağımlılıklar güncel ve açıksız | ✅ | NFR-SEC-11. |
| 14.3.2 Hata ayıklama modu kapalı | ✅ | OpenAPI dokümanı sadece Development/Testing'de (`OpenApiSetup.cs`); production imajında geliştirici hata sayfası yok (`GlobalExceptionHandler`). |
| 14.3.3 Sürüm bilgisi sızmaz | 🟡 | nginx `server_tokens off` (`docker/admin/nginx.conf`); Kestrel `Server` başlığı kapatılmadı. Fikirler 2. |
| 14.4 Güvenlik başlıkları (CSP, nosniff, frame, Referrer-Policy) | ⛔ | NFR-SEC-13 (faz 4). Fikirler 2. |
| 14.5.3 CORS izin listesi | ✅ | CORS yok (`CorsTests`, pozitif kontrollerle). |
| Değişiklik denetimi (CI, imaj, betik, kaynak, ADR) | 🟡 | `.github/CODEOWNERS` güvenlikle ilgili yolları (`.github/`, `.gitleaks.toml`, `docker/`, `scripts/`, `src/`, `tests/`, `docs/adr/`, paket dosyaları) sahibe atar; ruleset'te code-owner incelemesinin zorunlu olup olmayacağı sahibin kararı (`pazartesi-onay-listesi.md`). |

## 3. Fikirler (`type:idea` adayları, issue açılmadı)

1. **HTTPS/HSTS ve forwarded header'lar (Deploy bloğu):** reverse proxy'de TLS 1.2+, HSTS, HTTP→HTTPS; API'de `UseForwardedHeaders` (sadece proxy'nin IP'si güvenilir), aksi halde rate limit tüm istemcileri proxy IP'siyle sayar.
2. **Güvenlik başlıkları (NFR-SEC-13, faz 4 öne alınabilir):** API ve admin imajında `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `frame-ancestors 'none'` / `X-Frame-Options: DENY`, admin için CSP; Kestrel `AddServerHeader = false`. Otomatik başlık testi.
3. **Şifre uzunluğu:** Yapıldı. Politika 12 karakter (DEC-369, sahibin 2026-11-10 onayı).
4. **İhlal edilmiş şifre kontrolü:** k-anonymity ile HIBP benzeri bir servis veya daha büyük bir gömülü liste (çevrimdışı çalışmalı, KVKK açısından şifrenin kendisi dışarı çıkmamalı).
5. **MFA (Platform Admin ve Okul Admini için TOTP):** faz 4 adayı.
6. **Güvenlik denetim kaydı:** başarısız giriş, kilit, şifre değişimi/sıfırlama, pasifleştirme için ayrı, kişisel veri içermeyen audit event'leri ve Seq/alarm.
7. **Arama terimi sorgu dizesinde:** `GET /students?search=` isim/telefon parçası taşır; proxy erişim loglarında sorgu dizesi maskelenmeli veya arama `POST` gövdesine taşınmalı.
8. **Kişisel veri envanteri (NFR-PRV-01):** her kişisel veri alanını, amacını ve saklama süresini listeleyen doküman; faz geçişinde gözden geçirilir.
9. **Log gizliliği testinin kapsamı:** ödeme, aidat, kayıt, iptal gerekçesi ve 500 yolu artık `LogPrivacyTests`'te; yoklama (not alanı) ve duyurular (serbest metin) henüz yok, teste eklenmeli.
11. **Peşin ödemeli kaydın erken bitirilmesi:** Yapıldı (P3-13, DEC-422). Bitirme engellenmiyor; kayıt bitirme penceresi bitişten sonraki aylara ait ödemeleri listeleyip iadeyi hatırlatıyor, online ödemeler sağlayıcı üzerinden iade ediliyor (DEC-416 – DEC-419).
12. **Yüklenen dosyalarda antivirüs ve derin PDF denetimi (P2-F6):** ClamAV (GPL-2.0, ayrı servis olarak) veya yönetilen tarama; sıkıştırılmış nesne akışlarının açılarak taranması; gerekirse PDF'lerin rasterize edilip yeniden üretilmesi. CSP gelince `img-src`'ye depo origin'i eklenmeli (fotoğraf küçük resmi doğrudan imzalı URL'den yüklenir).
13. **Yükleme ile commit arasında çökme (P2-F6):** nesne depoya yazıldıktan sonra süreç metadata commit'inden önce çökerse nesne yetim kalır (DEC-325 istisnası). Ara sıra çalışan bir temizlik işi (P2-F2 Hangfire ile) `students.student_documents`'ta karşılığı olmayan ve belirli bir yaştan eski anahtarları silmeli.
14. **Yükleme bellek kullanımı (P2-F6):** form bellekte tamponlanır (DEC-321); eşzamanlı N yükleme ~N × 5 MB bellek kullanır. Yükleme uç noktasına eşzamanlılık/oran sınırı (ör. okul başına) veya akış hâlinde inceleme değerlendirilmeli.
10. **Refresh token yarışı:** aynı token'la eşzamanlı iki sekme birini `409` ile düşürür (DEC-119); kısa bir tolerans penceresi değerlendirilebilir.
