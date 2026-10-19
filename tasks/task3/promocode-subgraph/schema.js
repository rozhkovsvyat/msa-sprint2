import { buildSubgraphSchema } from '@apollo/subgraph';
import DataLoader from 'dataloader';
import gql from 'graphql-tag';
import { concurrencyLimit, logger, requestContext } from '../shared/request-context.js';

export const log = logger('promocode-subgraph');

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
  async function isValidFor(code, userId, requestId) {
    const url = userId
      ? `${baseUrl}/api/promos/validate?code=${encodeURIComponent(code)}&userId=${encodeURIComponent(userId)}`
      : `${baseUrl}/api/promos/${encodeURIComponent(code)}/valid?isVipUser=false`;
    const res = await fetch(url, {
      method: userId ? 'POST' : 'GET', headers: { 'x-request-id': requestId }, signal: AbortSignal.timeout(timeoutMs),
    });
    if (userId) {
      if (res.status === 400) return false;
      if (!res.ok) throw new Error(`monolith promos/validate: HTTP ${res.status}`);
      return true;
    }
    if (!res.ok) throw new Error(`monolith promos/valid: HTTP ${res.status}`);
    return (await res.text()).trim() === 'true';
  }

  return { getPromo, isValidFor };
}

// DataLoader на каждый запрос: каждый промокод бронирований пользователя читается из монолита один раз.
export function createLoaders(api, requestId) {
  const run = concurrencyLimit(4);
  const perKey = (keys, fn) => Promise.all(keys.map((key) => run(() => fn(key)).catch((error) => error)));
  return {
    promos: new DataLoader((codes) => perKey(codes, (code) => api.getPromo(code, requestId)), { maxBatchSize: 10 }),
    validity: new DataLoader((keys) => perKey(keys, (key) => {
      const [code, userId] = JSON.parse(key);
      return api.isValidFor(code, userId, requestId);
    }), { maxBatchSize: 10 }),
  };
}

function toDiscountInfo(promo, isValid) {
  return {
    code: promo.code,
    isValid,
    discountPercent: promo.discount,
    vipOnly: promo.vipOnly,
    expired: promo.expired,
    validUntil: promo.validUntil ?? null,
    description: promo.description ?? null,
  };
}

export const typeDefs = gql`
  extend schema @link(url: "https://specs.apollo.dev/federation/v2.7", import: ["@key", "@external", "@requires", "@override"])

  type Booking @key(fields: "id") {
    id: ID!
    promoCode: String @external
    userId: ID! @external
    "Скидка промокода бронирования по справочнику промокодов; без промокода — 0."
    discountPercent: Float @override(from: "booking-subgraph") @requires(fields: "promoCode")
    "Сведения о промокоде бронирования; isValid — действует ли он сейчас для этого пользователя."
    discountInfo: DiscountInfo @requires(fields: "promoCode userId")
  }

  type DiscountInfo {
    code: String!
    isValid: Boolean!
    discountPercent: Float!
    vipOnly: Boolean!
    expired: Boolean!
    validUntil: String
    description: String
  }

  type Query {
    validatePromoCode(code: String!, userId: ID): DiscountInfo
  }
`;

export const resolvers = {
  Booking: {
    discountPercent: async ({ id, promoCode }, _, { promos, log: reqLog }) => {
      // Промокода нет в справочнике — скидка неизвестна (null), а не нулевая.
      const discountPercent = promoCode ? (await promos.load(promoCode))?.discount ?? null : 0;
      reqLog('discountPercent resolved', { bookingId: id, promoCode, discountPercent });
      return discountPercent;
    },
    discountInfo: async ({ promoCode, userId }, _, { promos, validity }) => {
      if (!promoCode) return null;
      const promo = await promos.load(promoCode);
      if (!promo) return null;
      return toDiscountInfo(promo, await validity.load(JSON.stringify([promoCode, userId])));
    },
  },
  Query: {
    validatePromoCode: async (_, { code, userId }, { api, promos, requestId }) => {
      const promo = await promos.load(code);
      if (!promo) return null;
      return toDiscountInfo(promo, await api.isValidFor(code, userId ?? null, requestId));
    },
  },
};

export const schema = buildSubgraphSchema([{ typeDefs, resolvers }]);

export function contextFrom(req, api, res, reqLogger = log) {
  const context = requestContext(req, res, reqLogger);
  return { ...context, api, ...createLoaders(api, context.requestId) };
}
