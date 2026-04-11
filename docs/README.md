# Repère Documentation

> Documentation for both current users and contributors.
> For the project wiki (requirements, design decisions, plan), see [`wiki/`](../wiki/README.md).

---

## Sections

| Section | What's covered |
|---------|---------------|
| [User Guides](user/README.md) | Setup and practical MVP 1 usage without needing deep implementation knowledge |
| [Architecture](architecture/README.md) | System topology, service boundaries, data flow diagrams |
| [Backend](backend/README.md) | ASP.NET Core structure, DB access, formula engine, background workers |
| [Frontend](frontend/README.md) | Vite/React structure, routing, state management, build pipeline |
| [API](api/README.md) | REST endpoint reference, authentication, request/response schemas |
| [Components](components/README.md) | Shared UI components — props, usage examples, styling conventions |

---

## How These Docs Are Written

See [ai-guidelines.md](ai-guidelines.md) for:
- What to document and what to skip
- File structure conventions
- Code example format
- How to keep docs in sync with code

---

## User Docs vs Technical Docs

| Audience | Best place to start |
|----------|---------------------|
| Someone trying the current product | [User Guides](user/README.md) |
| Someone integrating against the API in detail | [API](api/README.md) |
| Someone working on the codebase | [Backend](backend/README.md), [Architecture](architecture/README.md) |

## Relationship to the Wiki

| Wiki (`wiki/`) | Docs (`docs/`) |
|----------------|----------------|
| Requirements | Implementation |
| Design decisions | How it actually works |
| What to build and why | How to work with what was built |
| For planning | For coding |
