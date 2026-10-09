FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY VERSION ./VERSION
COPY server/ ./server/
RUN dotnet publish server/src/AddonStore.Web/AddonStore.Web.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# the store client comes with the server (S1.15.0): released at start when the server has no newer one
COPY packaging/client-bundle/ ./client-bundle/

# Port 8080 comes from the image (ASPNETCORE_HTTP_PORTS); also setting URLS only logs a warning.
ENV Storage__Data=/data \
    Storage__Root=/data/packages \
    Storage__Devkit=/data/devkit \
    ConnectionStrings__Default="Data Source=/data/pluginstore.db;Cache=Shared" \
    ClientBundle__Path=/app/client-bundle

EXPOSE 8080
VOLUME /data

ENTRYPOINT ["dotnet", "AddonStore.Web.dll"]
