# Repère — Code Documentation

> Technical documentation for the Repère codebase. Covers architecture, backend, frontend, API, and component library.
> For the project wiki (requirements, design decisions, plan), see [`wiki/`](../wiki/README.md).

---

## Sections

| Section | What's covered |
|---------|---------------|
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

## Relationship to the Wiki

| Wiki (`wiki/`) | Docs (`docs/`) |
|----------------|----------------|
| Requirements | Implementation |
| Design decisions | How it actually works |
| What to build and why | How to work with what was built |
| For planning | For coding |
