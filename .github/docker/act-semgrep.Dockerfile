# Local act runner image: Python 3.12 + Node.js for JavaScript-based Actions.
FROM node:24-bookworm-slim AS node
FROM python:3.12-bookworm
COPY --from=node /usr/local/bin/node /usr/local/bin/node
RUN node --version && python --version
