import http from 'node:http';
import { once } from 'node:events';
import { fileURLToPath } from 'node:url';
import { ApolloServer } from '@apollo/server';
import { startStandaloneServer } from '@apollo/server/standalone';
import grpc from '@grpc/grpc-js';
import protoLoader from '@grpc/proto-loader';
import { connectBookingService, createListBookings } from '../booking-subgraph/booking-client.js';
import { contextFrom as bookingContext, schema as bookingSchema } from '../booking-subgraph/schema.js';
import { contextFrom as gatewayContext, createGateway, ForwardingDataSource, validationRules } from '../gateway/gateway.js';
import { contextFrom as hotelContext, createHotelCatalog, createMonolithClient as hotelClient, schema as hotelSchema } from '../hotel-subgraph/schema.js';
import { contextFrom as promoContext, createMonolithClient as promoClient, schema as promoSchema } from '../promocode-subgraph/schema.js';

export const booking = (id, userId, hotelId, promoCode = '', discount = 0) => ({
  id, user_id: userId, hotel_id: hotelId, promo_code: promoCode, discount_percent: discount, created_at: '2026-10-19T10:00:00Z',
});

export async function post(url, query, { userId = 'alice', requestId, variables } = {}) {
  const headers = { 'content-type': 'application/json' };
  if (userId) headers.userid = userId;
  if (requestId) headers['x-request-id'] = requestId;
  const response = await fetch(url, { method: 'POST', headers, body: JSON.stringify({ query, variables }) });
  return { status: response.status, requestId: response.headers.get('x-request-id'), body: await response.json() };
}

/**
 * Настоящие шлюз, подграфы и транспорт HTTP/gRPC на локальных портах. REST монолита и gRPC booking-service
 * заменены заглушками с фикстурами; журналы и обращения к ним записываются для проверок.
 */
export async function startFixture({ dataSource = ForwardingDataSource, listBookings } = {}) {
  const events = [];
  const restCalls = [];
  const grpcCalls = [];
  const subgraphRequests = [];
  const failedHotels = new Set();
  const promos = new Map([
    ['SUMMER', { code: 'SUMMER', discount: 20, vipOnly: false, expired: false }],
    ['OLD', { code: 'OLD', discount: 5, vipOnly: false, expired: true }],
    ['VIP', { code: 'VIP', discount: 30, vipOnly: true, expired: false }],
  ]);
  const bookings = [
    booking('b1', 'alice', 'h1', 'SUMMER', 20),
    booking('b2', 'alice', 'h2', 'OLD', 0),
    booking('b3', 'alice', 'h1', 'VIP', 0),
    booking('b4', 'alice', 'h3'),
    booking('b5', 'bob', 'h1', 'SUMMER', 20),
  ];
  const logTo = (svc) => (msg, extra) => events.push({ svc, msg, ...extra });
  const cleanup = [];
  const stop = async () => { while (cleanup.length) await cleanup.pop()(); };

  try {
    const monolith = http.createServer((req, res) => {
      const url = new URL(req.url, 'http://monolith');
      restCalls.push({ path: req.url, requestId: req.headers['x-request-id'] });
      res.setHeader('content-type', 'application/json');
      const hotel = /^\/api\/hotels\/(.+)$/.exec(url.pathname);
      const promo = /^\/api\/promos\/([^/]+)$/.exec(url.pathname);
      if (hotel) {
        const id = decodeURIComponent(hotel[1]);
        if (failedHotels.has(id)) res.writeHead(503).end('{}');
        else res.end(JSON.stringify({ id, city: 'Seoul', rating: 4.7, description: 'Hotel ' + id, operational: true }));
      } else if (url.pathname === '/api/promos/validate') {
        const p = promos.get(url.searchParams.get('code'));
        res.writeHead(p && !p.expired && !p.vipOnly ? 200 : 400).end('{}');
      } else if (promo && promos.has(decodeURIComponent(promo[1]))) {
        res.end(JSON.stringify(promos.get(decodeURIComponent(promo[1]))));
      } else {
        res.writeHead(404).end('{}');
      }
    });
    monolith.listen(0, '127.0.0.1');
    await once(monolith, 'listening');
    cleanup.push(() => new Promise((resolve) => { monolith.closeAllConnections(); monolith.close(resolve); }));
    const monolithUrl = `http://127.0.0.1:${monolith.address().port}`;

    const protoPath = fileURLToPath(new URL('../../task2/booking.proto', import.meta.url));
    const proto = grpc.loadPackageDefinition(protoLoader.loadSync(protoPath, { keepCase: true, defaults: true }));
    const bookingService = new grpc.Server();
    bookingService.addService(proto.booking.BookingService.service, {
      ListBookings(call, callback) {
        grpcCalls.push({ userId: call.request.user_id, requestId: call.metadata.get('x-request-id')[0] });
        callback(null, { bookings: bookings.filter((b) => b.user_id === call.request.user_id) });
      },
    });
    const grpcPort = await new Promise((resolve, reject) => bookingService.bindAsync('127.0.0.1:0',
      grpc.ServerCredentials.createInsecure(), (error, port) => (error ? reject(error) : resolve(port))));
    cleanup.push(() => bookingService.forceShutdown());
    const client = connectBookingService(`127.0.0.1:${grpcPort}`, protoPath);
    cleanup.push(() => client.close());

    async function start(options, context) {
      const server = new ApolloServer(options);
      const { url } = await startStandaloneServer(server, { listen: { host: '127.0.0.1', port: 0 }, context });
      cleanup.push(() => server.stop());
      return url;
    }
    const list = listBookings ?? createListBookings(client);
    const catalog = createHotelCatalog({ fetchHotel: hotelClient({ baseUrl: monolithUrl }), ttlMs: 0 });
    const api = promoClient({ baseUrl: monolithUrl });
    const subgraphs = [
      { name: 'booking-subgraph', url: await start({ schema: bookingSchema },
        async ({ req, res }) => bookingContext(req, list, res, logTo('booking-subgraph'))) },
      { name: 'hotel-subgraph', url: await start({ schema: hotelSchema },
        async ({ req, res }) => hotelContext(req, catalog, res, logTo('hotel-subgraph'))) },
      { name: 'promocode-subgraph', url: await start({ schema: promoSchema },
        async ({ req, res }) => promoContext(req, api, res, logTo('promocode-subgraph'))) },
    ];
    const names = new Map(subgraphs.map((s) => [s.url, s.name]));
    class RecordedDataSource extends dataSource {
      willSendRequest(args) {
        super.willSendRequest?.(args);
        subgraphRequests.push({ subgraph: names.get(this.url), query: args.request.query, variables: args.request.variables });
      }
    }
    const gatewayUrl = await start({ gateway: createGateway(subgraphs, RecordedDataSource), validationRules },
      async ({ req, res }) => gatewayContext(req, res, logTo('apollo-gateway')));
    return { gatewayUrl, events, restCalls, grpcCalls, subgraphRequests, failedHotels, promos, bookings, stop };
  } catch (error) {
    await stop();
    throw error;
  }
}
