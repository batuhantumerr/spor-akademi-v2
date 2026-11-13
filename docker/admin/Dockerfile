# syntax=docker/dockerfile:1
# Admin image (DEC-092): static Angular build served by nginx-unprivileged alpine-slim.
FROM node:26.13.0-alpine3.24@sha256:8996bb5821d62a8fd9bb27c1cb07462f3fdce6e47d640c6a881ead0b064ce216 AS build
WORKDIR /app
COPY web/admin/package.json web/admin/package-lock.json web/admin/.npmrc ./
RUN npm ci --no-audit --no-fund
COPY web/admin/ ./
RUN npm run build

FROM nginxinc/nginx-unprivileged:1.31.7-alpine-slim@sha256:a8cb0f33d51fa107c96fd2bd3a71aaf564ec131e9d2bdf8700944f0ed8b170d7
# DEC-104/DEC-202: temporary until the stable base ships fixed zlib (CVE-2026-85091) and musl (CVE-2026-107891). Remove then.
USER root
RUN apk add --no-cache 'zlib>=1.3.2-r1' 'musl>=1.2.6-r3' 'musl-utils>=1.2.6-r3'
USER 101
# P4-01 (DEC-502): the public origin of the document store, for photo thumbnails (img-src); empty means none. The
# entrypoint checks it and renders the security headers from the template; envsubst touches only this variable.
ENV STORAGE_PUBLIC_ORIGIN="" \
    NGINX_ENVSUBST_FILTER="STORAGE_PUBLIC_ORIGIN"
COPY --chmod=755 docker/admin/19-check-storage-origin.sh /docker-entrypoint.d/19-check-storage-origin.sh
COPY docker/admin/security-headers.inc.template /etc/nginx/templates/security-headers.inc.template
COPY docker/admin/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist/admin/browser/ /usr/share/nginx/html/
EXPOSE 8080
