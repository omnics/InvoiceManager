# Container image for the admin website, published to a public ghcr.io package and pulled
# anonymously by Azure Container Apps.
#
# Build context MUST be the repository root (the app has project references across src/), e.g.
#   docker build -f src/InvoiceManager.AdminWeb/Dockerfile -t ghcr.io/<owner>/invoicemanager-adminweb:<tag> .
#
# The 11.0 tags track the latest net11.0 release (RC builds included), which must satisfy the
# SDK pinned in global.json. The 11.0-preview tags stopped at preview 7 once the RCs shipped.
# If a restore fails on an SDK mismatch, check what 11.0 points to on mcr.microsoft.com.
ARG DOTNET_VERSION=11.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

# Copy NuGet config + the full source tree, then restore/publish. The app's transitive project
# references make a per-project copy fragile, so copy everything for a reliable build.
COPY NuGet.Config ./
COPY global.json ./
COPY src/ ./src/

RUN dotnet restore src/InvoiceManager.AdminWeb/InvoiceManager.AdminWeb.csproj
RUN dotnet publish src/InvoiceManager.AdminWeb/InvoiceManager.AdminWeb.csproj \
    --configuration Release \
    --no-restore \
    --output /app

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
WORKDIR /app
COPY --from=build /app ./

# Matches the Container App ingress target_port and the ASPNETCORE_HTTP_PORTS app setting.
EXPOSE 8080

ENTRYPOINT ["dotnet", "InvoiceManager.AdminWeb.dll"]
