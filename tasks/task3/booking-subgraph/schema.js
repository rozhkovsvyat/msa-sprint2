import { buildSubgraphSchema } from '@apollo/subgraph';
import { GraphQLError } from 'graphql';
import gql from 'graphql-tag';
import { logger, requestContext, isUserId } from '../shared/request-context.js';

export const log = logger('booking-subgraph');

export const toBooking = (b) => ({
  id: b.id,
  userId: b.user_id,
  hotelId: b.hotel_id,
  promoCode: b.promo_code || null, // proto3 не передаёт null: пустая строка — промокода нет
  discountPercent: b.discount_percent,
  createdAt: b.created_at,
});

// ACL: пользователь видит только свои бронирования. Личность берём из заголовка userid,
// который шлюз пробрасывает явно; в To-Be здесь будет идентификатор из проверенного токена.
function requireUser(context) {
  if (!isUserId(context.userId)) {
    throw new GraphQLError('Не передан заголовок userid', { extensions: { code: 'UNAUTHENTICATED' } });
  }
  return context.userId;
}
function forbid(context, requester, owner) {
  context.log('ACL deny', { requester, owner });
  return new GraphQLError('Доступ запрещён: можно смотреть только свои бронирования', {
    extensions: { code: 'FORBIDDEN' },
  });
}

export const typeDefs = gql`
  extend schema @link(url: "https://specs.apollo.dev/federation/v2.7", import: ["@key"])

  type Booking @key(fields: "id") {
    id: ID!
    userId: ID!
    hotelId: ID!
    promoCode: String
    "Скидка, сохранённая при бронировании; по паритету с монолитом это сумма скидки (ADR-001, Н2). В API поле отдаёт promocode-subgraph (@override), здесь оно остаётся для отката."
    discountPercent: Float
    createdAt: String!
    "Необязательный блок экрана: при отказе источника — null и ошибка с path, остальные бронирования остаются."
    hotel: Hotel
  }

  "Ссылка на сущность отеля; поля отеля разрешает hotel-subgraph."
  type Hotel @key(fields: "id", resolvable: false) {
    id: ID!
  }

  type Query {
    "Бронирования пользователя (имя из проверочного запроса задания)."
    bookingsByUser(userId: ID!): [Booking!]!
    "То же под именем из описания задания."
    userBookings(userId: ID!): [Booking!]!
    booking(id: ID!): Booking
  }
`;

async function bookingsOf(userId, context) {
  const requester = requireUser(context);
  if (requester !== userId) throw forbid(context, requester, userId);
  const bookings = (await context.listBookings(userId)).map(toBooking);
  if (bookings.some((b) => b.userId !== requester)) throw forbid(context, requester, 'upstream owner mismatch');
  context.log('bookings returned', { userId, count: bookings.length, ids: bookings.map((b) => b.id) });
  return bookings;
}

export const resolvers = {
  Query: {
    bookingsByUser: (_, { userId }, context) => bookingsOf(userId, context),
    userBookings: (_, { userId }, context) => bookingsOf(userId, context),
    booking: async (_, { id }, context) => {
      const requester = requireUser(context);
      // В контракте нет GetBooking: ищем среди бронирований запросившего — чужое просто не найдётся.
      const found = (await bookingsOf(requester, context)).find((b) => b.id === id) ?? null;
      context.log('booking by id', { requester, id, found: Boolean(found) });
      return found;
    },
  },
  Booking: {
    __resolveReference: async ({ id }, context) => {
      const requester = requireUser(context);
      const found = (await bookingsOf(requester, context)).find((b) => b.id === id);
      if (!found) throw forbid(context, requester, `booking ${id}`);
      return found;
    },
    hotel: (booking) => ({ __typename: 'Hotel', id: booking.hotelId }),
  },
};

export const schema = buildSubgraphSchema([{ typeDefs, resolvers }]);

// Контекст запроса: личность из заголовка и не больше одного вызова booking-service на пользователя.
export function contextFrom(req, listBookings, res, reqLogger = log) {
  const context = requestContext(req, res, reqLogger);
  const pending = new Map();
  return { ...context, listBookings: (userId) => {
    if (!pending.has(userId)) {
      context.log('gRPC ListBookings', { userId });
      pending.set(userId, listBookings(userId, context.requestId));
    }
    return pending.get(userId);
  } };
}
