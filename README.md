# Spor Akademi

Spor okulları için çok kiracılı (multi-tenant) yönetim platformu: okullar, branşlar, tesisler, gruplar, öğrenciler, kayıtlar, yoklama ve aidat.

- Backend: .NET 10, ASP.NET Core, EF Core, PostgreSQL 18
- Admin arayüzü: Angular 22, Tailwind CSS, ApexCharts
- Mimari: Clean Architecture katmanlarıyla modüler monolit ([proje dokümanı](docs/proje-dokumani.md))

## Gereksinimler

- .NET SDK 10.0.401+ (`global.json`)
- Node.js 26 (`web/admin/.nvmrc`)
- Docker (Compose v2)
- Bash (Windows'ta Git Bash) ve `openssl`

## Yerelde çalıştırma

```bash
bash scripts/setup-env.sh          # .env'i rastgele şifrelerle oluşturur (bir kez)
docker compose up -d --wait        # PostgreSQL ve Seq
dotnet tool restore
bash scripts/ef.sh database update # migration'ları migrator rolüyle uygular
bash scripts/run-api.sh            # API: http://127.0.0.1:5080
```

Başka bir terminalde:

```bash
cd web/admin
npm ci
npm start                          # http://localhost:4200 (/api proxy'si API'ye gider)
```

- Sağlık kontrolleri: `/health/live`, `/health/ready`
- Loglar (Seq): http://127.0.0.1:5341

### İlk Platform Admin

`setup-env.sh`, `.env`'e `PLATFORM_ADMIN_EMAIL` ve rastgele bir `PLATFORM_ADMIN_PASSWORD` yazar ve şifreyi **bir kez** ekrana basar. `run-api.sh` bunları API'ye `PlatformAdmin__Email` / `PlatformAdmin__InitialPassword` olarak verir. API açılırken:

- Hiç Platform Admin yoksa ve ikisi de verilmişse, şifre değiştirme zorunlu bir Platform Admin oluşturur. Aynı anda açılan birden fazla örnek bir advisory lock ile sıraya girer; en fazla bir Platform Admin oluşur.
- Platform Admin zaten varsa hiçbir şey yapmaz (yapılandırmadaki e-posta farklı olsa bile).
- İkisi de verilmemişse hiçbir şey yapmaz. Sadece biri verilmişse, şifre hâlâ `change-me…` ise veya e-posta başka bir hesaba aitse, eksik/sorunlu ayarın **adını** uyarı olarak loglar ve atlar (değerler asla loglanmaz).
- E-posta geçersizse veya şifre politikaya uymuyorsa (en az 10 karakter, yaygın şifre değil) API açılmaz (fail-closed).

İlk girişte şifreyi değiştirdikten sonra `PLATFORM_ADMIN_PASSWORD` satırını `.env`'den silin.

## Testler

```bash
dotnet test --solution SporAkademi.slnx   # Docker gerekir (Testcontainers)
cd web/admin && npm test
```

## Lisans

Lisans yoktur. Tüm hakları saklıdır.
