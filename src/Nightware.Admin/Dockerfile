# Nightware admin console. Build from the repository root:
#   docker build -f src/Nightware.Admin/Dockerfile -t nightware-admin .
# Debian-based images on purpose: GraphTimeZone needs ICU and tzdata for Windows<->IANA zone mapping,
# which alpine and chiseled images strip.

FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/ src/
RUN dotnet restore src/Nightware.Admin/Nightware.Admin.csproj
RUN dotnet publish src/Nightware.Admin/Nightware.Admin.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS runtime
RUN apt-get update \
    && apt-get install -y --no-install-recommends tzdata \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
EXPOSE 8080
# Where ASP.NET keeps its data-protection keys, made here so a fresh named volume mounted on it
# (docker-compose.yml's admin-keys) starts out owned by the 'app' user, as Docker copies it.
RUN install -d -o "$APP_UID" -g "$APP_UID" -m 0700 /home/app/.aspnet/DataProtection-Keys
# The aspnet image ships a non-root 'app' user (UID 1654).
USER $APP_UID
# Liveness: GET /healthz/live (anonymous). The image has no curl, so probe it from the orchestrator/proxy.
ENTRYPOINT ["dotnet", "Nightware.Admin.dll"]
