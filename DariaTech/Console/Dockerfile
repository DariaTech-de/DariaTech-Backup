# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
WORKDIR /src
# .dockerignore excludes secrets, upstream engine, build artifacts and proprietary code.
COPY . .
RUN --mount=type=secret,id=proxy_bundle \
    if [ -f /run/secrets/proxy_bundle ]; then export SSL_CERT_FILE=/run/secrets/proxy_bundle; fi; \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet restore DariaTech/Console/DariaTech.Console.csproj --locked-mode && \
    dotnet publish DariaTech/Console/DariaTech.Console.csproj -c Release --no-restore -o /out
FROM mcr.microsoft.com/dotnet/aspnet:10.0.12-noble AS runtime
WORKDIR /app
COPY --from=build /out .
RUN chmod -R a+rX /app && mkdir -p /var/lib/dariatech/keys && chown -R app:app /var/lib/dariatech
USER app
ENV ASPNETCORE_HTTP_PORTS=8080 DOTNET_CLI_TELEMETRY_OPTOUT=1
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=10s --start-period=30s CMD dotnet DariaTech.Console.dll --healthcheck
ENTRYPOINT ["dotnet", "DariaTech.Console.dll"]
