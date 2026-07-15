# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
ENV GITHUB_ACTIONS=false
COPY nuget.config .
COPY .dependencies/ .dependencies/
COPY Legacy.Maliev.QuotationService.Api/Legacy.Maliev.QuotationService.Api.csproj Legacy.Maliev.QuotationService.Api/
COPY Legacy.Maliev.QuotationService.Application/Legacy.Maliev.QuotationService.Application.csproj Legacy.Maliev.QuotationService.Application/
COPY Legacy.Maliev.QuotationService.Domain/Legacy.Maliev.QuotationService.Domain.csproj Legacy.Maliev.QuotationService.Domain/
COPY Legacy.Maliev.QuotationService.Data/Legacy.Maliev.QuotationService.Data.csproj Legacy.Maliev.QuotationService.Data/
RUN dotnet restore Legacy.Maliev.QuotationService.Api/Legacy.Maliev.QuotationService.Api.csproj \
    -p:MalievWorkspaceRoot=/src/.dependencies
COPY . .
RUN dotnet publish Legacy.Maliev.QuotationService.Api/Legacy.Maliev.QuotationService.Api.csproj \
    --configuration Release --no-restore --output /app /p:UseAppHost=false \
    -p:MalievWorkspaceRoot=/src/.dependencies

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build --chown=$APP_UID:$APP_UID /app .
USER $APP_UID
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "Legacy.Maliev.QuotationService.Api.dll"]
