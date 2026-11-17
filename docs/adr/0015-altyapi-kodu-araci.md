# ADR-0015 — Altyapı kodu aracı: OpenTofu ve `azurerm` sağlayıcısı

- **Durum:** Proposed (agent, 2026-11-17). Karar sahipte; ilk `apply`'dan önce verilmeli.
- **İlgili:** P5-05, DEC-640 – DEC-649, [ADR-0014](0014-veri-konumu.md) (veri konumu), DEC-087 (tek VPS, Caddy),
  NFR-CST-01, [docs/bulut-altyapisi.md](../bulut-altyapisi.md)

## Bağlam

Faz 5, ölçeklenebilir ve yönetilen bir barındırma ister (P5-05): birden çok API kopyası, yönetilen PostgreSQL,
sır deposu, gözlemlenebilirlik. Altyapının elle tıklanarak değil kodla tanımlanması gerekir: yeniden kurulabilirlik,
inceleme (PR) ve staging ile production'ın aynı şekilde kurulması için. Depodaki kural, ticari lisanslı bileşen
kullanmamaktır; BSL, SSPL, AGPL ve GPL kabul edilmez.

Hedef bulut Azure'dur (gerekçe için bulut-altyapisi.md; sahip başka bir sağlayıcı seçerse modüller değişir, araç
kararı değişmez).

## Seçenekler

| Seçenek | Lisans | Değerlendirme |
| --- | --- | --- |
| Terraform (HashiCorp) | **BSL-1.1** (Ağustos 2023'ten beri) | Kaynak açık ama rakip ürün kısıtı var; depoya "BSL yok" kuralı koyduk. Elenir. |
| **OpenTofu** | **MPL-2.0** (Linux Foundation) | Terraform 1.5 çatalı; aynı HCL, aynı sağlayıcılar. Geçici (`ephemeral`) kaynaklar ve write-only öznitelikler 1.11'den beri var, yani sır state'e yazılmadan akar. |
| Pulumi | Apache-2.0 | Genel amaçlı dil kullanır; ekibin .NET dışında yeni bir çalışma zamanı, yeni bir state hizmeti (bulut ya da kendi barındırma) demek. İlk adım için gereğinden ağır. |
| Bicep | MIT | Yalnız Azure; state yok, Azure'a bağlı. Güzel ama sağlayıcı değiştirme seçeneğini kapatır ve CI'da tek başına doğrulaması (`what-if`) bulut hesabı ister. |
| Elle (portal/CLI betiği) | - | Tekrarlanamaz, incelenemez. Elenir. |

`azurerm` sağlayıcısı (`hashicorp/azurerm`) **MPL-2.0**; `random` sağlayıcısı da MPL-2.0. OpenTofu bunları kendi
kayıt defterinden (`registry.opentofu.org`) ve imza doğrulamasıyla indirir.

## Karar (öneri)

- Araç **OpenTofu** (`>= 1.11`, CI'da 1.13.9, indirilen ikili SHA-256 ile doğrulanır).
- Sağlayıcılar: `hashicorp/azurerm ~> 5.14`, `hashicorp/random ~> 3.7`; her ortamda `.terraform.lock.hcl` commit edilir
  (linux_amd64 ve windows_amd64 özetleriyle), CI `-lockfile=readonly` ile çalışır: kilit dosyası güncel değilse kırmızı.
- Kod `infra/` altında: `modules/` (foundation, data, apps, stack) ve `envs/staging`, `envs/production`. Ortamlar workspace
  ile değil ayrı dizin ve ayrı state ile ayrılır (bir ortamdaki hata ötekinin state'ini etkilemesin; `apply` yetkisi ortam
  başına verilebilsin).
- CI'da `repo-checks` işine adım eklenir (zorunlu check kümesi değişmez): `tofu fmt -check`, her ortamda `init -backend=false`
  ve `validate`, ayrıca Trivy `config` taraması (HIGH/CRITICAL kırar). Bulut hesabı gerekmez.
- `plan` ve `apply` CI'da çalışmaz (hesap ve kimlik bilgisi yok); sahip yerelden ya da sonradan kurulacak bir iş akışından
  çalıştırır.

## Sonuçlar

- (+) Lisans riski yok; ileride sağlayıcı değişirse HCL bilgisi ve yapı korunur.
- (+) Sır state'e girmez (yönetici parolası geçici kaynakla üretilir, Key Vault'a ve sunucuya write-only yazılır).
- (-) `azurerm` 5.x yeni (2026-11-13'te 5.14.0); sürüm yükseltmeleri sağlayıcının kırıcı değişikliklerini getirebilir.
  Bu yüzden `~> 5.14` ile sabitlendi ve kilit dosyası commit'li. Dependabot'un OpenTofu desteği bu repoda denenmedi;
  sağlayıcı güncellemeleri şimdilik elle (aylık) yapılır.
- (-) `validate` yalnız söz dizimini ve şemayı doğrular. Gerçek hesapta `plan` çalışana kadar kota, bölge uygunluğu ve
  PostgreSQL sürümü (18) gibi kontroller bilinmez; bulut-altyapisi.md'deki ilk `apply` listesi bunu söyler.

## Geri dönüş koşulu

Terraform lisansı OSI uyumlu bir lisansa dönerse ya da OpenTofu'nun bakımı zayıflarsa yeniden değerlendirilir. HCL iki
araçta da çalıştığı için geçiş kod değişikliği değil, kilit dosyası ve CI adımının güncellenmesidir.
