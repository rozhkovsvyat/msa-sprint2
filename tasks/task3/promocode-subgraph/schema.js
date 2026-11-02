import { buildSubgraphSchema } from '@apollo/subgraph';
import DataLoader from 'dataloader';
import { GraphQLError } from 'graphql';
import gql from 'graphql-tag';
import pLimit from 'p-limit';
import { logger, requestContext } from '../shared/request-context.js';

export const log = logger('promocode-subgraph');
const MAX_CONCURRENT = 4; // одновременных обращений к монолиту на запрос
const MAX_BATCH = 10; // ключей в одной пачке DataLoader

// Ошибку монолита клиенту не показываем: она остаётся в логе, в ответе — код PROMO_UNAVAILABLE.
function unavailable(reqLog, error) {
  reqLog('monolith promo call failed', { error: error.message });
  return new GraphQLError('Справочник промокодов временно недоступен', { extensions: { code: 'PROMO_UNAVAILABLE' } });
}

// Пользователь известен — проверяем по правилу монолита для него, иначе как для обычного.
const isValidFor = (api, code, userId, requestId) =>
  userId ? api.isValidForUser(code, userId, requestId) : api.isValidForGuest(code, requestId);

// DataLoader на каждый запрос: каждый промокод бронирований пользователя читается из монолита один раз.
export function createLoaders(api, requestId, reqLog = log) {
  const limit = pLimit(MAX_CONCURRENT);
  const perKey = (keys, fn) => Promise.all(keys.map((key) => limit(() => fn(key)).catch((error) => unavailable(reqLog, error))));
  return {
    promos: new DataLoader((codes) => perKey(codes, (code) => api.getPromo(code, requestId)), { maxBatchSize: MAX_BATCH }),
    // Ключ — объект { code, userId }: DataLoader кеширует по примитиву, поэтому сворачиваем его в строку.
    validity: new DataLoader((keys) => perKey(keys, ({ code, userId }) => isValidFor(api, code, userId, requestId)), {
      maxBatchSize: MAX_BATCH,
      cacheKeyFn: ({ code, userId }) => JSON.stringify([code, userId]),
    }),
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

async function discountOf(promoCode, promos) {
  if (!promoCode) return 0;
  // Промокода нет в справочнике — скидка неизвестна (null), а не нулевая.
  const promo = await promos.load(promoCode);
  return promo?.discount ?? null;
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
      const discountPercent = await discountOf(promoCode, promos);
      reqLog('discountPercent resolved', { bookingId: id, promoCode, discountPercent });
      return discountPercent;
    },
    discountInfo: async ({ promoCode, userId }, _, { promos, validity }) => {
      if (!promoCode) return null;
      const promo = await promos.load(promoCode);
      if (!promo) return null;
      return toDiscountInfo(promo, await validity.load({ code: promoCode, userId }));
    },
  },
  Query: {
    validatePromoCode: async (_, { code, userId }, { api, promos, requestId, userId: requester, log: reqLog }) => {
      // Ответ с userId раскрывает VIP-статус, поэтому проверить промокод можно только для себя.
      if (userId && userId !== requester) {
        throw new GraphQLError('Доступ запрещён: проверять промокод можно только для себя', { extensions: { code: 'FORBIDDEN' } });
      }
      const promo = await promos.load(code);
      if (!promo) return null;
      const isValid = await isValidFor(api, code, userId, requestId).catch((error) => { throw unavailable(reqLog, error); });
      return toDiscountInfo(promo, isValid);
    },
  },
};

export const schema = buildSubgraphSchema([{ typeDefs, resolvers }]);

export function contextFrom({ req, res }, api, reqLogger = log) {
  const context = requestContext(req, res, reqLogger);
  return { ...context, api, ...createLoaders(api, context.requestId, context.log) };
}
