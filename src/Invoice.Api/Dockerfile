FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src
COPY . .
RUN dotnet test D1.Invoice.sln --configuration Release --logger "console;verbosity=minimal"
RUN dotnet publish src/Invoice.Api/Invoice.Api.csproj --configuration Release --no-restore --output /app/publish /p:UseAppHost=false
RUN mkdir -p /app/data

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 DOTNET_EnableDiagnostics=0 DEMO_MODE=live
EXPOSE 8080
COPY --from=build --chown=$APP_UID:$APP_UID /app/publish .
COPY --from=build --chown=$APP_UID:$APP_UID /app/data /data
USER $APP_UID
HEALTHCHECK --interval=15s --timeout=3s --start-period=20s --retries=3 CMD ["dotnet", "Invoice.Api.dll", "--healthcheck", "http://127.0.0.1:8080/healthz"]
ENTRYPOINT ["dotnet", "Invoice.Api.dll"]
