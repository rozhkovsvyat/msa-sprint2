FROM debian:bookworm-slim

WORKDIR /app

# Install dependencies
RUN apt-get update && apt-get install -y \
    curl \
    postgresql-client \
    bash \
    && rm -rf /var/lib/apt/lists/*

# grpcurl — проверка booking-service напрямую по gRPC (задание 2)
COPY --from=fullstorydev/grpcurl:v1.9.3 /bin/grpcurl /usr/local/bin/grpcurl

COPY wait-for-it.sh /usr/local/bin/wait-for-it
COPY init-fixtures.sql regress.sh ./
RUN chmod +x /usr/local/bin/wait-for-it regress.sh

ENTRYPOINT ["./regress.sh"]
