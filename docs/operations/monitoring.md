# Monitoring and alerting

## Health checks

| Probe | Use it for | Healthy means |
|---|---|---|
| `GET /healthz/live` | Container liveness (the compose healthcheck uses it) | The process answers HTTP. Never restart the façade because a *backend* is down. |
| `GET /healthz` | Readiness / external uptime check | Stalwart's JMAP session, Nextcloud's DAV root and Keycloak's realm metadata all answer. Returns 503 `Unhealthy` otherwise. The result is cached for 10 s (one backend round per 10 s however often it is hit), so a recovery or an outage shows up to 10 s late; poll no faster than that. |
| `https://auth.<domain>/realms/nightware/.well-known/openid-configuration` | Keycloak availability as NightMail sees it | 200 through Caddy. Alert on the first failure (see README: token refresh). |
| Keycloak `:9000/health/ready`, Nextcloud `status.php`, Stalwart `/healthz/live` | Per-backend, inside the network | As in `deploy/docker-compose.yml`. |

Anonymous, never rate-limited, never traced.

## Telemetry export

Set `Telemetry:Otlp:Endpoint` (compose: `NIGHTWARE_OTLP_ENDPOINT`) to an OpenTelemetry Collector;
traces, metrics and logs are then exported over OTLP (`Telemetry:Otlp:Protocol`: `Grpc` or
`HttpProtobuf`). From the collector, send metrics to Prometheus/Mimir, traces to Tempo/Jaeger and
logs to Loki — any OTLP-capable backend works. Off by default; stdout JSON logs are always on.

Every span and every log record carries `graph.request_id` (the `request-id` response header, also
in every error's `innerError`) and `graph.client_request_id` (echoed from the client). When a user
reports an error, NightMail shows the message; ask for the time and search logs/traces by request id.

## Metrics

| Metric (meter) | Tags worth using | What it tells you |
|---|---|---|
| `http.server.request.duration` (`Microsoft.AspNetCore.Hosting`) | `http.route`, `http.request.method`, `http.response.status_code` | Request rate, latency and status by route template. |
| `http.client.request.duration` (`System.Net.Http`) | `nightware.backend.client` (e.g. `Nightware.Jmap`, `IDavClient`, `Nightware.Keycloak.Token`), `server.address`, `http.response.status_code`, `error.type` | Backend latency and failures per adapter client. |
| `aspnetcore.rate_limiting.requests` (`Microsoft.AspNetCore.RateLimiting`) | `aspnetcore.rate_limiting.result` | Rejections by the per-user limiter. |
| `nightware.request.timeouts` (`Nightware.Api`) | `http.route`, `http.request.method` | Requests that overran the façade's budget (answered 503/504). |
| `resilience.polly.strategy.events` (`Polly`) | `pipeline.name`, `event.name` (`OnRetry`, `OnTimeout`) | Adapter retries and per-attempt timeouts. |
| `kestrel.active_connections`, `System.Runtime` GC/thread-pool | | Saturation. |

## Alerts

| Alert | Condition (suggested) | First look |
|---|---|---|
| **Token endpoint down** | Keycloak OIDC metadata or token endpoint failing for 1 min | Page immediately: every refresh in that window signs users out. [runbook](runbook.md#keycloak-down-or-restarting) |
| Façade not ready | `/healthz` unhealthy 3 min | Which check: the readiness body names it. |
| 5xx ratio | `status >= 500` > 2 % of requests over 10 min | Split by status: 503 = backend/budget, 500 = bug (logs by request id). |
| 401 spike | 401 rate > 5× its 1-day baseline | Issuer/clock/audience problem: [runbook](runbook.md#everyone-gets-401). |
| Backend failures | `http.client.request.duration{error.type}` > 5 % per `nightware.backend.client`, 5 min | That backend. |
| Slow backend | p95 `http.client.request.duration` > 2 s per client, 10 min | [runbook](runbook.md#nextcloud-is-slow). |
| Budget overruns | `nightware.request.timeouts` > 0 sustained 5 min | Slow backend or oversized requests. |
| Throttling | rate-limit rejections > 1 % of requests, 15 min | A runaway client or limits too tight for a heavy user. |
| Latency | p95 `http.server.request.duration` of delta/list routes > 1 s, 15 min | Load target is p95 < 300 ms. |
| Certificate expiry | < 14 days on any public name | Caddy renewals (ACME reachability). |
| Disk | Docker data / volume disk > 85 % | Postgres will not start on a full disk; prune *build cache* (`docker builder prune`), never volumes. |
