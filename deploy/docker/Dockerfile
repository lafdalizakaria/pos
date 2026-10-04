# Server images (API, back-office, migrator) from one multi-stage file:
#   docker build -f deploy/docker/Dockerfile --target api        -t newrest-pos-api .
#   docker build -f deploy/docker/Dockerfile --target backoffice -t newrest-pos-backoffice .
#   docker build -f deploy/docker/Dockerfile --target migrator   -t newrest-pos-migrator .
# The register (WPF) and its vision service are Windows packages: see deploy/register.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
# Behind a TLS-inspecting corporate proxy: docker build --secret id=extra_ca,src=<proxy-ca.crt> ...
RUN --mount=type=secret,id=extra_ca,required=false \
    if [ -f /run/secrets/extra_ca ]; then cp /run/secrets/extra_ca /usr/local/share/ca-certificates/extra-ca.crt && update-ca-certificates; fi
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props Newrest.Pos.slnx ./
COPY src/ src/
RUN dotnet publish src/Newrest.Pos.Api -c Release -o /out/api /p:UseAppHost=false \
 && dotnet publish src/Newrest.Pos.BackOffice -c Release -o /out/backoffice /p:UseAppHost=false \
 && dotnet publish src/Newrest.Pos.Migrator -c Release -o /out/migrator /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS api
WORKDIR /app
COPY --from=build /out/api .
ENV ASPNETCORE_URLS=http://+:8080 ASPNETCORE_ENVIRONMENT=Production DOTNET_gcServer=1
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Newrest.Pos.Api.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS backoffice
WORKDIR /app
COPY --from=build /out/backoffice .
ENV ASPNETCORE_URLS=http://+:8080 ASPNETCORE_ENVIRONMENT=Production
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Newrest.Pos.BackOffice.dll"]

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS migrator
WORKDIR /app
COPY --from=build /out/migrator .
ENV DOTNET_ENVIRONMENT=Production
USER $APP_UID
ENTRYPOINT ["dotnet", "Newrest.Pos.Migrator.dll"]
