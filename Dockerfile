FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY global.json Directory.Build.props nuget.config RiskAlert.slnx ./
COPY src/RiskAlert.Core/RiskAlert.Core.csproj src/RiskAlert.Core/
COPY src/RiskAlert.Infrastructure/RiskAlert.Infrastructure.csproj src/RiskAlert.Infrastructure/
COPY src/RiskAlert.Api/RiskAlert.Api.csproj src/RiskAlert.Api/
COPY tools/RiskAlert.EventSimulator/RiskAlert.EventSimulator.csproj tools/RiskAlert.EventSimulator/
COPY tests/RiskAlert.Tests/RiskAlert.Tests.csproj tests/RiskAlert.Tests/
RUN dotnet restore RiskAlert.slnx
COPY src/ src/
COPY tools/ tools/
RUN dotnet publish src/RiskAlert.Api/RiskAlert.Api.csproj --no-restore -c Release -o /out/api /p:UseAppHost=false
RUN dotnet publish tools/RiskAlert.EventSimulator/RiskAlert.EventSimulator.csproj --no-restore -c Release -o /out/simulator /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS simulator
WORKDIR /app
COPY --from=build /out/simulator .
USER $APP_UID
ENTRYPOINT ["dotnet", "RiskAlert.EventSimulator.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:5107
EXPOSE 5107
COPY --from=build /out/api .
RUN mkdir -p /app/appdata && chown "$APP_UID" /app/appdata
USER $APP_UID
ENTRYPOINT ["dotnet", "RiskAlert.Api.dll"]
