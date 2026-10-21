FROM mcr.microsoft.com/dotnet/sdk@sha256:ed034a8bf0b24ded0cbbac07e17825d8e9ebfe21e308191d0f7421eaf5ad4664 AS probe
WORKDIR /src
RUN apt-get update -qq && apt-get install -y --no-install-recommends strace
COPY [".config/dotnet-tools.json", ".config/dotnet-tools.json"]
RUN dotnet tool restore
COPY ["tools/Attribution.Probe/Attribution.Probe.csproj", "tools/Attribution.Probe/"]
COPY ["tools/Attribution.Probe/Program.cs", "tools/Attribution.Probe/"]
RUN dotnet publish tools/Attribution.Probe/Attribution.Probe.csproj -c Release -o /probe
COPY ["RuntimeMetricsLab.Analysis/", "RuntimeMetricsLab.Analysis/"]
COPY ["RuntimeMetricsLab.Contracts/", "RuntimeMetricsLab.Contracts/"]
COPY ["RuntimeMetricsLab.Analysis.Worker/", "RuntimeMetricsLab.Analysis.Worker/"]
RUN dotnet publish RuntimeMetricsLab.Analysis.Worker/RuntimeMetricsLab.Analysis.Worker.csproj -c Release -o /analysis
ENV DOTNET_ROLL_FORWARD=Major
WORKDIR /src
CMD ["dotnet", "/probe/Attribution.Probe.dll"]
