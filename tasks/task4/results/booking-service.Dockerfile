# Контекст сборки — tasks/task2 (нужны контракты booking.proto и booking-admin.proto):
#   docker build -f booking-service/Dockerfile -t booking-service:<tag> .
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa AS build
WORKDIR /src
COPY booking.proto booking-admin.proto ./
COPY booking-service/src/BookingService/BookingService.csproj booking-service/src/BookingService/
RUN dotnet restore booking-service/src/BookingService/BookingService.csproj
COPY booking-service/src/BookingService/ booking-service/src/BookingService/
RUN dotnet publish booking-service/src/BookingService/BookingService.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:d5633778c754e89a386882eddf8dbee6942447a8a4b8267c19cf1e1158d0b0ac
WORKDIR /app
COPY --from=build /app ./
# HTTP 8080: /ping, /ready, /version, /feature при ENABLE_FEATURE_X=true; gRPC 9090
ENV HTTP_PORT=8080 GRPC_PORT=9090
EXPOSE 8080 9090
USER app
ENTRYPOINT ["dotnet", "BookingService.dll"]
