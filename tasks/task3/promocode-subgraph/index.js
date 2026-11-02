import { ApolloServer } from '@apollo/server';
import { startStandaloneServer } from '@apollo/server/standalone';
import { createMonolithClient } from './monolith-client.js';
import { contextFrom, log, schema } from './schema.js';

const MONOLITH_URL = process.env.MONOLITH_URL ?? 'http://hotelio-monolith:8080';
const api = createMonolithClient({ baseUrl: MONOLITH_URL });

const server = new ApolloServer({ schema });

startStandaloneServer(server, {
  listen: { port: 4003 },
  context: async (ctx) => contextFrom(ctx, api),
}).then(({ url }) => {
  log(`ready at ${url}`, { monolith: MONOLITH_URL });
});
