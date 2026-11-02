// Источник данных — справочник промокодов в REST монолита.
export function createMonolithClient({ baseUrl, fetch = globalThis.fetch, timeoutMs = 3000 }) {
  async function getPromo(code, requestId) {
    const res = await fetch(`${baseUrl}/api/promos/${encodeURIComponent(code)}`, {
      headers: { 'x-request-id': requestId }, signal: AbortSignal.timeout(timeoutMs),
    });
    if (res.status === 404) return null;
    if (!res.ok) throw new Error(`monolith /api/promos/${code}: HTTP ${res.status}`);
    return res.json(); // { code, discount, vipOnly, expired, validUntil, description }
  }

  // Действует ли промокод для пользователя (с учётом VIP) — правило монолита.
  async function isValidForUser(code, userId, requestId) {
    const url = `${baseUrl}/api/promos/validate?code=${encodeURIComponent(code)}&userId=${encodeURIComponent(userId)}`;
    const res = await fetch(url, {
      method: 'POST', headers: { 'x-request-id': requestId }, signal: AbortSignal.timeout(timeoutMs),
    });
    if (res.status === 400) return false;
    if (!res.ok) throw new Error(`monolith promos/validate: HTTP ${res.status}`);
    return true;
  }

  // Без пользователя VIP-статус неизвестен: проверяем как обычного.
  async function isValidForGuest(code, requestId) {
    const res = await fetch(`${baseUrl}/api/promos/${encodeURIComponent(code)}/valid?isVipUser=false`, {
      method: 'GET', headers: { 'x-request-id': requestId }, signal: AbortSignal.timeout(timeoutMs),
    });
    if (!res.ok) throw new Error(`monolith promos/valid: HTTP ${res.status}`);
    return (await res.text()).trim() === 'true';
  }

  return { getPromo, isValidForUser, isValidForGuest };
}
