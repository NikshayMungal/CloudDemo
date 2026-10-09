# =====================================================================
# CoffeeNChill Azurite image (Azure Storage emulator: Blob / Queue / Table)
# Build:  docker build -t <dockerhub_username>/coffeenchill-azurite:v1.0 ./docker/azurite
# Ports:  10000 = Blob, 10001 = Queue, 10002 = Table
# =====================================================================
FROM mcr.microsoft.com/azure-storage/azurite:3.33.0

LABEL org.opencontainers.image.title="coffeenchill-azurite" \
      org.opencontainers.image.description="Azurite storage emulator preconfigured for the CoffeeNChill project"

# Persist emulator data in /data (mount a volume here to keep data between runs)
VOLUME /data

EXPOSE 10000 10001 10002

# --skipApiVersionCheck lets newer Azure SDK versions talk to Azurite
# --loose ignores unsupported headers instead of failing the request
CMD ["azurite", \
     "--location", "/data", \
     "--blobHost", "0.0.0.0", \
     "--queueHost", "0.0.0.0", \
     "--tableHost", "0.0.0.0", \
     "--skipApiVersionCheck", \
     "--loose"]
