import { ApolloServer } from '@apollo/server';
import { startStandaloneServer } from '@apollo/server/standalone';
import { createHotelCatalog } from './catalog.js';
import { createMonolithClient } from './monolith-client.js';
import { contextFrom, log, schema } from './schema.js';

const MONOLITH_URL = process.env.MONOLITH_URL ?? 'http://hotelio-monolith:8080';
const CACHE_TTL_MS = Number(process.env.HOTEL_CACHE_TTL_MS ?? 30_000);
const catalog = createHotelCatalog({ fetchHotel: createMonolithClient({ baseUrl: MONOLITH_URL }), ttlMs: CACHE_TTL_MS });

const server = new ApolloServer({ schema });

startStandaloneServer(server, {
  listen: { port: 4002 },
  context: async (ctx) => contextFrom(ctx, catalog),
}).then(({ url }) => {
  log(`ready at ${url}`, { monolith: MONOLITH_URL, cacheTtlMs: CACHE_TTL_MS });
});
