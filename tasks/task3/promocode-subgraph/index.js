import { ApolloServer } from '@apollo/server';
import { startStandaloneServer } from '@apollo/server/standalone';
import { contextFrom, createMonolithClient, log, schema } from './schema.js';

const MONOLITH_URL = process.env.MONOLITH_URL ?? 'http://hotelio-monolith:8080';
const api = createMonolithClient({ baseUrl: MONOLITH_URL });

const server = new ApolloServer({ schema });

startStandaloneServer(server, {
  listen: { port: 4003 },
  context: async ({ req, res }) => contextFrom(req, api, res),
}).then(({ url }) => {
  log(`ready at ${url}`, { monolith: MONOLITH_URL });
});
