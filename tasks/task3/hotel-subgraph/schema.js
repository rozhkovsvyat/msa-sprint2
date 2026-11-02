import { buildSubgraphSchema } from '@apollo/subgraph';
import gql from 'graphql-tag';
import { GraphQLError } from 'graphql';
import { logger, requestContext } from '../shared/request-context.js';

export const log = logger('hotel-subgraph');
export const MAX_IDS = 50; // id в одном запросе hotelsByIds

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
    // Шлюз присылает представления отелей пачкой (_entities); DataLoader собирает их в одну пачку.
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

export function contextFrom({ req, res }, catalog, reqLogger = log) {
  const context = requestContext(req, res, reqLogger);
  return { ...context, hotelLoader: catalog.loader(context.log, context.requestId) };
}
