# syntax=docker/dockerfile:1
#
# Container image for the SolaX Local Controller worker (issue #26).
#
# Cross-compiled, not emulated: the SDK stage is pinned to the *builder's* architecture with
# $BUILDPLATFORM and targets the requested one via `dotnet publish -a $TARGETARCH`, so an amd64 CI
# runner produces an arm64 image at native speed. The runtime stage contains no RUN instruction, so
# nothing arm64 ever has to execute at build time and QEMU is not needed at all.
#
#   docker build --platform linux/arm64 -t solax-controller .
#
# See docs/DECISIONS.md for why this and not an on-device build.

ARG DOTNET_VERSION=10.0

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG TARGETARCH
WORKDIR /source

# Restore against the project files alone, so the slow restore layer stays cached until a dependency
# actually changes -- not on every source edit.
COPY SolaxLocalController.slnx ./
COPY src/Solax.Core/Solax.Core.csproj                 src/Solax.Core/
COPY src/Solax.Infrastructure/Solax.Infrastructure.csproj src/Solax.Infrastructure/
COPY src/Solax.Worker/Solax.Worker.csproj             src/Solax.Worker/
RUN dotnet restore src/Solax.Worker/Solax.Worker.csproj -a "$TARGETARCH"

COPY src/ src/
RUN dotnet publish src/Solax.Worker/Solax.Worker.csproj \
        -a "$TARGETARCH" \
        -c Release \
        --no-restore \
        --self-contained false \
        -o /app \
    # Serilog's file sink writes here (appsettings.json: "logs/solax-.log", relative to WORKDIR).
    # Created now, in the natively-executing stage, so the runtime stage needs no RUN of its own.
    # The deploy stack bind-mounts a host directory over it -- see deploy/docker-compose.yml.
    && mkdir -p /app/logs

# Debian-based runtime rather than a chiseled variant: it carries tzdata and ICU (log timestamps and
# SolarForecast.ForDate are timezone-sensitive) and keeps a shell for diagnosing a headless Pi.
FROM mcr.microsoft.com/dotnet/runtime:${DOTNET_VERSION} AS runtime

# The non-root user shipped in the .NET base images. Declared explicitly rather than relying on the
# inherited $APP_UID, because the host directory bind-mounted over /app/logs must be chowned to this
# same id (deploy/README.md documents it).
ARG APP_UID=1654

WORKDIR /app
COPY --from=build --chown=${APP_UID}:${APP_UID} /app .
USER ${APP_UID}

# No diagnostic IPC socket: nothing here attaches a profiler, and it is one less writable path.
ENV DOTNET_EnableDiagnostics=0

ENTRYPOINT ["dotnet", "Solax.Worker.dll"]
