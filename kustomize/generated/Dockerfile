FROM mcr.microsoft.com/dotnet/sdk:latest as build
WORKDIR /operator

COPY ./ ./
RUN dotnet publish -c Release /p:AssemblyName=operator -o out

# The runner for the application
FROM mcr.microsoft.com/dotnet/runtime:latest as final

RUN addgroup k8s-operator && useradd -G k8s-operator operator-user

WORKDIR /operator
COPY --chown=operator-user:k8s-operator --from=build /operator/out/ ./

USER operator-user

ENTRYPOINT [ "dotnet", "operator.dll" ]