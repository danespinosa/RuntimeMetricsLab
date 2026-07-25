# syntax=docker/dockerfile:1
#
# Multi-stage build for RuntimeMetricsLab.ApiService, published as the Aspire
# "server-linux" container resource (Linux container, run via the Docker CLI
# backed by the WSL2 Docker Engine, e.g. `docker build`/`docker run`).
#
# Build context is the repository root (see AppHost.cs -> AddDockerfile("server-linux", "..", ...)),
# so paths below are relative to the solution root, not this project folder.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore only the project files first for better layer caching.
COPY ["RuntimeMetricsLab.ApiService/RuntimeMetricsLab.ApiService.csproj", "RuntimeMetricsLab.ApiService/"]
COPY ["RuntimeMetricsLab.ServiceDefaults/RuntimeMetricsLab.ServiceDefaults.csproj", "RuntimeMetricsLab.ServiceDefaults/"]
COPY ["RuntimeMetricsLab.Contracts/RuntimeMetricsLab.Contracts.csproj", "RuntimeMetricsLab.Contracts/"]
COPY ["RuntimeMetricsLab.Experiments/RuntimeMetricsLab.Experiments.csproj", "RuntimeMetricsLab.Experiments/"]
RUN dotnet restore "RuntimeMetricsLab.ApiService/RuntimeMetricsLab.ApiService.csproj"

# Now copy the rest of the referenced project sources and publish.
COPY ["RuntimeMetricsLab.ApiService/", "RuntimeMetricsLab.ApiService/"]
COPY ["RuntimeMetricsLab.ServiceDefaults/", "RuntimeMetricsLab.ServiceDefaults/"]
COPY ["RuntimeMetricsLab.Contracts/", "RuntimeMetricsLab.Contracts/"]
COPY ["RuntimeMetricsLab.Experiments/", "RuntimeMetricsLab.Experiments/"]

WORKDIR /src/RuntimeMetricsLab.ApiService
RUN dotnet publish "RuntimeMetricsLab.ApiService.csproj" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080
EXPOSE 8443
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "RuntimeMetricsLab.ApiService.dll"]
