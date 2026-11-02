import DataLoader from 'dataloader';
import { GraphQLError } from 'graphql';
import { LRUCache } from 'lru-cache';
import pLimit from 'p-limit';
import { log } from './schema.js';

export const MAX_BATCH = 10; // отелей в одной пачке DataLoader

// Справочник отелей: общий короткий кеш между запросами (до 500 отелей) и DataLoader на каждый запрос.
// Обращений к монолиту одновременно не больше maxConcurrent, чтобы длинный список его не нагружал.
export function createHotelCatalog({ fetchHotel, ttlMs = 30_000, maxConcurrent = 4, maxEntries = 500 }) {
  const cache = ttlMs > 0 ? new LRUCache({ max: maxEntries, ttl: ttlMs }) : null;
  const limit = pLimit(maxConcurrent);

  // Функция загрузки DataLoader: один вызов на пачку отелей одного GraphQL-запроса.
  async function loadHotels(ids, reqLog, requestId) {
    // Попадания в кеш берём до запросов к монолиту: новые ключи могут вытеснить их из кеша.
    const results = new Map();
    for (const id of ids) {
      const hit = cache?.get(id);
      if (hit) results.set(id, hit.value);
    }
    const missing = ids.filter((id) => !results.has(id));
    reqLog('batch', { requested: ids, fromCache: ids.length - missing.length, fetchFromMonolith: missing });
    const fetched = await Promise.all(missing.map((id) => limit(async () => {
      try { return await fetchHotel(id, requestId); }
      catch (error) {
        reqLog('hotel lookup failed', { id, error: error.message });
        return new GraphQLError('Источник отеля временно недоступен', { extensions: { code: 'HOTEL_UNAVAILABLE' } });
      }
    })));
    missing.forEach((id, i) => {
      results.set(id, fetched[i]);
      // Ошибки не кешируем: следующий запрос может восстановиться. Отель может быть null (нет в монолите).
      if (!(fetched[i] instanceof Error)) cache?.set(id, { value: fetched[i] });
    });
    return ids.map((id) => results.get(id) ?? null);
  }

  // Новый DataLoader на каждый запрос: дедупликация в пределах запроса, без смешивания данных разных запросов.
  return { loader: (reqLog = log, requestId) => new DataLoader((ids) => loadHotels(ids, reqLog, requestId), { maxBatchSize: MAX_BATCH }) };
}
