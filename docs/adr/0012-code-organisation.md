# ADR-0012: Code organisation: one project per service, vertical slices, no mediator or mapper

- **Status:** Accepted
- **Date:** 2026-11-06

## Context

Microservice repositories often grow a Clean-Architecture template per service: Domain, Application, Infrastructure, Api, and their test projects. With four services that is twenty or more projects, most of which hold a handful of files. The brief rules out MediatR and AutoMapper.

## Decision

- **One deployable project per service** (`TicketHub.Catalog`, `TicketHub.Booking`, and so on), organised by feature:
  - `Features/<Feature>/` holds the endpoint, handler and request/response records together.
  - `Domain/` holds entities and rules, with no infrastructure types. In Booking, a reflection test (`ArchitectureTests`) enforces this.
  - `Data/` holds the `DbContext`, migrations and seed data.
- **Handlers are plain classes** with a `HandleAsync` method, registered in DI and injected into minimal-API endpoints. There is no mediator: a handler is found with "go to definition", and its dependencies are visible in its constructor.
- **Mapping is explicit:** small `From(...)` / `ToMessage(...)` methods next to the types they map. A renamed property is a compile error, not a silent runtime gap.
- **Expected failures are values.** Domain methods return `Result` / `Result<T>` with a typed `Error` (Validation, NotFound, Conflict, Gone, Unprocessable or Unavailable), and the edge maps it to RFC 9457 problem details. Exceptions are for the unexpected.
- **Shared code is technical only:**
  - `TicketHub.BuildingBlocks`: `Result`, `Money`, the messaging layer, the migration helper.
  - `TicketHub.Contracts`: message records.
  - No domain types are shared between services.
- **One test project per service,** with `Domain/` (pure unit tests) and `Integration/` (Testcontainers) folders.

## Consequences

- There are fewer projects and less indirection; the whole of a feature fits on one screen.
- Dependency direction inside a service is enforced by convention plus one test, not by project references. That is a deliberate trade for less ceremony.
- `TicketHub.Contracts` is a compile-time coupling point between services. Within one repository that is a feature (a breaking change fails the build). Across repositories it would become a versioned package, or contracts copied per consumer.

## Alternatives considered

- **Domain/Application/Infrastructure projects per service:** stronger compile-time boundaries, at roughly four times the project count. Worth it in a larger codebase with more contributors.
- **A mediator pipeline for cross-cutting behaviour** (validation, logging, transactions): those concerns are handled by endpoint filters, the messaging layer and EF Core's unit of work instead.
