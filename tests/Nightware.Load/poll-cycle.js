// k6 model of one NightMail client's poll cycle against Nightware, for N concurrent users.
//
//   tests/Nightware.Load/run.sh poll-cycle.js          (or: make load)
//
// Each virtual user is one signed-in NightMail device. Per cycle (default every 30 s, the
// client's default poll interval) it does what mail_poller_cubit.dart and the calendar/task
// syncs do at steady state:
//   1. inbox delta (follows @odata.nextLink, keeps the deltaLink for the next cycle; a 400/404/410
//      drops it and re-bootstraps, exactly like the client)
//   2. the folder list
//   3. calendarView for the next 28 days (Prefer: outlook.timezone="UTC")
//   4. To Do task lists
// Environment (all optional): see README.md.
import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter } from 'k6/metrics';

const BASE = __ENV.GRAPH_BASE || 'https://graph.nightware.localhost/v1.0';
const TOKEN_URL = __ENV.TOKEN_URL || 'https://auth.nightware.localhost/realms/nightware/protocol/openid-connect/token';
const USERS = (__ENV.LOAD_USERS || 'alice,bob').split(',');
const PASSWORD = __ENV.TEST_USER_PASSWORD;
const CLIENT_SECRET = __ENV.NIGHTWARE_DEV_CLIENT_SECRET;
const VUS = parseInt(__ENV.VUS || '10', 10);
const DURATION = __ENV.DURATION || '5m';
const POLL_INTERVAL = parseFloat(__ENV.POLL_INTERVAL || '30');
// Routes a feature phase has not implemented yet answer 501; they are counted, not failed.
const ALLOW_501 = (__ENV.ALLOW_501 || 'true') === 'true';

export const options = {
  scenarios: {
    poll: {
      executor: 'constant-vus',
      vus: VUS,
      duration: DURATION,
      gracefulStop: '30s',
    },
  },
  thresholds: {
    // Target from docs/phases/phase-6-hardening.md: p95 under 300 ms per poll-cycle call.
    'http_req_duration{call:delta}': ['p(95)<300'],
    'http_req_duration{call:folders}': ['p(95)<300'],
    'http_req_duration{call:calendarView}': ['p(95)<300'],
    'http_req_duration{call:taskLists}': ['p(95)<300'],
    // Throttling or backend failure under the modelled load is a regression.
    http_req_failed: ['rate<0.01'],
    checks: ['rate>0.99'],
    throttled: ['count==0'],
  },
};

const throttled = new Counter('throttled');
const notImplemented = new Counter('not_implemented');

http.setResponseCallback(ALLOW_501 ? http.expectedStatuses({ min: 200, max: 399 }, 501) : http.expectedStatuses({ min: 200, max: 399 }));

function fetchToken(user) {
  const res = http.post(TOKEN_URL, {
    grant_type: 'password',
    client_id: 'nightware-dev',
    client_secret: CLIENT_SECRET,
    username: user,
    password: PASSWORD,
    scope: 'openid',
  }, { tags: { call: 'token' } });
  if (res.status !== 200) {
    throw new Error(`token for ${user}: HTTP ${res.status} ${res.body}`);
  }
  const body = res.json();
  return { value: body.access_token, expiresAt: Date.now() + (body.expires_in - 30) * 1000 };
}

// Per-VU state (k6 runs each VU in its own JS runtime).
const state = { token: null, deltaLink: null };

function token() {
  if (!state.token || Date.now() > state.token.expiresAt) {
    state.token = fetchToken(USERS[(__VU - 1) % USERS.length]);
  }
  return state.token.value;
}

function get(url, call, extraHeaders) {
  const res = http.get(url, {
    headers: Object.assign({ Authorization: `Bearer ${token()}`, 'client-request-id': `k6-${__VU}-${__ITER}-${call}` }, extraHeaders || {}),
    tags: { call },
  });
  if (res.status === 429) throttled.add(1);
  if (res.status === 501) notImplemented.add(1, { call });
  check(res, {
    [`${call}: expected status`]: (r) => (r.status >= 200 && r.status < 300) || (ALLOW_501 && r.status === 501),
    [`${call}: integer Retry-After on 429/503`]: (r) => ![429, 503].includes(r.status) || /^\d+$/.test(r.headers['Retry-After'] || ''),
  });
  return res;
}

function inboxDelta() {
  let url = state.deltaLink || `${BASE}/me/mailFolders/inbox/messages/delta?$select=id,subject,from,receivedDateTime,isRead,flag&$top=50`;
  for (let page = 0; page < 10; page++) {
    const res = get(url, 'delta', { Prefer: 'odata.maxpagesize=50' });
    if ([400, 404, 410].includes(res.status)) {
      state.deltaLink = null; // the client drops its token and re-bootstraps next cycle
      return;
    }
    if (res.status !== 200) return;
    const body = res.json();
    if (body['@odata.nextLink']) {
      url = body['@odata.nextLink'];
      continue;
    }
    state.deltaLink = body['@odata.deltaLink'] || null;
    return;
  }
}

export default function () {
  inboxDelta();
  get(`${BASE}/me/mailFolders?$top=100`, 'folders');
  const start = new Date();
  const end = new Date(start.getTime() + 28 * 24 * 3600 * 1000);
  get(`${BASE}/me/calendarView?startDateTime=${start.toISOString()}&endDateTime=${end.toISOString()}&$top=200`, 'calendarView',
    { Prefer: 'outlook.timezone="UTC"' });
  get(`${BASE}/me/todo/lists`, 'taskLists');
  sleep(POLL_INTERVAL * (0.9 + Math.random() * 0.2)); // devices drift apart, as real timers do
}
