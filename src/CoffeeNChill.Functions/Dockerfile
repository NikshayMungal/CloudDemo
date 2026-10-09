# =====================================================================
# CoffeeNChill Azure Functions image (multi-stage)
#   Stage 1: build/publish with the .NET 8 SDK
#   Stage 2: run on the official Azure Functions isolated-worker runtime
# Build:  docker build -t <dockerhub_username>/coffeenchill-functions:v1.0 .
# =====================================================================

# ---------- Stage 1: build ----------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy the project file first so the restore layer is cached between builds
COPY CoffeeNChill.Functions.csproj ./
RUN dotnet restore

# Copy the rest of the source and publish a Release build
COPY . .
RUN dotnet publish CoffeeNChill.Functions.csproj -c Release -o /home/site/wwwroot --no-restore

# ---------- Stage 2: runtime ----------
FROM mcr.microsoft.com/azure-functions/dotnet-isolated:4-dotnet-isolated8.0 AS runtime

ENV AzureWebJobsScriptRoot=/home/site/wwwroot \
    AzureFunctionsJobHost__Logging__Console__IsEnabled=true \
    FUNCTIONS_WORKER_RUNTIME=dotnet-isolated

COPY --from=build /home/site/wwwroot /home/site/wwwroot

# The Functions host listens on port 80 inside the container
EXPOSE 80
