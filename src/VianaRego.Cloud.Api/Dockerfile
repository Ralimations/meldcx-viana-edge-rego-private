# Multi-stage Dockerfile for VianaRego Cloud API
# This file should be built with the solution root as the context.
# Example: docker build -t viana-cloud-api -f src/VianaRego.Cloud.Api/Dockerfile .

# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /app

# Copy projects and restore dependencies
COPY ["VianaRego.slnx", "./"]
COPY ["src/VianaRego.Cloud.Api/VianaRego.Cloud.Api.csproj", "src/VianaRego.Cloud.Api/"]
COPY ["src/VianaRego.Shared/VianaRego.Shared.csproj", "src/VianaRego.Shared/"]
RUN dotnet restore "src/VianaRego.Cloud.Api/VianaRego.Cloud.Api.csproj"

# Copy everything else and build
COPY . .
WORKDIR "/app/src/VianaRego.Cloud.Api"
RUN dotnet publish "VianaRego.Cloud.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

# Create data directory for SQLite
RUN mkdir -p /app/data && chown -R $APP_UID /app/data

COPY --from=build /app/publish .

# Environment variables
ENV ASPNETCORE_URLS=http://+:8080
ENV DATA_PATH=/app/data

ENTRYPOINT ["dotnet", "VianaRego.Cloud.Api.dll"]
