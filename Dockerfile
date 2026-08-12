# syntax=docker/dockerfile:1
#
# Linux container image for the SolaX Local Controller worker (issues #26, #35). Builds both Linux
# architectures from this one file; Windows Nano Server needs its own, because a Dockerfile targets
# one OS -- see Dockerfile.windows.
#
# Cross-compiled, not emulated: the SDK stage is pinned to the *builder's* architecture with
# $BUILDPLATFORM and targets the requested one via `dotnet publish -a $TARGETARCH`, so an amd64 CI
# runner produces an arm64 image at native speed. The runtime stage contains no RUN instruction, so
# no foreign-architecture binary ever executes at build time and QEMU is not needed at all.
#
#   docker build --platform linux/arm64 -t solax-controller .   # the Pi
#   docker build --platform linux/amd64 -t solax-controller .   # an x64 host
#
# CI publishes both under one name as a multi-platform manifest list, so a deploy names a tag and
# never an architecture (deploy/README.md). See docs/DECISIONS.md for why this and not an on-device
# build.

ARG DOTNET_VERSION=10.0

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG TARGETARCH

# What the built worker will report at startup and to Home Assistant. Defaults match
# Directory.Build.props, so a plain `docker build` is honestly labelled as a local build; CI passes
# the release version and the commit. See src/Solax.Worker/BuildInfo.cs.
ARG VERSION=0.0.0-dev
ARG SOURCE_REVISION=

WORKDIR /source

# Restore against the project files alone, so the slow restore layer stays cached until a dependency
# actually changes -- not on every source edit.
COPY SolaxLocalController.slnx ./
COPY src/Solax.Core/Solax.Core.csproj                 src/Solax.Core/
COPY src/Solax.Infrastructure/Solax.Infrastructure.csproj src/Solax.Infrastructure/
COPY src/Solax.Web/Solax.Web.csproj                   src/Solax.Web/
COPY src/Solax.Worker/Solax.Worker.csproj             src/Solax.Worker/
RUN dotnet restore src/Solax.Worker/Solax.Worker.csproj -a "$TARGETARCH"

COPY src/ src/
RUN dotnet publish src/Solax.Worker/Solax.Worker.csproj \
        -a "$TARGETARCH" \
        -c Release \
        --no-restore \
        --self-contained false \
        -p:Version="$VERSION" \
        -p:SourceRevisionId="$SOURCE_REVISION" \
        -o /app \
    # The two directories the app writes to, both relative to WORKDIR: Serilog's file sink
    # ("logs/solax-.log") and the charging-session SQLite store ("data/sessions.db"). Created now, in
    # the natively-executing stage, so the runtime stage needs no RUN of its own. The deploy stack
    # bind-mounts host directories over both -- see deploy/docker-compose.yml. Without those mounts
    # the app still starts and writes here, into the container, and loses it all on the next
    # `docker rm`; the session history is the part that cannot be regenerated.
    && mkdir -p /app/logs /app/data

# The ASP.NET runtime rather than the plain one: the worker hosts the self-hosted UI (issue #44), so
# its assemblies bind the Microsoft.AspNetCore.App shared framework and the process will not start
# without it -- including when Web:Enabled is false, because the framework reference is a property of
# the build, not of the configuration. It costs roughly 25 MB of image over dotnet/runtime and no
# measurable memory while nothing is listening.
#
# Debian-based rather than a chiseled variant: it carries tzdata and ICU (log timestamps and
# SolarForecast.ForDate are timezone-sensitive) and keeps a shell for diagnosing a headless Pi.
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime

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
