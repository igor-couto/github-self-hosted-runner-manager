FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /source
COPY global.json ./
COPY src/RunnerRoom/RunnerRoom.csproj ./src/RunnerRoom/
RUN dotnet restore src/RunnerRoom/RunnerRoom.csproj
COPY src/RunnerRoom/ ./src/RunnerRoom/
COPY uninstall.sh LICENSE ./
RUN dotnet publish src/RunnerRoom/RunnerRoom.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_URLS=http://+:8080
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "RunnerRoom.dll"]
