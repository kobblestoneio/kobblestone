# syntax=docker/dockerfile:1

ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

COPY ["Directory.Build.props", "Directory.Packages.props", "./"]
COPY ["src/Kobblestone.Operator/Kobblestone.Operator.csproj", "src/Kobblestone.Operator/"]

RUN dotnet restore "src/Kobblestone.Operator/Kobblestone.Operator.csproj"

COPY . .
WORKDIR /src/src/Kobblestone.Operator
RUN dotnet publish "Kobblestone.Operator.csproj" \
    -c Release \
    -o /app/publish \
    --no-restore \
    --self-contained false \
    /p:GenerateOperatorResources=false \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
WORKDIR /app

RUN groupadd --system kobblestone \
    && useradd --system --gid kobblestone --no-create-home --shell /usr/sbin/nologin kobblestone

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_GENERATE_ASPNET_CERTIFICATE=false

COPY --from=build --chown=kobblestone:kobblestone /app/publish .

USER kobblestone
EXPOSE 8080

ENTRYPOINT ["dotnet", "Kobblestone.Operator.dll"]