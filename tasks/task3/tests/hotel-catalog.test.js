import test from 'node:test';
import assert from 'node:assert/strict';
import { createHotelCatalog } from '../hotel-subgraph/catalog.js';

const quiet = () => {};

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
  const fetched = [];
  const catalog = createHotelCatalog({ fetchHotel: async (id) => { fetched.push(id); return { id }; }, maxEntries: 2 });
  await catalog.loader(quiet).loadMany(['a', 'b']);
  // В кеше два места: новые c и d вытесняют из него a, но a взят из кеша ещё до запросов к монолиту.
  assert.deepEqual(await catalog.loader(quiet).loadMany(['a', 'c', 'd']), [{ id: 'a' }, { id: 'c' }, { id: 'd' }]);
  assert.deepEqual(fetched, ['a', 'b', 'c', 'd']);
});

test('Кеш отелей: в пределах TTL отель берётся из кеша, после — снова из монолита', async () => {
  const fetched = [];
  const catalog = createHotelCatalog({ fetchHotel: async (id) => { fetched.push(id); return { id }; }, ttlMs: 50 });
  await catalog.loader(quiet).load('h1');
  await catalog.loader(quiet).load('h1');
  assert.deepEqual(fetched, ['h1']);
  await new Promise((resolve) => setTimeout(resolve, 80));
  await catalog.loader(quiet).load('h1');
  assert.deepEqual(fetched, ['h1', 'h1']);
});

test('Кеш отелей: ошибку монолита не кешируем, следующий запрос снова идёт в монолит', async () => {
  let calls = 0;
  const catalog = createHotelCatalog({
    fetchHotel: async (id) => {
      if (++calls === 1) throw new Error('timeout');
      return { id };
    },
  });
  const [failed] = await catalog.loader(quiet).loadMany(['h1']);
  assert.equal(failed.extensions.code, 'HOTEL_UNAVAILABLE');
  assert.deepEqual(await catalog.loader(quiet).loadMany(['h1']), [{ id: 'h1' }]);
  assert.equal(calls, 2);
});
