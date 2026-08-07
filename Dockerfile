FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props Alven.Bridge.slnx ./
COPY src/Alven.Bridge/Alven.Bridge.csproj src/Alven.Bridge/
RUN dotnet restore src/Alven.Bridge/Alven.Bridge.csproj
COPY src/Alven.Bridge src/Alven.Bridge
RUN dotnet publish src/Alven.Bridge/Alven.Bridge.csproj -c Release --no-restore -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
LABEL org.opencontainers.image.source="https://github.com/IlyaBaikou/Alven-Bridge" \
      org.opencontainers.image.description="Outbound-only Alven private AI and family storage bridge" \
      org.opencontainers.image.licenses="MIT"
RUN addgroup -S alven && adduser -S -G alven -u 10001 alven
WORKDIR /app
COPY --from=build /app ./
RUN mkdir -p /var/lib/alven-bridge && chown -R alven:alven /var/lib/alven-bridge /app
USER alven
ENV ASPNETCORE_URLS=http://0.0.0.0:7433
ENV Bridge__StateDirectory=/var/lib/alven-bridge
EXPOSE 7433
HEALTHCHECK --interval=30s --timeout=5s --retries=3 \
  CMD wget -q --spider http://127.0.0.1:7433/health/live || exit 1
ENTRYPOINT ["dotnet", "Alven.Bridge.dll"]
