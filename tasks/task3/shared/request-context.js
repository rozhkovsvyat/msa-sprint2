import { randomUUID } from 'node:crypto';

export const logger = (svc) => (msg, extra = {}) =>
  console.log(JSON.stringify({ ts: new Date().toISOString(), svc, msg, ...extra }));

// userid — учебная модель личности, не JWT. Пробелы и управляющие символы не допускаются.
export const isUserId = (value) => typeof value === 'string' && value.length > 0 && value.length <= 128
  && !/[\s\p{Cc}]/u.test(value);

// Те же правила, что у booking-service (RequestId.cs): 1–128 символов, латинские буквы, цифры и -_.:
export const isRequestId = (value) => typeof value === 'string' && /^[A-Za-z0-9._:-]{1,128}$/.test(value);

export function requestContext(req, res, log) {
  const header = req.headers['x-request-id'];
  const requestId = isRequestId(header) ? header : randomUUID();
  res?.setHeader('x-request-id', requestId);
  const reqLog = (msg, extra = {}) => log(msg, { ...extra, requestId });
  const userId = isUserId(req.headers.userid) ? req.headers.userid : null;
  reqLog('request', { userid: userId, operation: req.body?.operationName ?? null });
  return { userId, requestId, log: reqLog };
}

// Предел одновременных обращений к монолиту, общий для всех пачек DataLoader.
export function concurrencyLimit(max) {
  let active = 0;
  const waiting = [];
  return async (fn) => {
    if (active >= max) await new Promise((resolve) => waiting.push(resolve));
    else active++;
    try { return await fn(); }
    finally {
      const next = waiting.shift();
      if (next) next();
      else active--;
    }
  };
}
