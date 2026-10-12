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
# copyright and license texts of every package in /app (S1.25.0, tools/gen_third_party_notices.py)
COPY server/THIRD-PARTY-NOTICES.md ./THIRD-PARTY-NOTICES.md
COPY LICENSE ./LICENSE
LABEL com.tungsten.addonstore.third-party-notices="/app/THIRD-PARTY-NOTICES.md" \
      com.tungsten.addonstore.base-image-licenses="The base image (Ubuntu 24.04 packages, some under GPL or LGPL) is passed on unchanged under its own licenses: texts in /usr/share/doc/*/copyright, sources via https://launchpad.net/ubuntu and https://github.com/dotnet/dotnet-docker"

# Port 8080 comes from the image (ASPNETCORE_HTTP_PORTS); also setting URLS only logs a warning.
ENV Storage__Data=/data \
    Storage__Root=/data/packages \
    Storage__Devkit=/data/devkit \
    ConnectionStrings__Default="Data Source=/data/pluginstore.db;Cache=Shared" \
    ClientBundle__Path=/app/client-bundle

EXPOSE 8080
VOLUME /data

ENTRYPOINT ["dotnet", "AddonStore.Web.dll"]
