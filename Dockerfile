# Production Dockerfile for DNM Lab Template
# Multi-stage build: Frontend + Backend

# ============================================
# Stage 1: Build Frontend
# ============================================
FROM node:20-alpine AS frontend-build
WORKDIR /app

# Copy frontend package files
COPY packages/backoffice-web/package*.json ./
RUN npm ci

# Copy frontend source and build
COPY packages/backoffice-web/ ./
RUN npm run build

# ============================================
# Stage 2: Build Backend
# ============================================
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS backend-build
WORKDIR /src

# Copy csproj and restore (better caching)
COPY packages/dotnet-api/api.csproj ./
RUN dotnet restore api.csproj

# Copy source and publish
COPY packages/dotnet-api/ ./
RUN dotnet publish api.csproj -c Release -o /app/publish --no-restore

# ============================================
# Stage 3: Runtime
# ============================================
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# Install curl for health checks
RUN apt-get update && apt-get install -y curl && rm -rf /var/lib/apt/lists/*

# Copy published backend
COPY --from=backend-build /app/publish .

# Copy frontend build to wwwroot for static file serving
COPY --from=frontend-build /app/dist ./wwwroot

# Environment
ENV ASPNETCORE_ENVIRONMENT=Production
# PORT defaults to 3000 locally; managed hosts (Render, etc.) inject their own PORT and
# the shell-form entrypoint below binds Kestrel to it at container start.
ENV PORT=3000

EXPOSE 3000

# Health check honors the runtime PORT (falls back to 3000).
HEALTHCHECK --interval=30s --timeout=10s --start-period=30s --retries=3 \
    CMD curl -f http://localhost:${PORT:-3000}/health || exit 1

# Shell form so ${PORT} is expanded at runtime — lets the host pick the listen port.
ENTRYPOINT ["sh", "-c", "ASPNETCORE_URLS=http://+:${PORT:-3000} dotnet api.dll"]
