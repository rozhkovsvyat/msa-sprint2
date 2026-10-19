import { ApolloServer } from '@apollo/server';
import { startStandaloneServer } from '@apollo/server/standalone';
import { contextFrom, createGateway, log, validationRules } from './gateway.js';

// Имя booking-subgraph должно совпадать с @override(from: "booking-subgraph") в promocode-subgraph.
const subgraphs = [
  { name: 'booking-subgraph', url: process.env.BOOKING_SUBGRAPH_URL ?? 'http://booking-subgraph:4001' },
  { name: 'hotel-subgraph', url: process.env.HOTEL_SUBGRAPH_URL ?? 'http://hotel-subgraph:4002' },
  { name: 'promocode-subgraph', url: process.env.PROMOCODE_SUBGRAPH_URL ?? 'http://promocode-subgraph:4003' },
];

const server = new ApolloServer({ gateway: createGateway(subgraphs), validationRules });

startStandaloneServer(server, {
  listen: { port: 4000 },
  context: async ({ req, res }) => contextFrom(req, res), // userid и x-request-id передаются подграфам
}).then(({ url }) => {
  log(`ready at ${url}`, { subgraphs: subgraphs.map((s) => s.name) });
});
