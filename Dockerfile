FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY . .
RUN dotnet restore FiscalDocuments.sln
RUN dotnet publish src/FiscalDocuments.Api -c Release --no-restore -o /out/api /p:UseAppHost=false
RUN dotnet publish src/FiscalDocuments.Worker -c Release --no-restore -o /out/worker /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS api
WORKDIR /app
COPY --from=build /out/api .
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "FiscalDocuments.Api.dll"]

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS worker
WORKDIR /app
COPY --from=build /out/worker .
USER app
ENTRYPOINT ["dotnet", "FiscalDocuments.Worker.dll"]
