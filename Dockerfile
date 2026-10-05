FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /source
COPY RunnerRoom.csproj global.json ./
RUN dotnet restore
COPY *.cs ./
COPY wwwroot ./wwwroot
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_URLS=http://+:8080
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "RunnerRoom.dll"]
