# syntax=docker/dockerfile:1
# Admin image (DEC-092): static Angular build served by nginx-unprivileged alpine-slim.
FROM node:26.13.0-alpine3.24@sha256:8996bb5821d62a8fd9bb27c1cb07462f3fdce6e47d640c6a881ead0b064ce216 AS build
WORKDIR /app
COPY web/admin/package.json web/admin/package-lock.json web/admin/.npmrc ./
RUN npm ci --no-audit --no-fund
COPY web/admin/ ./
RUN npm run build

FROM nginxinc/nginx-unprivileged:1.30.6-alpine-slim@sha256:5d8bb9437d74a600ec5dbf90c035451c724dc33eac8d3a71078f6f8612c345f6
# DEC-104: temporary until the stable base ships zlib >= 1.3.2-r1 (CVE-2026-85091). Remove then.
USER root
RUN apk upgrade --no-cache zlib
USER 101
COPY docker/admin/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist/admin/browser/ /usr/share/nginx/html/
EXPOSE 8080
