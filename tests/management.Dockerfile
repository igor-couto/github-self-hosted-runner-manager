FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /source
RUN apt-get update && apt-get install -y --no-install-recommends gcc libc6-dev && rm -rf /var/lib/apt/lists/*
COPY . .
RUN dotnet publish tests/RunnerRoom.Tests -c Release -o /checks
RUN cc tests/runner-stub.c -o /checks/runner-stub
FROM mcr.microsoft.com/dotnet/aspnet:9.0
COPY --from=build /checks /checks
ENV RUNNER_ROOM_STUB=/checks/runner-stub
USER $APP_UID
ENTRYPOINT ["dotnet", "/checks/RunnerRoom.Tests.dll"]
