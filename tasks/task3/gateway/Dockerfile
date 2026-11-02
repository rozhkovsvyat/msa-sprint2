FROM node:22-alpine
WORKDIR /app
ENV NODE_ENV=production
COPY gateway/package.json gateway/package-lock.json ./
RUN npm ci --omit=dev
COPY gateway/*.js ./
COPY shared /shared
USER node
CMD ["node", "index.js"]
