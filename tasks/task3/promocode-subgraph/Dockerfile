FROM node:22-alpine
WORKDIR /app
ENV NODE_ENV=production
COPY promocode-subgraph/package.json promocode-subgraph/package-lock.json ./
RUN npm ci --omit=dev
COPY promocode-subgraph/*.js ./
COPY shared /shared
USER node
CMD ["node", "index.js"]
