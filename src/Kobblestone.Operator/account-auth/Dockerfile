FROM node:22-alpine AS deps
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci --omit=dev && npm cache clean --force

FROM node:22-alpine
RUN apk add --no-cache tini
ENV NODE_ENV=production
WORKDIR /app
RUN addgroup -S bot && adduser -S -G bot -h /home/bot bot

COPY --from=deps /app/node_modules ./node_modules
COPY index.js ./

USER bot

ENTRYPOINT ["/sbin/tini", "--"]
CMD ["node", "index.js"]
