# Nightware build, test and CI entry points. `make help` lists the targets.
# The GitHub Actions workflow (.github/workflows/ci.yml) calls these same targets,
# so a green `make ci` locally means a green CI build. See docs/operations/ci.md.

SHELL := /usr/bin/env bash
.SHELLFLAGS := -euo pipefail -c
.DEFAULT_GOAL := help

CONFIGURATION ?= Release
SOLUTION := Nightware.slnx
RESULTS := $(CURDIR)/artifacts/test-results
COVERAGE_SETTINGS := $(CURDIR)/scripts/ci/coverage.settings.xml
# Gate; measured 2026-11-15 at 96.9% line / 91.0% branch; see docs/operations/ci.md for the reasoning.
COVERAGE_LINE ?= 90
COVERAGE_BRANCH ?= 80
IMAGE ?= nightware/api:dev
COMPOSE := docker compose -p nightware
K6_IMAGE ?= grafana/k6:1.4.2
LOAD_SCRIPT ?= poll-cycle.js

.PHONY: help
help: ## List the targets
	@grep -E '^[a-z-]+:.*## ' $(MAKEFILE_LIST) | awk 'BEGIN {FS = ":.*## "}; {printf "  %-18s %s\n", $$1, $$2}'

.PHONY: restore
restore: ## Restore NuGet packages (NuGet audit runs here)
	dotnet restore $(SOLUTION)

.PHONY: build
build: restore ## Build everything; warnings are errors (Directory.Build.props)
	dotnet build $(SOLUTION) --no-restore -c $(CONFIGURATION)

.PHONY: test
test: build ## Every test project, no coverage (integration tests skip themselves unless NIGHTWARE_INTEGRATION=1)
	dotnet test --solution $(SOLUTION) --no-build -c $(CONFIGURATION)

.PHONY: test-architecture
test-architecture: build ## Layer rules (ArchUnitNET); kept out of the coverage run, whose instrumentation rewrites the DLLs they inspect
	dotnet test --project tests/Nightware.Architecture.Tests --no-build -c $(CONFIGURATION)

.PHONY: coverage
coverage: build ## Unit, endpoint, adapter and conformance tests with coverage, then the gate
	rm -rf $(RESULTS)
	dotnet test --solution scripts/ci/coverage.slnf --no-build -c $(CONFIGURATION) \
		--coverage --coverage-output-format cobertura --coverage-settings $(COVERAGE_SETTINGS) \
		--results-directory $(RESULTS)
	python3 scripts/ci/coverage-gate.py $$(ls $(RESULTS)/merged/*.cobertura.xml) $(COVERAGE_LINE) $(COVERAGE_BRANCH)

.PHONY: vulnerable
vulnerable: restore ## Fail on any known-vulnerable package, direct or transitive
	scripts/ci/check-vulnerable.sh

.PHONY: ci
ci: build vulnerable test-architecture coverage ## What CI runs on every push: build, vulnerability check, all tests, coverage gate

.PHONY: image
image: ## Build the API container image
	docker build -f src/Nightware.Api/Dockerfile -t $(IMAGE) .

# ---------------------------------------------------------------- live stack
# These never delete volumes and never reconfigure a running backend; bootstrap.sh is idempotent.

.PHONY: env
env: ## Create deploy/.env if missing and generate its NIGHTWARE_DELTA_LINK_KEY if absent (never replaces one)
	deploy/scripts/init-env.sh

.PHONY: stack
stack: env ## Start the deploy/ backends (Keycloak, Stalwart, Nextcloud, Caddy) and wait until healthy
	cd deploy && $(COMPOSE) up -d --wait

.PHONY: api
api: env ## (Re)build and start only the façade container against the running stack
	cd deploy && $(COMPOSE) --profile api up -d --build --wait --no-deps nightware-api

.PHONY: integration
integration: stack ## Bootstrap the stack, start the façade, run smoke.sh and the live integration suite
	cd deploy && ./bootstrap.sh
	$(MAKE) api
	cd deploy && ./smoke.sh
	NIGHTWARE_INTEGRATION=1 dotnet test --project tests/Nightware.Integration.Tests -c $(CONFIGURATION)

.PHONY: load
load: ## k6 poll-cycle load test against the running stack (USERS=, DURATION=, see tests/Nightware.Load/README.md)
	tests/Nightware.Load/run.sh $(LOAD_SCRIPT)

.PHONY: clean
clean: ## Remove build output and test results
	dotnet clean $(SOLUTION) -c $(CONFIGURATION) >/dev/null
	rm -rf artifacts
