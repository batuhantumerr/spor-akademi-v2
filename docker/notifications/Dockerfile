# syntax=docker/dockerfile:1
# Notifications worker image (P5-04, DEC-630): the API's base images and hardening (chiseled-extra for ICU + tzdata,
# non-root, digest-pinned); no demo seeder, no API code. Build from the repository root.
FROM mcr.microsoft.com/dotnet/sdk:10.0.404-noble@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Directory.Build.props src/
COPY src/SporAkademi.Domain/SporAkademi.Domain.csproj src/SporAkademi.Domain/packages.lock.json src/SporAkademi.Domain/
COPY src/SporAkademi.Application/SporAkademi.Application.csproj src/SporAkademi.Application/packages.lock.json src/SporAkademi.Application/
COPY src/SporAkademi.Infrastructure/SporAkademi.Infrastructure.csproj src/SporAkademi.Infrastructure/packages.lock.json src/SporAkademi.Infrastructure/
COPY src/SporAkademi.Notifications/SporAkademi.Notifications.csproj src/SporAkademi.Notifications/packages.lock.json src/SporAkademi.Notifications/
RUN dotnet restore src/SporAkademi.Notifications/SporAkademi.Notifications.csproj --locked-mode -r linux-x64
COPY src/ src/
RUN dotnet publish src/SporAkademi.Notifications/SporAkademi.Notifications.csproj -c Release -r linux-x64 --self-contained false --no-restore -o /app

# Same runtime tag as the API image (keep the two in step: CVE fixes land in the shared framework).
FROM mcr.microsoft.com/dotnet/aspnet:10.0.15-noble-chiseled-extra@sha256:cae5b962bfb786606def03d250c6a645cc98839095c89e806e4bd8978a68115b
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "SporAkademi.Notifications.dll"]
