# syntax=docker/dockerfile:1
# Migrator image (ADR-0011): a self-contained efbundle plus the SQL script from the same build.
# Connection comes from ConnectionStrings__Migrator at run time.
FROM mcr.microsoft.com/dotnet/sdk:10.0.404-noble@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY .config/ .config/
COPY src/Directory.Build.props src/
COPY src/SporAkademi.Domain/SporAkademi.Domain.csproj src/SporAkademi.Domain/packages.lock.json src/SporAkademi.Domain/
COPY src/SporAkademi.Application/SporAkademi.Application.csproj src/SporAkademi.Application/packages.lock.json src/SporAkademi.Application/
COPY src/SporAkademi.Infrastructure/SporAkademi.Infrastructure.csproj src/SporAkademi.Infrastructure/packages.lock.json src/SporAkademi.Infrastructure/
RUN dotnet tool restore && dotnet restore src/SporAkademi.Infrastructure/SporAkademi.Infrastructure.csproj --locked-mode -r linux-x64
COPY src/SporAkademi.Domain/ src/SporAkademi.Domain/
COPY src/SporAkademi.Application/ src/SporAkademi.Application/
COPY src/SporAkademi.Infrastructure/ src/SporAkademi.Infrastructure/
RUN dotnet tool run dotnet-ef migrations bundle --project src/SporAkademi.Infrastructure --startup-project src/SporAkademi.Infrastructure \
      --self-contained -r linux-x64 -o /out/efbundle \
 && dotnet tool run dotnet-ef migrations script --idempotent --project src/SporAkademi.Infrastructure --startup-project src/SporAkademi.Infrastructure \
      --no-build -o /out/migrations.sql

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0.15-noble-chiseled@sha256:40eba5db69594d517c3a61e96bdacc2e3d6f8c99a44b52d55edee844d33518d0
WORKDIR /migrator
COPY --from=build /out/ ./
USER $APP_UID
ENTRYPOINT ["/migrator/efbundle"]
