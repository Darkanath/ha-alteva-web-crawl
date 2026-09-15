# Use the official .NET runtime as a parent image
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS base
WORKDIR /app

# Use the SDK image for building the application
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
# Copy project files and restore dependencies
# This leverages Docker cache to save time on subsequent builds
COPY ["src/Alteva.CrawlWorker/Alteva.CrawlWorker.csproj", "src/Alteva.CrawlWorker/"]
COPY ["src/Alteva.Infrastructure/Alteva.Infrastructure.csproj", "src/Alteva.Infrastructure/"]
COPY ["src/Alteva.Domain/Alteva.Domain.csproj", "src/Alteva.Domain/"]
RUN dotnet restore "src/Alteva.CrawlWorker/Alteva.CrawlWorker.csproj"

# Copy the rest of the source code
COPY . .
WORKDIR "/src/src/Alteva.CrawlWorker"
RUN dotnet build "Alteva.CrawlWorker.csproj" -c Release -o /app/build

# Publish the application
FROM build AS publish
RUN dotnet publish "Alteva.CrawlWorker.csproj" -c Release -o /app/publish

# Final stage: build the runtime image
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Alteva.CrawlWorker.dll"]
