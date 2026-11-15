// Proves the load harness end to end on routes that need no feature phase: liveness, readiness,
// discovery (pure façade overhead) and one mapped Graph route (/me) with a real token.
//   tests/Nightware.Load/run.sh harness.js
import http from 'k6/http';
import { check, sleep } from 'k6';

const ORIGIN = __ENV.GRAPH_ORIGIN || 'https://graph.nightware.localhost';
const TOKEN_URL = __ENV.TOKEN_URL || 'https://auth.nightware.localhost/realms/nightware/protocol/openid-connect/token';

export const options = {
  vus: parseInt(__ENV.VUS || '5', 10),
  duration: __ENV.DURATION || '30s',
  thresholds: {
    'http_req_duration{call:live}': ['p(95)<50'],
    'http_req_duration{call:discovery}': ['p(95)<50'],
    'http_req_duration{call:me}': ['p(95)<300'],
    'http_req_duration{call:ready}': ['p(95)<300'],
    http_req_failed: ['rate<0.01'],
    checks: ['rate>0.99'],
  },
};

export function setup() {
  const res = http.post(TOKEN_URL, {
    grant_type: 'password', client_id: 'nightware-dev', client_secret: __ENV.NIGHTWARE_DEV_CLIENT_SECRET,
    username: 'alice', password: __ENV.TEST_USER_PASSWORD, scope: 'openid',
  });
  if (res.status !== 200) throw new Error(`token: HTTP ${res.status}`);
  return { token: res.json().access_token };
}

export default function (data) {
  check(http.get(`${ORIGIN}/healthz/live`, { tags: { call: 'live' } }), { 'live 200': (r) => r.status === 200 });
  check(http.get(`${ORIGIN}/healthz`, { tags: { call: 'ready' } }), { 'ready 200': (r) => r.status === 200 });
  check(http.get(`${ORIGIN}/.well-known/nightware`, { tags: { call: 'discovery' } }), { 'discovery 200': (r) => r.status === 200 });
  check(http.get(`${ORIGIN}/v1.0/me`, { headers: { Authorization: `Bearer ${data.token}` }, tags: { call: 'me' } }),
    { 'me 200 with mail': (r) => r.status === 200 && !!r.json().mail });
  // One user shared by every VU: stay inside its rate-limit budget (10 requests/s sustained) so the
  // run measures latency, not the limiter. Unpaced, the limiter answers 429 + Retry-After as designed.
  sleep(1);
}
