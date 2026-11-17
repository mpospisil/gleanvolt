# syntax=docker/dockerfile:1
#
# The OpenWeather plugin's image (#424, shipped since #426): ghcr.io/mpospisil/gleanvolt-plugin-openweather,
# published by publish-image.yml beside Gleanvolt's own image, from the same tag. deploy/docker-compose.yml
# runs it under the `openweather` profile. Built from the repository root, and cross-compiled like the
# main Dockerfile, so an amd64 machine builds the Pi's arm64 image at native speed:
#
#   docker build --platform linux/arm64 -f plugins/Gleanvolt.Plugin.OpenWeather/Dockerfile -t gleanvolt-plugin-openweather .
#
# See plugins/Gleanvolt.Plugin.OpenWeather/README.md for running it.

ARG DOTNET_VERSION=10.0

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG TARGETARCH

# The version the plugin reports in its hello, as for Gleanvolt's image: CI passes the release, a plain
# `docker build` is a development build of the product line. DockerfileTests holds this literal to
# Directory.Build.props, which .dockerignore keeps out of the context.
ARG VERSION=1.3.0-dev
ARG SOURCE_REVISION=

WORKDIR /source

# The plugin references the provider SDK and nothing else of Gleanvolt.
COPY src/Gleanvolt.Providers.Contracts/Gleanvolt.Providers.Contracts.csproj src/Gleanvolt.Providers.Contracts/
COPY src/Gleanvolt.Providers.Client/Gleanvolt.Providers.Client.csproj src/Gleanvolt.Providers.Client/
COPY plugins/Gleanvolt.Plugin.OpenWeather/Gleanvolt.Plugin.OpenWeather.csproj plugins/Gleanvolt.Plugin.OpenWeather/
RUN dotnet restore plugins/Gleanvolt.Plugin.OpenWeather/Gleanvolt.Plugin.OpenWeather.csproj -a "$TARGETARCH"

COPY src/Gleanvolt.Providers.Contracts/ src/Gleanvolt.Providers.Contracts/
COPY src/Gleanvolt.Providers.Client/ src/Gleanvolt.Providers.Client/
COPY plugins/Gleanvolt.Plugin.OpenWeather/ plugins/Gleanvolt.Plugin.OpenWeather/
RUN dotnet publish plugins/Gleanvolt.Plugin.OpenWeather/Gleanvolt.Plugin.OpenWeather.csproj \
        -a "$TARGETARCH" -c Release --no-restore --self-contained false \
        -p:Version="$VERSION" -p:SourceRevisionId="$SOURCE_REVISION" -o /app \
    # The data directory, for the volume: created here, owned by the app's user below.
    && mkdir -p /data

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
ARG APP_UID=1654
WORKDIR /app
COPY --from=build --chown=${APP_UID}:${APP_UID} /app .

# The terms travel with the image, as they do with Gleanvolt's.
COPY --chown=${APP_UID}:${APP_UID} LICENSE LICENSE-MIT THIRD-PARTY-NOTICES.md /app/
LABEL org.opencontainers.image.licenses="PolyForm-Noncommercial-1.0.0"

# The OpenWeatherMap key: owned by the app's user and closed to everyone else; a named volume takes
# these permissions over on first use.
COPY --from=build --chown=${APP_UID}:${APP_UID} --chmod=700 /data /data

USER ${APP_UID}
ENV DOTNET_EnableDiagnostics=0 \
    Urls=http://0.0.0.0:8096 \
    OpenWeather__DataDirectory=/data
VOLUME /data
EXPOSE 8096

ENTRYPOINT ["dotnet", "Gleanvolt.Plugin.OpenWeather.dll"]
