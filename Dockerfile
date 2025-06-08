# See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

# Build Vue frontend
FROM node:20 AS web-build
WORKDIR /web
COPY web/package*.json ./
RUN npm ci
COPY web/ .
RUN npm run build

# Build ASP.NET backend
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY api/*.csproj ./api/
RUN dotnet restore ./api/docker-launcher.csproj
COPY api/ ./api/

# Copy frontend build output to backend wwwroot
COPY --from=web-build /web/dist ./api/wwwroot

# Build and publish the backend
WORKDIR /src/api
RUN dotnet build docker-launcher.csproj -c Release -o /app/build
RUN dotnet publish docker-launcher.csproj -c Release -o /app/publish /p:UseAppHost=false

# Final runtime image
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "docker-launcher.dll"]