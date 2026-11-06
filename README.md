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

## Testler

```bash
dotnet test --solution SporAkademi.slnx   # Docker gerekir (Testcontainers)
cd web/admin && npm test
```

## Lisans

Lisans yoktur. Tüm hakları saklıdır.
