FROM node:22-alpine
WORKDIR /app
ENV NODE_ENV=production
COPY hotel-subgraph/package.json hotel-subgraph/package-lock.json ./
RUN npm ci --omit=dev
COPY hotel-subgraph/*.js ./
COPY shared /shared
USER node
CMD ["node", "index.js"]
