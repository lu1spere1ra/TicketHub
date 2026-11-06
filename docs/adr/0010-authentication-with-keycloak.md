# ADR-0010: Authentication with Keycloak and JWT validation at every hop

- **Status:** Accepted
- **Date:** 2026-11-06

## Context

Customers must be identified in order to hold and pay for seats, and only operators may see saga state and dead letters. The brief asks for Keycloak (OIDC), with tokens validated at the gateway **and** in the services.

## Decision

- **Keycloak realm `tickethub`**, imported at startup from `deploy/keycloak/tickethub-realm.json`. The same file is used by Aspire, docker-compose and the E2E tests.
  - **Realm roles:** `customer` and `admin`. The realm's default role includes `customer`, so every new sign-up is a customer.
  - **`tickethub-api`:** the audience that APIs accept. An audience mapper adds it to access tokens.
  - **`tickethub-cli`:** a public client with the password grant, **for automated tests and k6 only**. A browser front end would use the authorization-code flow with PKCE.
- **One shared registration,** `AddTicketHubAuthentication()` in ServiceDefaults:
  - JWT bearer validation of issuer, audience, signature (JWKS from the realm's metadata) and lifetime.
  - Claims are kept as issued (`MapInboundClaims = false`), so `sub` and `email` stay `sub` and `email`.
  - An `IClaimsTransformation` flattens Keycloak's nested `realm_access.roles` into role claims, so `RequireRole` works.
  - Two policies: `customer` and `admin`.
- **Gateway:** each YARP route has a policy. `/catalog/**` is anonymous (browsing is public), `/booking/**` requires `customer`, and `/admin/**` requires `admin`. Bad requests stop at the edge, and the token is forwarded untouched.
- **Services validate again.** Booking enforces the same policies on its endpoints, and Catalog requires an authenticated caller on gRPC. A request that reaches a service without going through the gateway (misrouted traffic, a compromised pod, a developer port-forward) gets no free pass.
- **Service to service:** Booking calls Catalog *on behalf of the customer*, so it forwards the customer's bearer token (`ForwardAccessTokenHandler`). Messages carry no tokens: they are produced and consumed by trusted services inside the broker's network, and the customer's identity travels as data (the customer ID and email on the reservation).
- **Ownership:** a customer can only read or check out their own reservations. Someone else's reservation returns `404`, not `403`, so reservation IDs cannot be probed.

## Consequences

- Every API hop costs one signature check, which is microseconds with cached JWKS.
- Keycloak is needed to *log in*, not to *validate*. Services cache the signing keys, so a Keycloak outage blocks new logins but not requests carrying valid tokens.
- **docker-compose needs care:** tokens are issued for `http://localhost:5260/realms/tickethub` (the URL k6 and browsers use), while containers reach Keycloak at `http://keycloak:8080`. Compose fixes the issuer with `KC_HOSTNAME`, and services read metadata from the internal URL (`Auth:MetadataAddress`) while validating the public issuer (`Auth:ValidIssuer`).
- Integration tests use locally signed tokens with Keycloak's shape (`TestJwt`). They exercise the real validation, roles and policies without starting Keycloak. The E2E tests use real Keycloak tokens.

## Alternatives considered

- **Validate only at the gateway and trust internal traffic:** simpler, but "the network is trusted" is the assumption zero-trust guidance tells you to drop.
- **Client-credentials tokens for Booking → Catalog:** the right choice for calls a service makes on its own behalf (a batch job, say). Here the call is made for a customer, so forwarding their token keeps the audit trail honest. Client credentials would be added if Catalog needed to tell the two apart.
- **Opaque tokens with introspection:** a Keycloak round trip on every request, which makes the identity provider a hard runtime dependency.
