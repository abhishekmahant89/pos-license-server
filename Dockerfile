# 1. Build Stage with .NET 10 SDK
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project file and restore
COPY ["PosLicenseServer.csproj", "./"]
RUN dotnet restore "PosLicenseServer.csproj"

# Copy full source and publish
COPY . .
RUN dotnet publish "PosLicenseServer.csproj" -c Release -o /app/publish /p:UseAppHost=false

# 2. Runtime Stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Create persistent storage folder for SQLite database
RUN mkdir -p /app/Data /app/Logs

COPY --from=build /app/publish .

# Render and Cloud platforms pass PORT dynamically
ENV PORT=5055
ENV DATABASE_PATH=/app/Data/licenses.db
EXPOSE 5055

ENTRYPOINT ["dotnet", "PosLicenseServer.dll"]
