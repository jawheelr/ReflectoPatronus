# Builds the rootfs of the downloadable Proxmox LXC template (Debian 12, PostgreSQL 17, ASP.NET Core 10 runtime, the
# API + web console and the provisioning bundle, all preinstalled). Build context = repository root.
# scripts/build-lxc-template.sh turns the image into dist/reflectopatronus-<version>-amd64.tar.zst.
FROM node:24-slim AS web
WORKDIR /w
COPY src/web/package.json src/web/package-lock.json ./
RUN npm ci --no-audit --no-fund --ignore-scripts
COPY src/web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api
WORKDIR /src
COPY src/ReflectoPatronus.Api/ReflectoPatronus.Api.csproj ./
RUN dotnet restore
COPY src/ReflectoPatronus.Api/ ./
RUN rm -rf wwwroot
COPY --from=web /w/dist ./wwwroot
RUN dotnet publish -c Release -o /out --no-restore && tar -C /out -czf /api.tar.gz .

FROM debian:12-slim
ARG RP_VERSION=dev
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends \
      systemd systemd-sysv dbus ifupdown iproute2 iputils-ping procps ca-certificates curl gnupg sudo locales openssl \
      libicu72 libssl3 zlib1g \
 && curl -fsSL https://www.postgresql.org/media/keys/ACCC4CF8.asc | gpg --dearmor -o /usr/share/keyrings/pgdg.gpg \
 && echo "deb [signed-by=/usr/share/keyrings/pgdg.gpg] https://apt.postgresql.org/pub/repos/apt bookworm-pgdg main" > /etc/apt/sources.list.d/pgdg.list \
 && mkdir -p /etc/postgresql-common && echo "create_main_cluster = false" > /etc/postgresql-common/createcluster.conf \
 && apt-get update && apt-get install -y --no-install-recommends postgresql-17 postgresql-client-17 \
 && curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
 && bash /tmp/dotnet-install.sh --runtime aspnetcore --channel 10.0 --install-dir /usr/share/dotnet \
 && ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet \
 && rm -f /tmp/dotnet-install.sh && apt-get clean && rm -rf /var/lib/apt/lists/* \
 && systemctl disable postgresql 2>/dev/null || true

# The database cluster is NOT created here: /var/lib/postgresql is usually a separate mount (see pct-create.sh
# --data-mount), and the cluster must be created on it at first provisioning.
COPY deploy /opt/reflectopatronus/bundle/deploy
COPY db /opt/reflectopatronus/bundle/db
COPY scripts /opt/reflectopatronus/bundle/scripts
COPY docker/postgres/init /opt/reflectopatronus/bundle/docker/postgres/init
COPY --from=api /api.tar.gz /opt/reflectopatronus/api-dist.tar.gz
RUN printf '#!/usr/bin/env bash\nrole="${1:-}"; [[ "$role" == db || "$role" == api ]] || { echo "usage: rp-setup db|api   (reads /root/rp.env, certs in /root/pki-in)" >&2; exit 2; }\nexec bash /opt/reflectopatronus/bundle/deploy/lxc/provision-$role.sh\n' > /usr/local/sbin/rp-setup \
 && chmod 0755 /usr/local/sbin/rp-setup \
 && echo "$RP_VERSION" > /opt/reflectopatronus/VERSION && touch /opt/reflectopatronus/.preinstalled \
 && find /opt/reflectopatronus -name '._*' -delete
