FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY energy-graphs.csproj ./
RUN dotnet restore energy-graphs.csproj
COPY . ./
RUN dotnet publish energy-graphs.csproj -c Release -o /app/publish /p:UseAppHost=false \
    && mkdir -p /app/publish/cache \
    && if [ -d cache ]; then cp -a cache/. /app/publish/cache/; fi

FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS final
RUN apt-get update \
    && apt-get install -y --no-install-recommends libfontconfig1 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish ./
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    CACHE_DIR=/app/cache
EXPOSE 8080
ENTRYPOINT ["dotnet", "energy-graphs.dll"]
