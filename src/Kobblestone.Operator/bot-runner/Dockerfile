FROM node:22-bookworm-slim AS deps
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends \
      python3 build-essential pkg-config \
      libcairo2-dev libpango1.0-dev libjpeg-dev libgif-dev librsvg2-dev \
 && rm -rf /var/lib/apt/lists/*
COPY package.json package-lock.json ./
RUN npm ci --omit=dev \
 && node -e "require('canvas')" \
 && npm cache clean --force

FROM node:22-bookworm-slim
RUN apt-get update && apt-get install -y --no-install-recommends \
      tini libcairo2 libpango-1.0-0 libpangocairo-1.0-0 \
      libjpeg62-turbo libgif7 librsvg2-2 \
 && rm -rf /var/lib/apt/lists/*
ENV NODE_ENV=production
WORKDIR /app
RUN groupadd -r bot && useradd -r -g bot -m -d /home/bot bot

COPY --from=deps /app/node_modules ./node_modules
COPY index.js ./

USER bot
EXPOSE 8080 3000

ENTRYPOINT ["/usr/bin/tini", "--"]
CMD ["node", "index.js"]