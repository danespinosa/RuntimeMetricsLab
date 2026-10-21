# syntax=docker/dockerfile:1
#
# Multi-stage build for RuntimeMetricsLab.Web, published as the Aspire
# "client-linux" container resource (Linux container, run via the Docker CLI
# backed by the WSL2 Docker Engine, e.g. `docker build`/`docker run`).
#
# Build context is the repository root (see AppHost.cs -> AddDockerfile("client-linux", "..", ...)),
# so paths below are relative to the solution root, not this project folder.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore only the project files first for better layer caching.
COPY ["RuntimeMetricsLab.Web/RuntimeMetricsLab.Web.csproj", "RuntimeMetricsLab.Web/"]
COPY ["RuntimeMetricsLab.ServiceDefaults/RuntimeMetricsLab.ServiceDefaults.csproj", "RuntimeMetricsLab.ServiceDefaults/"]
COPY ["RuntimeMetricsLab.Contracts/RuntimeMetricsLab.Contracts.csproj", "RuntimeMetricsLab.Contracts/"]
COPY ["RuntimeMetricsLab.Experiments/RuntimeMetricsLab.Experiments.csproj", "RuntimeMetricsLab.Experiments/"]
COPY ["RuntimeMetricsLab.Analysis/RuntimeMetricsLab.Analysis.csproj", "RuntimeMetricsLab.Analysis/"]
COPY ["RuntimeMetricsLab.Analysis.Worker/RuntimeMetricsLab.Analysis.Worker.csproj", "RuntimeMetricsLab.Analysis.Worker/"]
RUN dotnet restore "RuntimeMetricsLab.Web/RuntimeMetricsLab.Web.csproj"

# Now copy the rest of the referenced project sources and publish.
COPY ["RuntimeMetricsLab.Web/", "RuntimeMetricsLab.Web/"]
COPY ["RuntimeMetricsLab.ServiceDefaults/", "RuntimeMetricsLab.ServiceDefaults/"]
COPY ["RuntimeMetricsLab.Contracts/", "RuntimeMetricsLab.Contracts/"]
COPY ["RuntimeMetricsLab.Experiments/", "RuntimeMetricsLab.Experiments/"]
COPY ["RuntimeMetricsLab.Analysis/", "RuntimeMetricsLab.Analysis/"]
COPY ["RuntimeMetricsLab.Analysis.Worker/", "RuntimeMetricsLab.Analysis.Worker/"]

WORKDIR /src/RuntimeMetricsLab.Web
RUN dotnet publish "RuntimeMetricsLab.Web.csproj" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080
EXPOSE 8443
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "RuntimeMetricsLab.Web.dll"]
