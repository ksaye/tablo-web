# Tablo web — build once, run anywhere ffmpeg runs.
#
# The runtime image carries Debian's ffmpeg, which is built with NVENC and VA-API support. That
# is only half the story: hardware encoding also needs the host's GPU handed into the container
# (see docs/docker.md). The app tests its encoder at startup and falls back to software, so an
# image with no GPU in reach still works — it just costs CPU.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, as its own layer: the dependency graph changes far less often than the code.
COPY src/TabloWeb/TabloWeb.csproj src/TabloWeb/
COPY src/TabloWeb.Core/TabloWeb.Core.csproj src/TabloWeb.Core/
RUN dotnet restore src/TabloWeb/TabloWeb.csproj

COPY src/ src/
RUN dotnet publish src/TabloWeb/TabloWeb.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0

RUN apt-get update \
 && apt-get install -y --no-install-recommends ffmpeg curl \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app .

ENV TABLOWEB_URLS=http://0.0.0.0:8787 \
    TABLOWEB_CONFIG_DIR=/config \
    TABLOWEB_STREAM_DIR=/var/tmp/tabloweb-stream \
    TABLOWEB_MOSAIC_DIR=/var/tmp/tabloweb-mosaic

# Credentials and the data-protection keys that encrypt them and sign session cookies. Mount it,
# or every restart is a fresh sign-in for everybody.
VOLUME ["/config"]

EXPOSE 8787

# The Tablo may be unreachable or still loading its guide; that is not the container's health.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s \
  CMD curl -fsS http://127.0.0.1:8787/healthz || exit 1

ENTRYPOINT ["dotnet", "TabloWeb.dll"]
