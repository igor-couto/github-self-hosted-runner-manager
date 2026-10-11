FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /source
COPY global.json ./
COPY src/RunnerRoom/RunnerRoom.csproj ./src/RunnerRoom/
RUN dotnet restore src/RunnerRoom/RunnerRoom.csproj
COPY src/RunnerRoom/ ./src/RunnerRoom/
COPY uninstall.sh LICENSE ./
ARG VERSION=0.0.0-dev
RUN dotnet publish src/RunnerRoom/RunnerRoom.csproj -c Release -o /app --no-restore -p:Version="$VERSION"

FROM mcr.microsoft.com/dotnet/aspnet:9.0
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_URLS=http://+:8080
USER $APP_UID
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 CMD curl --noproxy '*' --fail --silent http://127.0.0.1:8080/healthz || exit 1
ENTRYPOINT ["dotnet", "RunnerRoom.dll"]
