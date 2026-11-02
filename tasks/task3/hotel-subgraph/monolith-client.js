// Источник данных — REST монолита. Метода «отели по списку id» у монолита нет, а монолит мы не
// дорабатываем, поэтому пачка разворачивается в параллельные запросы по id.
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
      name: null, // в монолите у отеля нет названия, появится вместе с сервисом отелей
      city: h.city,
      rating: h.rating,
      description: h.description,
      operational: h.operational,
      stars: null, // звёздности в источнике нет; rating — оценка по отзывам, не класс отеля
    };
  };
}
