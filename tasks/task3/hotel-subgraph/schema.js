import { buildSubgraphSchema } from '@apollo/subgraph';
import DataLoader from 'dataloader';
import gql from 'graphql-tag';
import { GraphQLError } from 'graphql';
import { concurrencyLimit, logger, requestContext } from '../shared/request-context.js';

export const log = logger('hotel-subgraph');
export const MAX_BATCH = 10; // отелей в одной пачке DataLoader
export const MAX_IDS = 50;   // id в одном запросе hotelsByIds

// Источник данных — REST монолита. Метода «отели по списку id» у монолита нет, а дорабатывать его
// нельзя (старые модули не дорабатываются), поэтому батч разворачивается в параллельные запросы по id.
export function createMonolithClient({ baseUrl, fetch = globalThis.fetch, timeoutMs = 3000 }) {
  return async function fetchHotel(id, requestId) {
    const res = await fetch(`${baseUrl}/api/hotels/${encodeURIComponent(id)}`, {
      headers: { 'x-request-id': requestId }, signal: AbortSignal.timeout(timeoutMs),
    });
    if (res.status === 404) return null;
    if (!res.ok) throw new Error(`monolith /api/hotels/${id}: HTTP ${res.status}`);
    const h = await res.json();
    return {
      id: h.id,
      name: null, // у отеля в монолите нет названия (ADR-001, Н3): не выдумываем, поле появится с hotel-service
      city: h.city,
      rating: h.rating,
      description: h.description,
      operational: h.operational,
      stars: null, // звёздности в источнике нет; rating — оценка по отзывам, не класс отеля
    };
  };
}

// Справочник отелей: общий короткий кеш между запросами и DataLoader на каждый запрос.
export function createHotelCatalog({ fetchHotel, ttlMs = 30_000, now = Date.now, maxConcurrent = 4 }) {
  const cache = new Map(); // id -> { value, expires }
  const run = concurrencyLimit(maxConcurrent);

  // Батч-функция DataLoader: один вызов на пачку отелей одного GraphQL-запроса.
  async function loadHotels(ids, reqLog, requestId) {
    const t = now();
    // Попадания в кеш берём до запросов к монолиту: новые ключи могут вытеснить их из общего кеша.
    const results = new Map(ids.filter((id) => cache.get(id)?.expires > t).map((id) => [id, cache.get(id).value]));
    const missing = ids.filter((id) => !results.has(id));
    reqLog('batch', { requested: ids, fromCache: ids.length - missing.length, fetchFromMonolith: missing });
    const fetched = await Promise.all(missing.map((id) => run(async () => {
      try { return await fetchHotel(id, requestId); }
      catch (error) {
        reqLog('hotel lookup failed', { id, error: error.message });
        return new GraphQLError('Источник отеля временно недоступен', { extensions: { code: 'HOTEL_UNAVAILABLE' } });
      }
    })));
    missing.forEach((id, i) => {
      results.set(id, fetched[i]);
      // Ошибки не кешируем между запросами: следующий запрос может восстановиться.
      if (!(fetched[i] instanceof Error)) {
        if (cache.size >= 500) cache.delete(cache.keys().next().value);
        cache.set(id, { value: fetched[i], expires: now() + ttlMs });
      }
    });
    return ids.map((id) => results.get(id) ?? null);
  }

  // Новый DataLoader на каждый запрос: дедупликация в пределах запроса, без смешивания данных разных запросов.
  return { loader: (reqLog = log, requestId) => new DataLoader((ids) => loadHotels(ids, reqLog, requestId), { maxBatchSize: MAX_BATCH }) };
}

export const typeDefs = gql`
  extend schema @link(url: "https://specs.apollo.dev/federation/v2.7", import: ["@key"])

  type Hotel @key(fields: "id") {
    id: ID!
    "Нет в источнике (монолит не хранит название) — всегда null до появления hotel-service."
    name: String
    city: String
    "Средняя оценка по отзывам."
    rating: Float
    description: String
    operational: Boolean
    "Нет в источнике — всегда null."
    stars: Int
  }

  type Query {
    hotelsByIds(ids: [ID!]!): [Hotel]!
  }
`;

export const resolvers = {
  Hotel: {
    // Шлюз присылает представления отелей пачкой (_entities); DataLoader склеивает их в один батч.
    __resolveReference: async ({ id }, { hotelLoader }) => {
      const hotel = await hotelLoader.load(id);
      if (!hotel) throw new GraphQLError('Отель не найден', { extensions: { code: 'HOTEL_NOT_FOUND' } });
      return hotel;
    },
  },
  Query: {
    hotelsByIds: (_, { ids }, { hotelLoader }) => {
      if (ids.length > MAX_IDS) {
        throw new GraphQLError(`hotelsByIds: не больше ${MAX_IDS} id за запрос`, { extensions: { code: 'BAD_USER_INPUT' } });
      }
      return hotelLoader.loadMany(ids);
    },
  },
};

export const schema = buildSubgraphSchema([{ typeDefs, resolvers }]);

export function contextFrom(req, catalog, res, reqLogger = log) {
  const context = requestContext(req, res, reqLogger);
  return { ...context, hotelLoader: catalog.loader(context.log, context.requestId) };
}
