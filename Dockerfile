# syntax=docker/dockerfile:1
#
# One multi-stage Dockerfile for every service:
#   docker build --build-arg SERVICE=Catalog --target catalog -t tickethub/catalog .
# The build stage is shared; each service gets a small final stage because the chiseled runtime image has no shell
# to expand a variable in ENTRYPOINT.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG SERVICE
WORKDIR /src

# Restore first, from project files only, so the dependency layer is cached until a .csproj changes.
COPY global.json nuget.config Directory.Build.props Directory.Packages.props ./
COPY src/TicketHub.ServiceDefaults/TicketHub.ServiceDefaults.csproj src/TicketHub.ServiceDefaults/
COPY src/TicketHub.BuildingBlocks/TicketHub.BuildingBlocks.csproj src/TicketHub.BuildingBlocks/
COPY src/TicketHub.Contracts/TicketHub.Contracts.csproj src/TicketHub.Contracts/
COPY src/TicketHub.Catalog/TicketHub.Catalog.csproj src/TicketHub.Catalog/
COPY src/TicketHub.Booking/TicketHub.Booking.csproj src/TicketHub.Booking/
COPY src/TicketHub.Payments/TicketHub.Payments.csproj src/TicketHub.Payments/
COPY src/TicketHub.Notifications/TicketHub.Notifications.csproj src/TicketHub.Notifications/
COPY src/TicketHub.Gateway/TicketHub.Gateway.csproj src/TicketHub.Gateway/
# Booking compiles its gRPC client from Catalog's proto, which must exist at restore time.
COPY src/TicketHub.Catalog/Protos/ src/TicketHub.Catalog/Protos/
RUN dotnet restore src/TicketHub.${SERVICE}/TicketHub.${SERVICE}.csproj

COPY src/ src/
RUN dotnet publish src/TicketHub.${SERVICE}/TicketHub.${SERVICE}.csproj -c Release -o /app --no-restore

# Chiseled: no shell, no package manager, runs as a non-root user by default.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080

FROM runtime AS catalog
# Catalog also serves gRPC (cleartext HTTP/2) on 8081, configured in its appsettings.json.
EXPOSE 8081
ENTRYPOINT ["dotnet", "TicketHub.Catalog.dll"]

FROM runtime AS booking
ENTRYPOINT ["dotnet", "TicketHub.Booking.dll"]

FROM runtime AS payments
ENTRYPOINT ["dotnet", "TicketHub.Payments.dll"]

FROM runtime AS notifications
ENTRYPOINT ["dotnet", "TicketHub.Notifications.dll"]

FROM runtime AS gateway
ENTRYPOINT ["dotnet", "TicketHub.Gateway.dll"]
