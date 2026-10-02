# Run Harbor on this machine.
# Postgres stays on 127.0.0.1:5432. This file does not start Postgres.

.PHONY: dev seed

ROOT := $(abspath $(dir $(lastword $(MAKEFILE_LIST))))
DOTNET := $(HOME)/.dotnet/dotnet
WEB_ORIGIN := http://localhost:5190
API_URL := http://localhost:5088

.DEFAULT_GOAL := dev

dev:
	@if ! lsof -nP -iTCP:5432 -sTCP:LISTEN >/dev/null 2>&1; then \
		echo "Postgres is not running on 127.0.0.1:5432."; \
		exit 1; \
	fi
	@if lsof -nP -iTCP:5088 -sTCP:LISTEN >/dev/null 2>&1; then \
		echo "Port 5088 is in use. Stop that process, then run make again."; \
		exit 1; \
	fi
	@if lsof -nP -iTCP:5190 -sTCP:LISTEN >/dev/null 2>&1; then \
		echo "Port 5190 is in use. Stop that process, then run make again."; \
		exit 1; \
	fi
	@echo "API $(API_URL)"
	@echo "Web $(WEB_ORIGIN)"
	@bash -c 'set -m; \
		trap "kill 0" INT TERM EXIT; \
		cd "$(ROOT)" && ASPNETCORE_ENVIRONMENT=Development "$(DOTNET)" run --project backend/Harbor.Host --no-launch-profile --urls http://localhost:5088 & \
		cd "$(ROOT)/frontend" && pnpm dev & \
		wait'

seed:
	@cd "$(ROOT)" && ASPNETCORE_ENVIRONMENT=Development HARBOR_DEV_WEB_ORIGIN="$(WEB_ORIGIN)" \
		"$(DOTNET)" run --project backend/Harbor.Host --no-launch-profile --verbosity quiet -- --seed-dev
