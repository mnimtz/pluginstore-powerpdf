FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY VERSION ./VERSION
COPY server/ ./server/
RUN dotnet publish server/src/AddonStore.Web/AddonStore.Web.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080 \
    Storage__Data=/data \
    Storage__Root=/data/packages \
    Storage__Devkit=/data/devkit \
    ConnectionStrings__Default="Data Source=/data/pluginstore.db;Cache=Shared"

EXPOSE 8080
VOLUME /data

ENTRYPOINT ["dotnet", "AddonStore.Web.dll"]
