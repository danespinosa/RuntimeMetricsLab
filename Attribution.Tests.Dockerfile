FROM mcr.microsoft.com/dotnet/sdk@sha256:ed034a8bf0b24ded0cbbac07e17825d8e9ebfe21e308191d0f7421eaf5ad4664
WORKDIR /src
COPY ["RuntimeMetricsLab.Contracts/", "RuntimeMetricsLab.Contracts/"]
COPY ["RuntimeMetricsLab.Experiments/", "RuntimeMetricsLab.Experiments/"]
COPY ["RuntimeMetricsLab.Analysis/", "RuntimeMetricsLab.Analysis/"]
COPY ["RuntimeMetricsLab.Collector/", "RuntimeMetricsLab.Collector/"]
COPY ["RuntimeMetricsLab.Web/Services/WindowsUiOriginGuard.cs", "RuntimeMetricsLab.Web/Services/"]
COPY ["RuntimeMetricsLab.Attribution.Tests/", "RuntimeMetricsLab.Attribution.Tests/"]
COPY ["RuntimeMetricsLab.Experiments.Tests/", "RuntimeMetricsLab.Experiments.Tests/"]
RUN dotnet test RuntimeMetricsLab.Attribution.Tests/RuntimeMetricsLab.Attribution.Tests.csproj -c Release --logger "trx;LogFileName=attribution.trx" --results-directory /results
RUN dotnet test RuntimeMetricsLab.Experiments.Tests/RuntimeMetricsLab.Experiments.Tests.csproj -c Release --logger "trx;LogFileName=experiments.trx" --results-directory /results
