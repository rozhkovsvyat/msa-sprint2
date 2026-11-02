import { existsSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import grpc from '@grpc/grpc-js';
import protoLoader from '@grpc/proto-loader';

// Источник данных — booking-service (задание 2) по контракту booking.proto, тот же, что у прокси монолита.
const here = path.dirname(fileURLToPath(import.meta.url));
// В образе контракт копируется в proto/, при локальном запуске лежит в каталоге задания 2.
const DEFAULT_PROTO = [
  path.join(here, 'proto/booking.proto'),
  path.join(here, '../../task2/booking.proto'),
].find(existsSync);
const DEFAULT_TIMEOUT_MS = 3000;

export function connectBookingService(address, protoPath = DEFAULT_PROTO) {
  const proto = grpc.loadPackageDefinition(protoLoader.loadSync(protoPath, { keepCase: true, defaults: true }));
  return new proto.booking.BookingService(address, grpc.credentials.createInsecure());
}

export function createListBookings(client, timeoutMs = DEFAULT_TIMEOUT_MS) {
  return (userId, requestId) => new Promise((resolve, reject) => {
    const deadline = new Date(Date.now() + timeoutMs);
    const metadata = new grpc.Metadata();
    if (requestId) metadata.set('x-request-id', requestId);
    client.ListBookings({ user_id: userId }, metadata, { deadline }, (err, res) =>
      err ? reject(err) : resolve(res.bookings ?? []));
  });
}
