# Security

Bento holds people's data and runs their code (functions), so security reports are welcome and taken seriously.

## Reporting a vulnerability

Please **do not open a public issue**. Report it privately through GitHub: **Security → Report a vulnerability** on this repository. Include what you found, how to reproduce it, and which version or commit you used.

You get an answer within a week. Once a fix is released, the report is published with credit to you unless you prefer otherwise.

## Supported versions

Fixes go into the latest release and the `dev` branch. Please update to the latest release before reporting.

## Scope

In scope: the backend API, authentication and sessions, OAuth/MCP, share links and public forms, the function sandbox, imports that fetch URLs, and the Docker setups in this repository.

Out of scope: installations you do not run yourself, and findings that need an admin account to harm the same installation (admins can change everything by design).
