import { ApolloServer } from '@apollo/server';
import { startStandaloneServer } from '@apollo/server/standalone';
import { connectBookingService, createListBookings } from './booking-client.js';
import { contextFrom, log, schema } from './schema.js';

const BOOKING_GRPC = process.env.BOOKING_GRPC ?? 'booking-service:9090';
const listBookings = createListBookings(connectBookingService(BOOKING_GRPC));

const server = new ApolloServer({ schema });

startStandaloneServer(server, {
  listen: { port: 4001 },
  context: async ({ req, res }) => contextFrom(req, listBookings, res),
}).then(({ url }) => {
  log(`ready at ${url}`, { bookingGrpc: BOOKING_GRPC });
});
