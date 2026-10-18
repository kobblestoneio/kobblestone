# syntax=docker/dockerfile:1
# Build from the repository root: docker build -f src/Kobblestone.AccountAuth/Dockerfile .

ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

COPY ["Directory.Build.props", "Directory.Packages.props", "./"]
COPY ["src/Kobblestone.Sessions/Kobblestone.Sessions.csproj", "src/Kobblestone.Sessions/"]
COPY ["src/Kobblestone.AccountAuth/Kobblestone.AccountAuth.csproj", "src/Kobblestone.AccountAuth/"]

RUN dotnet restore "src/Kobblestone.AccountAuth/Kobblestone.AccountAuth.csproj"

COPY src/Kobblestone.Sessions src/Kobblestone.Sessions
COPY src/Kobblestone.AccountAuth src/Kobblestone.AccountAuth

RUN dotnet publish "src/Kobblestone.AccountAuth/Kobblestone.AccountAuth.csproj" \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:${DOTNET_VERSION} AS runtime
WORKDIR /app

COPY --from=build /app/publish .

USER app

ENTRYPOINT ["dotnet", "Kobblestone.AccountAuth.dll"]
