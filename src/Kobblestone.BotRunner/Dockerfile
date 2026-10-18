# syntax=docker/dockerfile:1
# Build from the repository root: docker build -f src/Kobblestone.BotRunner/Dockerfile .
# The bundled Minecraft-Console-Client release can be changed with --build-arg MCC_VERSION=<release tag>.

ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

COPY ["Directory.Build.props", "Directory.Packages.props", "./"]
COPY ["src/Kobblestone.Sessions/Kobblestone.Sessions.csproj", "src/Kobblestone.Sessions/"]
COPY ["src/Kobblestone.BotRunner/Kobblestone.BotRunner.csproj", "src/Kobblestone.BotRunner/"]

RUN dotnet restore "src/Kobblestone.BotRunner/Kobblestone.BotRunner.csproj"

COPY src/Kobblestone.Sessions src/Kobblestone.Sessions
COPY src/Kobblestone.BotRunner src/Kobblestone.BotRunner

RUN dotnet publish "src/Kobblestone.BotRunner/Kobblestone.BotRunner.csproj" \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

# Unmodified MCC release binary (CDDL-1.0, https://github.com/MCCTeam/Minecraft-Console-Client).
# MCC_VERSION is a release tag; override it with --build-arg MCC_VERSION=<tag>.
FROM alpine:3.22 AS mcc
ARG MCC_VERSION=20260929-519
ARG TARGETARCH
RUN case "${TARGETARCH}" in amd64) arch=x64 ;; *) arch="${TARGETARCH}" ;; esac \
    && mkdir -p /opt/mcc \
    && wget -q -O /opt/mcc/MinecraftClient \
       "https://github.com/MCCTeam/Minecraft-Console-Client/releases/download/${MCC_VERSION}/MinecraftClient-${MCC_VERSION}-linux-${arch}" \
    && wget -q -O /opt/mcc/LICENSE \
       "https://raw.githubusercontent.com/MCCTeam/Minecraft-Console-Client/${MCC_VERSION}/LICENSE.md" \
    && chmod 755 /opt/mcc/MinecraftClient

FROM mcr.microsoft.com/dotnet/runtime:${DOTNET_VERSION} AS runtime

COPY --from=mcc /opt/mcc /opt/mcc
COPY --from=build /app/publish /app

RUN mkdir -p /data && chown app:app /data

USER app
WORKDIR /data

ENTRYPOINT ["dotnet", "/app/Kobblestone.BotRunner.dll"]
