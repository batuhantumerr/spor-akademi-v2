# syntax=docker/dockerfile:1
# API image (DEC-081): chiseled-extra (ICU + tzdata), non-root, digest-pinned bases.
FROM mcr.microsoft.com/dotnet/sdk:10.0.403-noble@sha256:212fe4cb74627d5adf9817a4088906ae3edff1bd853bb27565d4b46731efe61e AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Directory.Build.props src/
COPY src/SporAkademi.Domain/SporAkademi.Domain.csproj src/SporAkademi.Domain/packages.lock.json src/SporAkademi.Domain/
COPY src/SporAkademi.Application/SporAkademi.Application.csproj src/SporAkademi.Application/packages.lock.json src/SporAkademi.Application/
COPY src/SporAkademi.Infrastructure/SporAkademi.Infrastructure.csproj src/SporAkademi.Infrastructure/packages.lock.json src/SporAkademi.Infrastructure/
COPY src/SporAkademi.Api/SporAkademi.Api.csproj src/SporAkademi.Api/packages.lock.json src/SporAkademi.Api/
RUN dotnet restore src/SporAkademi.Api/SporAkademi.Api.csproj --locked-mode -r linux-x64
COPY src/ src/
RUN dotnet publish src/SporAkademi.Api/SporAkademi.Api.csproj -c Release -r linux-x64 --self-contained false --no-restore -o /app

# P3-08 (DEC-401): 10.0.15 fixes CVE-2026-114147 (GHSA-wr8v-9x5q-ggcm, ASP.NET Core DoS) in the shared framework that now serves SignalR.
FROM mcr.microsoft.com/dotnet/aspnet:10.0.15-noble-chiseled-extra@sha256:cae5b962bfb786606def03d250c6a645cc98839095c89e806e4bd8978a68115b
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "SporAkademi.Api.dll"]
