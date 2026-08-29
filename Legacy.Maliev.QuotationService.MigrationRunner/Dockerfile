# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY nuget.config .
COPY Directory.Build.props .
COPY Legacy.Maliev.QuotationService.Application/Legacy.Maliev.QuotationService.Application.csproj Legacy.Maliev.QuotationService.Application/
COPY Legacy.Maliev.QuotationService.Domain/Legacy.Maliev.QuotationService.Domain.csproj Legacy.Maliev.QuotationService.Domain/
COPY Legacy.Maliev.QuotationService.Data/Legacy.Maliev.QuotationService.Data.csproj Legacy.Maliev.QuotationService.Data/
COPY Legacy.Maliev.QuotationService.MigrationRunner/Legacy.Maliev.QuotationService.MigrationRunner.csproj Legacy.Maliev.QuotationService.MigrationRunner/
RUN dotnet restore Legacy.Maliev.QuotationService.MigrationRunner/Legacy.Maliev.QuotationService.MigrationRunner.csproj
COPY Legacy.Maliev.QuotationService.Application/ Legacy.Maliev.QuotationService.Application/
COPY Legacy.Maliev.QuotationService.Domain/ Legacy.Maliev.QuotationService.Domain/
COPY Legacy.Maliev.QuotationService.Data/ Legacy.Maliev.QuotationService.Data/
COPY Legacy.Maliev.QuotationService.MigrationRunner/ Legacy.Maliev.QuotationService.MigrationRunner/
RUN dotnet publish Legacy.Maliev.QuotationService.MigrationRunner/Legacy.Maliev.QuotationService.MigrationRunner.csproj \
    --configuration Release --no-restore --output /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build --chown=$APP_UID:$APP_UID /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "Legacy.Maliev.QuotationService.MigrationRunner.dll"]
