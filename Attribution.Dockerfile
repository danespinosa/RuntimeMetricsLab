FROM mcr.microsoft.com/dotnet/sdk@sha256:ed034a8bf0b24ded0cbbac07e17825d8e9ebfe21e308191d0f7421eaf5ad4664 AS build
WORKDIR /src
COPY [".config/dotnet-tools.json", ".config/dotnet-tools.json"]
RUN dotnet tool restore
COPY ["RuntimeMetricsLab.Contracts/", "RuntimeMetricsLab.Contracts/"]
COPY ["RuntimeMetricsLab.ServiceDefaults/", "RuntimeMetricsLab.ServiceDefaults/"]
COPY ["RuntimeMetricsLab.Experiments/", "RuntimeMetricsLab.Experiments/"]
COPY ["RuntimeMetricsLab.ApiService/", "RuntimeMetricsLab.ApiService/"]
COPY ["RuntimeMetricsLab.Analysis/", "RuntimeMetricsLab.Analysis/"]
COPY ["RuntimeMetricsLab.Analysis.Worker/", "RuntimeMetricsLab.Analysis.Worker/"]
COPY ["RuntimeMetricsLab.Collector/", "RuntimeMetricsLab.Collector/"]
COPY ["RuntimeMetricsLab.Web/", "RuntimeMetricsLab.Web/"]
RUN dotnet publish RuntimeMetricsLab.ApiService/RuntimeMetricsLab.ApiService.csproj -c Release -o /publish/server
RUN dotnet publish RuntimeMetricsLab.Analysis.Worker/RuntimeMetricsLab.Analysis.Worker.csproj -c Release -o /publish/analysis
RUN dotnet publish RuntimeMetricsLab.Collector/RuntimeMetricsLab.Collector.csproj -c Release -o /publish/collector
RUN dotnet publish RuntimeMetricsLab.Web/RuntimeMetricsLab.Web.csproj -c Release -o /publish/web

FROM mcr.microsoft.com/dotnet/sdk@sha256:ed034a8bf0b24ded0cbbac07e17825d8e9ebfe21e308191d0f7421eaf5ad4664
WORKDIR /app
RUN mkdir /data /captures && chown 1654:1654 /data
COPY --from=build /publish /app
RUN sed -i 's/\r$//' /app/collector/bootstrap.sh && chmod 0555 /app/collector/bootstrap.sh
COPY --from=build /root/.nuget/packages/dotnet-trace/10.0.750501 /root/.nuget/packages/dotnet-trace/10.0.750501
ENV ASPNETCORE_HTTP_PORTS=8080
ENV ASPNETCORE_ENVIRONMENT=Development
ENV RuntimeMetricsLab__Mode=attribution
ENV TMPDIR=/tmp
ENV DOTNET_ROLL_FORWARD=Major
EXPOSE 8080
ENTRYPOINT ["dotnet"]
