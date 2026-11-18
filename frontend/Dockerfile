# Base images are pinned by digest (Dependabot proposes updates); the tag says which release the digest is.
FROM node:24-bookworm-slim@sha256:dc26ae92c9fb15cf6b1d63e20f0944b2efe0f115b20e00be48fb23fceb061c54 AS build
WORKDIR /src
COPY package.json pnpm-lock.yaml pnpm-workspace.yaml ./
COPY frontend/package.json frontend/package.json
RUN corepack enable && corepack install && pnpm install --frozen-lockfile
COPY frontend/ frontend/
RUN pnpm --filter vessel-frontend build

FROM nginxinc/nginx-unprivileged:alpine@sha256:b5047b9b76845eac124358373c6852377c52e4b05834a47647e955dd91cb8f1e
ARG VERSION=0.4.2
ARG REVISION=unknown
LABEL org.opencontainers.image.source="https://github.com/sloppitty/vessel" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${REVISION}"
ENV VESSEL_TRUSTED_PROXY="" \
    VESSEL_API_UPSTREAM=http://api:8080 \
    NGINX_ENVSUBST_FILTER=VESSEL_API_UPSTREAM
COPY --chmod=755 frontend/19-trusted-proxy.sh /docker-entrypoint.d/19-trusted-proxy.sh
COPY frontend/static-security.conf /etc/nginx/static-security.conf
COPY frontend/nginx.conf.template /etc/nginx/templates/default.conf.template
COPY --from=build /src/frontend/dist/ /usr/share/nginx/html/
EXPOSE 8080
