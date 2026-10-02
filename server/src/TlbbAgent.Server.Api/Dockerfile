FROM mcr.microsoft.com/dotnet/sdk:10.0.301 AS publish
WORKDIR /src

COPY . .
RUN dotnet restore src/TlbbAgent.Server.Api/TlbbAgent.Server.Api.csproj --locked-mode
RUN dotnet publish src/TlbbAgent.Server.Api/TlbbAgent.Server.Api.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.9 AS final
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

COPY --from=publish --chown=$APP_UID:$APP_UID /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "TlbbAgent.Server.Api.dll"]
