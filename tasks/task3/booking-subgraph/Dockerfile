FROM node:22-alpine
WORKDIR /app
ENV NODE_ENV=production
COPY booking-subgraph/package.json booking-subgraph/package-lock.json ./
RUN npm ci --omit=dev
COPY booking-subgraph/*.js ./
COPY shared /shared
# Контракт booking.proto — из задания 2 (дополнительный контекст task2 в docker-compose.yml)
COPY --from=task2 booking.proto ./proto/booking.proto
USER node
CMD ["node", "index.js"]
