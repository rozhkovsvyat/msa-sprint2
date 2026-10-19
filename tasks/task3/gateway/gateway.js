import { ApolloGateway, IntrospectAndCompose, RemoteGraphQLDataSource } from '@apollo/gateway';
import { maxAliasesRule } from '@escape.tech/graphql-armor-max-aliases';
import { maxDepthRule } from '@escape.tech/graphql-armor-max-depth';
import { logger, requestContext } from '../shared/request-context.js';

export const log = logger('apollo-gateway');

// Apollo Gateway не передаёт заголовки клиента в подграфы сам: без этого ACL по userid не сработает.
export class ForwardingDataSource extends RemoteGraphQLDataSource {
  willSendRequest({ request, context }) {
    if (context.userId) request.http.headers.set('userid', context.userId);
    request.http.headers.set('x-request-id', context.requestId);
  }
}

// Ограничения клиентской операции; служебные запросы шлюза к подграфам через них не проходят.
// Нарушение — обычная ошибка валидации запроса (HTTP 400), как у встроенных правил GraphQL.
const reject = { propagateOnRejection: false, onReject: [(context, error) => context.reportError(error)] };
export const validationRules = [maxDepthRule({ n: 8, ...reject }), maxAliasesRule({ n: 20, ...reject })];

export const createGateway = (subgraphs, DataSource = ForwardingDataSource) => new ApolloGateway({
  supergraphSdl: new IntrospectAndCompose({ subgraphs }),
  buildService: ({ url }) => new DataSource({ url }),
});

export const contextFrom = (req, res, reqLogger = log) => requestContext(req, res, reqLogger);
