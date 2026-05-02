# DocumentForge Admin UI — Next.js 15 standalone image.
#
# Build:  docker build -t ghcr.io/aerotoysio/documentforge-admin:latest \
#           --build-arg NEXT_PUBLIC_DFDB_URL=http://localhost:5000 \
#           ./admin-ui
#
# NEXT_PUBLIC_* envs are baked into the JS bundle at *build* time (Next.js
# inlines them into the client bundle). Override per-environment by passing
# a different --build-arg, or rebuild for each target.

# ---- 1. install deps ----
FROM node:22-alpine AS deps
WORKDIR /app
COPY package.json package-lock.json* ./
RUN npm ci

# ---- 2. build ----
FROM node:22-alpine AS build
WORKDIR /app
ARG NEXT_PUBLIC_DFDB_URL=http://localhost:5000
ENV NEXT_PUBLIC_DFDB_URL=${NEXT_PUBLIC_DFDB_URL}
ENV NEXT_TELEMETRY_DISABLED=1
COPY --from=deps /app/node_modules ./node_modules
COPY . .
RUN npm run build

# ---- 3. runtime ----
FROM node:22-alpine AS runtime
WORKDIR /app

ENV NODE_ENV=production
ENV NEXT_TELEMETRY_DISABLED=1
ENV PORT=3000
ENV HOSTNAME=0.0.0.0

# Non-root user
RUN addgroup -S nodejs && adduser -S -G nodejs nextjs

# Standalone output bundles a tiny server.js + minimum node_modules
COPY --from=build --chown=nextjs:nodejs /app/.next/standalone ./
COPY --from=build --chown=nextjs:nodejs /app/.next/static ./.next/static

USER nextjs
EXPOSE 3000

# Lightweight healthcheck (Next responds with 200 at /, even before any users)
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
  CMD wget -q --spider http://127.0.0.1:3000/ || exit 1

CMD ["node", "server.js"]
