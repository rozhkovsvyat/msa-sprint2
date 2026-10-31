# Контекст сборки — tasks/task2:
#   docker build -f booking-history-service/Dockerfile -t booking-history-service:<tag> .
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa AS build
WORKDIR /src
COPY booking-history-service/src/BookingHistoryService.csproj booking-history-service/src/
RUN dotnet restore booking-history-service/src/BookingHistoryService.csproj
COPY booking-history-service/src/ booking-history-service/src/
RUN dotnet publish booking-history-service/src/BookingHistoryService.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:d5633778c754e89a386882eddf8dbee6942447a8a4b8267c19cf1e1158d0b0ac
WORKDIR /app
COPY --from=build /app ./
ENV HTTP_PORT=8080
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "BookingHistoryService.dll"]
