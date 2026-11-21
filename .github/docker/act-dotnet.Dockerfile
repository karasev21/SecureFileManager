# Local act runner image: .NET 8 SDK + Node.js for JavaScript-based Actions.
FROM node:24-bookworm-slim AS node
FROM mcr.microsoft.com/dotnet/sdk:8.0
COPY --from=node /usr/local/bin/node /usr/local/bin/node
RUN node --version && dotnet --version
