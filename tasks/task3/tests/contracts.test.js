import test, { after, before } from 'node:test';
import assert from 'node:assert/strict';
import { RemoteGraphQLDataSource } from '@apollo/gateway';
import { booking, post, startFixture } from './fixture.js';
import { contextFrom as bookingContext, resolvers as bookingResolvers } from '../booking-subgraph/schema.js';
import { createHotelCatalog } from '../hotel-subgraph/schema.js';

const MY_BOOKINGS = `query MyBookings {
  bookingsByUser(userId: "alice") {
    id promoCode discountPercent
    discountInfo { code discountPercent isValid }
    hotel { id city }
  }
}`;

let fx;
before(async () => { fx = await startFixture(); });
after(() => fx.stop());

test('Свои бронирования приходят одним запросом вместе с отелями и промокодами', async () => {
  const { body } = await post(fx.gatewayUrl, MY_BOOKINGS);
  assert.equal(body.errors, undefined);
  const rows = body.data.bookingsByUser;
  assert.deepEqual(rows.map((b) => b.id), ['b1', 'b2', 'b3', 'b4']);
  assert.deepEqual(rows.map((b) => b.hotel), [{ id: 'h1', city: 'Seoul' }, { id: 'h2', city: 'Seoul' },
    { id: 'h1', city: 'Seoul' }, { id: 'h3', city: 'Seoul' }]);
  // Истёкший и VIP-промокод для обычного пользователя сейчас не действуют.
  assert.deepEqual(rows.map((b) => b.discountInfo?.isValid ?? null), [true, false, false, null]);
});

test('@override: discountPercent отдаёт promocode-subgraph по справочнику промокодов', async () => {
  const { body } = await post(fx.gatewayUrl, MY_BOOKINGS);
  assert.deepEqual(body.data.bookingsByUser.map((b) => b.discountPercent), [20, 5, 30, 0]);
  // Шлюз передаёт promocode-subgraph промокод бронирования (@requires); booking-subgraph поле не запрашивает.
  const representations = fx.subgraphRequests.filter((r) => r.subgraph === 'promocode-subgraph')
    .flatMap((r) => r.variables?.representations ?? []);
  assert.ok(representations.some((ref) => ref.id === 'b1' && ref.promoCode === 'SUMMER'));
  assert.ok(representations.every((ref) => !('discountPercent' in ref)));
  assert.ok(!fx.subgraphRequests.some((r) => r.subgraph === 'booking-subgraph' && r.query.includes('discountPercent')));
  assert.ok(fx.events.some((e) => e.svc === 'promocode-subgraph' && e.msg === 'discountPercent resolved'
    && e.bookingId === 'b1' && e.discountPercent === 20));

  fx.promos.get('SUMMER').discount = 25;
  fx.promos.delete('OLD');
  try {
    const changed = await post(fx.gatewayUrl, MY_BOOKINGS);
    assert.equal(changed.body.errors, undefined);
    assert.deepEqual(changed.body.data.bookingsByUser.map((b) => b.discountPercent), [25, null, 30, 0]);
    assert.equal(changed.body.data.bookingsByUser[1].discountInfo, null);
  } finally {
    fx.promos.get('SUMMER').discount = 20;
    fx.promos.set('OLD', { code: 'OLD', discount: 5, vipOnly: false, expired: true });
  }
});

test('ACL: чужие бронирования и запрос без userid отклоняются, чужое бронирование по id не находится', async () => {
  const foreign = await post(fx.gatewayUrl, MY_BOOKINGS, { userId: 'bob' });
  assert.equal(foreign.body.errors[0].extensions.code, 'FORBIDDEN');
  assert.equal(foreign.body.data, null);
  const anonymous = await post(fx.gatewayUrl, MY_BOOKINGS, { userId: null });
  assert.equal(anonymous.body.errors[0].extensions.code, 'UNAUTHENTICATED');
  const own = await post(fx.gatewayUrl, '{ booking(id: "b1") { id discountPercent } }');
  assert.deepEqual(own.body.data.booking, { id: 'b1', discountPercent: 20 });
  const other = await post(fx.gatewayUrl, '{ booking(id: "b5") { id } }');
  assert.equal(other.body.errors, undefined);
  assert.equal(other.body.data.booking, null);
});

test('ACL: без проброса userid шлюзом даже владелец получает UNAUTHENTICATED', async () => {
  const plain = await startFixture({ dataSource: RemoteGraphQLDataSource });
  try {
    const { body } = await post(plain.gatewayUrl, MY_BOOKINGS);
    assert.equal(body.errors[0].extensions.code, 'UNAUTHENTICATED');
    assert.equal(body.data, null);
  } finally { await plain.stop(); }
});

test('ACL: userid из пробелов или с управляющими символами отклоняется до вызова booking-service', async () => {
  for (const userId of [' ', '\t', ' ', 'alice\n', ['alice', 'bob']]) {
    let called = false;
    const context = bookingContext({ headers: { userid: userId } }, async () => { called = true; return []; }, undefined, () => {});
    await assert.rejects(() => bookingResolvers.Query.bookingsByUser(null, { userId }, context),
      (error) => error.extensions?.code === 'UNAUTHENTICATED');
    assert.equal(called, false);
  }
});

test('ACL: чужое бронирование в ответе booking-service не попадает к пользователю', async () => {
  const wrong = await startFixture({ listBookings: async () => [booking('x1', 'bob', 'h1')] });
  try {
    const { body } = await post(wrong.gatewayUrl, MY_BOOKINGS);
    assert.equal(body.errors[0].extensions.code, 'FORBIDDEN');
    assert.equal(body.data, null);
  } finally { await wrong.stop(); }
});

test('Отказ одного отеля: ошибка с path на hotel, остальные бронирования и скидки целы', async () => {
  fx.failedHotels.add('h2');
  try {
    const { body } = await post(fx.gatewayUrl, MY_BOOKINGS);
    assert.deepEqual(body.errors.map((e) => [e.path, e.extensions.code]),
      [[['bookingsByUser', 1, 'hotel'], 'HOTEL_UNAVAILABLE']]);
    const rows = body.data.bookingsByUser;
    assert.equal(rows.length, 4);
    assert.deepEqual(rows[1].hotel, { id: 'h2', city: null });
    assert.equal(rows[1].discountPercent, 5);
    assert.deepEqual(rows[0].hotel, { id: 'h1', city: 'Seoul' });
  } finally { fx.failedHotels.clear(); }
});

test('Лимиты: глубина, псевдонимы и длина hotelsByIds отклоняются до обращения к источникам', async () => {
  const calls = () => fx.restCalls.length + fx.grpcCalls.length;
  const before = calls();
  const aliases = Array.from({ length: 21 }, (_, i) => `a${i}: hotelsByIds(ids: ["h1"]) { id }`).join(' ');
  for (const query of [
    `{ __type(name: "Query") { ${'ofType { '.repeat(8)} name ${'}'.repeat(8)} } }`,
    `{ ${aliases} }`,
    `{ ...Many } fragment Many on Query { ${aliases} }`,
  ]) {
    const { status, body } = await post(fx.gatewayUrl, query);
    assert.equal(status, 400);
    assert.equal(body.errors[0].extensions.code, 'GRAPHQL_VALIDATION_FAILED');
  }
  const { body } = await post(fx.gatewayUrl, 'query($ids: [ID!]!) { hotelsByIds(ids: $ids) { id } }',
    { variables: { ids: Array(51).fill('h1') } });
  assert.equal(body.errors[0].extensions.code, 'BAD_USER_INPUT');
  assert.equal(calls(), before);
});

test('51 бронирование с отелями и скидками читается через Federation целиком', async () => {
  const many = await startFixture();
  try {
    many.bookings.splice(0, Infinity, ...Array.from({ length: 51 }, (_, i) => booking(`b${i}`, 'alice', `h${i}`, 'SUMMER', 20)));
    const { body } = await post(many.gatewayUrl, '{ bookingsByUser(userId: "alice") { id discountPercent hotel { id city } } }');
    assert.equal(body.errors, undefined);
    assert.equal(body.data.bookingsByUser.length, 51);
    assert.ok(body.data.bookingsByUser.every((b, i) => b.hotel.id === `h${i}` && b.hotel.city === 'Seoul' && b.discountPercent === 20));
    // Один REST-вызов на отель и один на промокод: DataLoader не делает лишних обращений к монолиту.
    assert.equal(many.restCalls.filter((c) => c.path.startsWith('/api/hotels/')).length, 51);
    assert.equal(many.restCalls.filter((c) => c.path.startsWith('/api/promos/SUMMER')).length, 1);
  } finally { await many.stop(); }
});

test('x-request-id клиента проходит через шлюз, подграфы, REST и gRPC; недопустимый заменяется новым', async () => {
  const kept = await post(fx.gatewayUrl, MY_BOOKINGS, { requestId: 'client-id-1' });
  assert.equal(kept.requestId, 'client-id-1');
  for (const svc of ['apollo-gateway', 'booking-subgraph', 'hotel-subgraph', 'promocode-subgraph']) {
    assert.ok(fx.events.some((e) => e.svc === svc && e.requestId === 'client-id-1'), svc);
  }
  assert.ok(fx.grpcCalls.some((c) => c.requestId === 'client-id-1'));
  assert.ok(fx.restCalls.some((c) => c.requestId === 'client-id-1' && c.path.startsWith('/api/hotels/')));
  assert.ok(fx.restCalls.some((c) => c.requestId === 'client-id-1' && c.path.startsWith('/api/promos/')));

  const replaced = await post(fx.gatewayUrl, MY_BOOKINGS, { requestId: 'id with spaces' });
  assert.match(replaced.requestId, /^[0-9a-f-]{36}$/);
  assert.ok(fx.grpcCalls.some((c) => c.requestId === replaced.requestId));
});

test('DataLoader: пачки до 10 отелей, без повторов, не больше 4 одновременных запросов к монолиту', async () => {
  let active = 0;
  let peak = 0;
  const fetched = [];
  const batches = [];
  const catalog = createHotelCatalog({
    fetchHotel: async (id) => {
      fetched.push(id);
      peak = Math.max(peak, ++active);
      await new Promise((resolve) => setTimeout(resolve, 5));
      active--;
      return { id };
    },
  });
  const loader = catalog.loader((msg, data) => { if (msg === 'batch') batches.push(data.requested.length); });
  const ids = Array.from({ length: 25 }, (_, i) => `h${i}`);
  assert.equal((await loader.loadMany([...ids, ids[0]])).length, 26);
  assert.equal(fetched.length, 25);
  assert.deepEqual(batches, [10, 10, 5]);
  assert.ok(peak <= 4);
});

test('Кеш отелей: вытеснение старых записей не портит результат текущей пачки', async () => {
  const catalog = createHotelCatalog({ fetchHotel: async (id) => ({ id }) });
  const quiet = () => {};
  await catalog.loader(quiet).loadMany(Array.from({ length: 500 }, (_, i) => String(i)));
  assert.deepEqual(await catalog.loader(quiet).loadMany(['0', 'new']), [{ id: '0' }, { id: 'new' }]);
});
