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
| NFR-SEC-04 | Şifre politikası (≥10, yaygın şifre listesi) | ✅ | `src/SporAkademi.Application/Identity/PasswordPolicy.cs`, `common-passwords.txt`; `Application.UnitTests/Identity/PasswordPolicyTests`, `IntegrationTests/Identity/PasswordTests`. Aynı şifreye değiştirme reddedilir (`Auth.PasswordUnchanged`, `AuthHardeningTests`). |
| NFR-SEC-05 | Token süreleri (15 dk / 7 gün rotation / mutlak 30 gün) | ✅ | `IntegrationTests/Identity/SessionTests`, `LoginTests` (saat ilerletilerek, DEC-107); reuse detection `SessionTests`, `Domain.UnitTests/Identity/RefreshTokenTests`. |
| NFR-SEC-06 | Tarayıcıda token saklama | ✅ | Refresh token `HttpOnly; Secure; SameSite=Strict; Path=/api/v1/auth` (`LoginTests`); access token sadece yanıt gövdesinde, istemci bellekte tutar (`web/admin/src/app/core/auth/auth.service.ts`). |
| NFR-SEC-07 | Varsayılan olarak yetki | ✅ | `tests/SporAkademi.ArchitectureTests/EndpointSecurityTests` (fallback policy + gözden geçirilmiş anonim endpoint listesi); `IntegrationTests/Platform/ErrorHandlingTests`. |
| NFR-SEC-08 | Tenant izolasyonu | ✅ | `ModuleRulesTests` (tenant'a bağlı aggregate'lar), `IntegrationTests/Tenancy/TenantIsolationTests` ve her modülün `*TenantIsolationTests` sınıfı; yazma koruması `TenantWriteGuardInterceptor`. Tam izolasyon paketi P1-35'te. |
| NFR-SEC-09 | Repoda / imajda / logda gizli bilgi yok | ✅ | CI `repo-checks` → gitleaks ile tüm geçmiş taraması (DEC-200, `.gitleaks.toml`); `.env` git-ignored ve hiçbir betikçe yazdırılmaz (`scripts/lib/env.sh`); imajlarda gizli bilgi yok (`docker/*/Dockerfile`, yapılandırma ortam değişkeninden). Loglar: `LogPrivacyTests`. |
| NFR-SEC-10 | Veritabanı yetkileri (uygulama ≠ migrator) | ✅ | `docker/postgres/initdb/10-create-roles.sh`; CI `images` smoke testi uygulama rolünün migration geçmişine erişemediğini ve tüm modül tablolarını kullanabildiğini doğrular; `DatabaseFixture` testleri uygulama rolüyle çalıştırır. |
| NFR-SEC-11 | Bağımlılık açıkları | ✅ | CI: NuGet audit `NuGetAuditMode=all` + high/critical hata (`Directory.Build.props`, DEC-080), `npm audit --audit-level=high` (`frontend` işi), Trivy imaj taraması (`images` işi), Dependabot. |
| NFR-SEC-12 | Girdi sınırları (1 MB, sınırda doğrulama) | ✅ | Kestrel limiti + `RequestSizeLimitMiddleware`; `ErrorHandlingTests.Bodies_over_one_megabyte_are_rejected`; FluentValidation decorator'ı (`Decorators.cs`, `PipelineTests`). |
| NFR-SEC-13 | Güvenlik başlıkları (CSP vb.) | ⛔ | Faz 4. Şimdilik sadece `Cache-Control: no-store` (`NoStoreMiddleware`, `AuthHardeningTests`). Bkz. Fikirler 2. |
| NFR-SEC-14 | Rate limiting (giriş) | ✅ | `AuthRateLimiting.cs` (giriş, refresh, şifre değiştirme; IP + yol başına 10/dk), hesap kilidi (DEC-010); `LoginTests`, `AuthHardeningTests`. Proxy arkasında forwarded header Deploy bloğunda. |
| NFR-PRV-03 | Loglarda kişisel veri yok | ✅ | `LogPrivacyTests` (DEC-201): giriş (hatalı/başarılı), refresh, kişisel verili öğrenci kaydı, doğrulama hatası, arama ve şifre değiştirme tüm log seviyeleri açıkken çalışır; hiçbir olayda e-posta, telefon, isim, doğum tarihi, şifre veya token geçmez. Decorator'lar sadece istek tipi ve hata kodu loglar (`Decorators.cs`). |
| DEC-054 | Aynı origin, CORS yok | ✅ | `IntegrationTests/Security/CorsTests`: CORS servisi kayıtlı değil; yabancı origin'den gelen istek ve preflight'larda hiçbir `Access-Control-*` başlığı yok. |

## 2. OWASP ASVS 4.0.3 Seviye 1

Sadece bu uygulamaya uygulanabilen maddeler listelenir (dosya yükleme, SOAP, GraphQL, OAuth/OIDC sağlayıcısı, WebSocket yok).

### V1 Mimari (L1'de sadece kod kalitesi beklentileri)
| Madde | Durum | Kanıt / not |
| --- | --- | --- |
| Katman ve modül sınırları | ✅ | `LayerDependencyTests`, `ModuleRulesTests`. |

### V2 Kimlik doğrulama
| Madde | Durum | Kanıt / not |
| --- | --- | --- |
| 2.1.1 Şifre ≥ 12 karakter | 🟡 | Politika ≥ 10 karakter (NFR-SEC-04, sahip kararı). ASVS 12 önerir; bkz. Fikirler 3. |
| 2.1.2 – 2.1.4 Uzun şifreler, kırpılmaz, her karakter | ✅ | En fazla 128 karakter, kırpma ve karakter sınıfı kuralı yok (`PasswordPolicy`, `PasswordPolicyTests`). |
| 2.1.5 – 2.1.6 Kullanıcı şifresini değiştirebilir, mevcut şifre istenir | ✅ | `POST /auth/change-password`; `PasswordTests`. |
| 2.1.7 İhlal edilmiş/yaygın şifre kontrolü | 🟡 | Gömülü yaygın şifre listesi (Türkçe olanlar dahil); büyük bir ihlal listesine (k-anonymity) karşı kontrol yok. Fikirler 4. |
| 2.1.9 Karakter türü kuralı yok | ✅ | `PasswordPolicy`. |
| 2.2.1 Otomatik saldırılara karşı koruma | ✅ | Hesap kilidi 15 dk'da 5 hata (`LoginLockoutTests`, `LoginTests`), şifre değiştirmede de sayılır (`AuthHardeningTests`); IP rate limit. |
| 2.2.2 Zayıf kimlik doğrulayıcı (SMS/e-posta) | — | Yok; MVP'de e-postayla sıfırlama yok (admin sıfırlar, US-05). |
| 2.3.1 Geçici şifre rastgele, ilk girişte değiştirilir | ✅ | `TemporaryPassword` (CSPRNG, 16 karakter), `MustChangePassword` + `PasswordChangeRequiredMiddleware`; `PasswordTests`, `CoachTests`, `SchoolTests`. |
| 2.4.1 Şifreler tuzlu ve yavaş hash ile | ✅ | NFR-SEC-03. |
| 2.5.1 – 2.5.4 Kurtarma | 🟡 | Sadece admin sıfırlaması; varsayılan hesap yok (ilk Platform Admin seed'i zorunlu değişimle, `PlatformAdminSeedTests`). |
| 2.7 – 2.9 OTP / MFA | — | MVP'de yok; Fikirler 5. |
| Kullanıcı sayımı engeli | ✅ | Bilinmeyen e-posta ile yanlış şifre aynı yanıt + benzer süre (`LoginTests`, `IdentityHandlerTests`, DEC-115). |

### V3 Oturum yönetimi
| Madde | Durum | Kanıt / not |
| --- | --- | --- |
| 3.1.1 Oturum token'ı URL'de yok | ✅ | Access token `Authorization` başlığında, refresh token cookie'de. |
| 3.2.1 – 3.2.3 Yeni token girişte, ≥ 64 bit entropi, güvenli saklama | ✅ | Refresh token 256 bit CSPRNG, DB'de SHA-256 hash (`Credentials.cs`, `LoginTests`). |
| 3.3.1 Çıkışta oturum biter | ✅ | `SessionTests.Logout_*`, `AuthHardeningTests.Logout_clears_the_cookie_even_when_revoking_fails`. |
| 3.3.2 Mutlak süre | ✅ | 30 gün (`SessionTests`). |
| 3.4.1 – 3.4.3 Cookie `Secure`, `HttpOnly`, `SameSite` | ✅ | `LoginTests.Valid_credentials_*`. |
| 3.5.2 – 3.5.3 Token imza/süre doğrulaması | ✅ | HS256, issuer/audience/lifetime, tutarsız rol/tenant reddi (`JwtSetup.cs`, `ErrorHandlingTests.Invalid_tokens_are_rejected`). |
| 3.7.1 Hassas işlemde yeniden doğrulama | ✅ | Şifre değişimi mevcut şifreyi ister. |
| Pasif kullanıcının açık access token'ı | 🟡 | En fazla 15 dk geçerli (DEC-056, kabul edilen sınır). |

### V4 Erişim kontrolü
| Madde | Durum | Kanıt / not |
| --- | --- | --- |
| 4.1.1 – 4.1.3 Sunucu tarafında, en az yetki, varsayılan red | ✅ | Fallback policy, rol grupları (`EndpointConventions.RequireRole`), `EndpointSecurityTests`. |
| 4.2.1 IDOR / başka okulun kaydı | ✅ | Tenant filtresi + bileşik FK + yazma koruması; başka okulun kaydı `404` (her modülün `*TenantIsolationTests` sınıfı). |
| 4.2.2 CSRF | ✅ | Bearer token cookie'de değil; cookie ile çalışan refresh/çıkış `SameSite=Strict` + `X-Requested-With` ister (`SessionTests`), CORS yok (`CorsTests`). |
| 4.3.1 Yönetim arayüzlerinde ek koruma | 🟡 | Platform Admin rolüyle ayrılmış; MFA yok (Fikirler 5). |
| Antrenör görünümü (NFR-PRV-02) | ✅ | Roster yanıt şekli sabit (`IntegrationTests/Enrollments/RosterTests`). |

### V5 Doğrulama, temizleme, kodlama
| Madde | Durum | Kanıt / not |
| --- | --- | --- |
| 5.1.1 – 5.1.4 Sınırda pozitif doğrulama, toplu atama yok | ✅ | Her command için FluentValidation; istek kayıtları (record) sadece izinli alanları içerir; value object'ler (`Domain.UnitTests/SharedKernel/*`). |
| 5.2 / 5.3 Çıktı kodlaması, XSS | ✅ | API sadece JSON döner; Angular şablonları varsayılan olarak kodlar (uygulama kodunda `innerHTML` / `bypassSecurityTrust*` yok). |
| 5.3.4 SQL injection | ✅ | EF Core parametreli sorgular; ham SQL sadece `ExecuteSqlAsync` interpolasyonuyla (parametreye dönüşür, `EnrollmentLocks`, `GroupLocks`, `PlatformAdminSeed`) ve sabit `SET LOCAL lock_timeout` metniyle. |
| 5.5 Deserialization | ✅ | System.Text.Json, polimorfik tip çözümü yok; para metin olarak (DEC-066). |

### V7 Hata yönetimi ve loglama
| Madde | Durum | Kanıt / not |
| --- | --- | --- |
| 7.1.1 – 7.1.2 Loglarda kimlik bilgisi/kişisel veri yok | ✅ | `LogPrivacyTests`. |
| 7.4.1 Genel hata mesajı, iz kimliği | ✅ | `ErrorHandlingTests.Unexpected_exceptions_become_a_bare_500_problem` (sadece `Unexpected.Error` + `traceId`). |
| Güvenlik olaylarının loglanması (giriş hatası, kilit) | 🟡 | Hata kodları loglanıyor (`LoginCommand failed with Auth.InvalidCredentials`), ayrı bir denetim (audit) kaydı yok; Fikirler 6. |

### V8 Veri koruma
| Madde | Durum | Kanıt / not |
| --- | --- | --- |
| 8.2.1 Hassas veriler önbelleğe alınmaz | ✅ | `/api` yanıtlarında `Cache-Control: no-store` (`AuthHardeningTests.Api_responses_are_never_cached`). |
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
| Dosya yükleme | — | MVP'de yok. |

### V13 API
| Madde | Durum | Kanıt / not |
| --- | --- | --- |
| 13.1.3 URL'de anahtar/token yok | ✅ | V3.1.1. |
| 13.2.1 – 13.2.3 Yöntem kısıtı, JSON şema, CSRF | ✅ | Rotalar sadece tanımlı yöntemleri kabul eder (`405`, `IntegrationTests/Security/ApiSurfaceTests`); OpenAPI sözleşme testi (`OpenApiContractTests`); CSRF V4.2.2. |
| 13.2.5 `Content-Type` kontrolü | ✅ | JSON gövdeli endpoint'ler başka içerik türünü `415` ile reddeder (`ApiSurfaceTests`). |

### V14 Yapılandırma
| Madde | Durum | Kanıt / not |
| --- | --- | --- |
| 14.1 Tekrarlanabilir derleme, uyarılar hata | ✅ | `TreatWarningsAsErrors`, analyzer'lar, `dotnet format` CI'da. |
| 14.2.1 Bağımlılıklar güncel ve açıksız | ✅ | NFR-SEC-11. |
| 14.3.2 Hata ayıklama modu kapalı | ✅ | OpenAPI dokümanı sadece Development/Testing'de (`OpenApiSetup.cs`); production imajında geliştirici hata sayfası yok (`GlobalExceptionHandler`). |
| 14.3.3 Sürüm bilgisi sızmaz | 🟡 | nginx `server_tokens off` (`docker/admin/nginx.conf`); Kestrel `Server` başlığı kapatılmadı. Fikirler 2. |
| 14.4 Güvenlik başlıkları (CSP, nosniff, frame, Referrer-Policy) | ⛔ | NFR-SEC-13 (faz 4). Fikirler 2. |
| 14.5.3 CORS izin listesi | ✅ | CORS yok (`CorsTests`). |

## 3. Fikirler (`type:idea` adayları, issue açılmadı)

1. **HTTPS/HSTS ve forwarded header'lar (Deploy bloğu):** reverse proxy'de TLS 1.2+, HSTS, HTTP→HTTPS; API'de `UseForwardedHeaders` (sadece proxy'nin IP'si güvenilir), aksi halde rate limit tüm istemcileri proxy IP'siyle sayar.
2. **Güvenlik başlıkları (NFR-SEC-13, faz 4 öne alınabilir):** API ve admin imajında `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `frame-ancestors 'none'` / `X-Frame-Options: DENY`, admin için CSP; Kestrel `AddServerHeader = false`. Otomatik başlık testi.
3. **Şifre uzunluğu:** ASVS L1 12 karakter önerir; politika şu an 10 (sahip kararı, NFR-SEC-04). Sahip isterse 12'ye çıkarılır.
4. **İhlal edilmiş şifre kontrolü:** k-anonymity ile HIBP benzeri bir servis veya daha büyük bir gömülü liste (çevrimdışı çalışmalı, KVKK açısından şifrenin kendisi dışarı çıkmamalı).
5. **MFA (Platform Admin ve Okul Admini için TOTP):** faz 4 adayı.
6. **Güvenlik denetim kaydı:** başarısız giriş, kilit, şifre değişimi/sıfırlama, pasifleştirme için ayrı, kişisel veri içermeyen audit event'leri ve Seq/alarm.
7. **Arama terimi sorgu dizesinde:** `GET /students?search=` isim/telefon parçası taşır; proxy erişim loglarında sorgu dizesi maskelenmeli veya arama `POST` gövdesine taşınmalı.
8. **Kişisel veri envanteri (NFR-PRV-01):** her kişisel veri alanını, amacını ve saklama süresini listeleyen doküman; faz geçişinde gözden geçirilir.
9. **Log gizliliği testinin kapsamı:** ödeme kaydı (Billing, PR #40) bu dal açıldığında main'de değildi; birleşince `LogPrivacyTests`'e eklenmeli. Yeni modüller (yoklama, duyurular) da eklenmeli.
11. **Peşin ödemeli kaydın erken bitirilmesi:** ileri bir ay için ödemesi olan kayıt o aydan önce bitirilebiliyor (DEC-184); iade akışı veya uyarı değerlendirilmeli.
10. **Refresh token yarışı:** aynı token'la eşzamanlı iki sekme birini `409` ile düşürür (DEC-119); kısa bir tolerans penceresi değerlendirilebilir.
