import path from 'node:path';
import { fileURLToPath } from 'node:url';
import grpc from '@grpc/grpc-js';
import protoLoader from '@grpc/proto-loader';
import { existsSync } from 'node:fs';

// Источник данных — booking-service (задание 2) по контракту booking.proto, тот же, что у прокси монолита.
const here = path.dirname(fileURLToPath(import.meta.url));

export function connectBookingService(address, protoPath = existsSync(path.join(here, 'proto/booking.proto'))
  ? path.join(here, 'proto/booking.proto') : path.join(here, '../../task2/booking.proto')) {
  const proto = grpc.loadPackageDefinition(protoLoader.loadSync(protoPath, { keepCase: true, defaults: true }));
  return new proto.booking.BookingService(address, grpc.credentials.createInsecure());
}

export function createListBookings(client, timeoutMs = 3000) {
  return (userId, requestId) => new Promise((resolve, reject) => {
    const deadline = new Date(Date.now() + timeoutMs);
    const metadata = new grpc.Metadata();
    if (requestId) metadata.set('x-request-id', requestId);
    client.ListBookings({ user_id: userId }, metadata, { deadline }, (err, res) =>
      err ? reject(err) : resolve(res.bookings ?? []));
  });
}
