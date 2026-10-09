# Azurite (Azure Storage emulator) packaged as coffeenchill-azurite
FROM mcr.microsoft.com/azure-storage/azurite
EXPOSE 10000 10001 10002
# 10000 = Blob, 10001 = Queue, 10002 = Table (real Azurite port assignment)
CMD ["azurite", "--blobHost", "0.0.0.0", "--queueHost", "0.0.0.0", "--tableHost", "0.0.0.0", "--location", "/data", "--loose", "--skipApiVersionCheck"]
