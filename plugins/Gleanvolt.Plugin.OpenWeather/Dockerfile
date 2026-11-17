# syntax=docker/dockerfile:1
#
# The OpenWeather plugin's own image (#424). Not the Gleanvolt image and not published anywhere yet
# (#426 will): built by hand, from the repository root, for a test on the Pi. Cross-compiled like the
# main Dockerfile, so an amd64 machine builds the Pi's arm64 image at native speed:
#
#   docker build --platform linux/arm64 -f plugins/Gleanvolt.Plugin.OpenWeather/Dockerfile -t gleanvolt-openweather .
#
# See plugins/Gleanvolt.Plugin.OpenWeather/README.md for running it.

ARG DOTNET_VERSION=10.0

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG TARGETARCH
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
        -a "$TARGETARCH" -c Release --no-restore --self-contained false -o /app \
    # The data directory, for the volume: created here, owned by the app's user below.
    && mkdir -p /data

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
ARG APP_UID=1654
WORKDIR /app
COPY --from=build --chown=${APP_UID}:${APP_UID} /app .

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
