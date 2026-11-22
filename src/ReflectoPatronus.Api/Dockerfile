FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ReflectoPatronus.Api.csproj ./
RUN dotnet restore
COPY . ./
RUN dotnet publish -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
# The commit this image was built from, so the console can tell when a newer one exists.
ARG GIT_SHA=unknown
ENV ASPNETCORE_URLS=http://+:8080 RP_BUILD_SHA=$GIT_SHA RP_DEPLOY=docker
EXPOSE 8080
COPY --from=build --chmod=a+rX /out ./
RUN mkdir -p /data/transfer && chown $APP_UID /data/transfer   # large database exports/imports (a volume in compose)
USER $APP_UID
ENTRYPOINT ["dotnet", "ReflectoPatronus.Api.dll"]
