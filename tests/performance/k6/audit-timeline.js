// k6 Test Plan v1.0 for AUD-API-001, the Work Order timeline (UC-WO-002; docs/15 §7 and §13, D11).
// Local evidence tooling: NOT part of CI, never pointed at a production environment. See ../README.md.
//
//   k6 run -e PROFILE=large -e FIXTURE=<fixture.json> -e BASE_URL=http://127.0.0.1:5199 -e SUMMARY_OUT=<out.json> audit-timeline.js
//
// PROFILE   smoke | warmup | small | medium | large | manyids | bigids | negative   (default smoke)
// THINK     think time per iteration in seconds (default 0.5; 0 = saturation run, supplementary evidence only)
// VUS       virtual users for the acceptance profiles (default 100)
// HOLD      plateau duration (default 2m30s); ramp-up 30 s, ramp-down 10 s
// DEEP_WOS  Work Orders per profile whose deep-page cursors are precomputed in setup() (default 10)
//
// The FIXTURE (PerfSeed output) holds test credentials; it is generated locally and never committed.
// Every response is validated; any deviation counts into `unexpected_response`, which must stay at 0.

import http from 'k6/http';
import { sleep } from 'k6';
import { Rate, Counter } from 'k6/metrics';

const BASE_URL = __ENV.BASE_URL || 'http://127.0.0.1:5199';
const PROFILE = (__ENV.PROFILE || 'smoke').toLowerCase();
const THINK = __ENV.THINK === undefined ? 0.5 : Number(__ENV.THINK);
const VUS = Number(__ENV.VUS || 100);
const HOLD = __ENV.HOLD || '2m30s';
const DEEP_WOS = Number(__ENV.DEEP_WOS || 10);
const SUMMARY_OUT = __ENV.SUMMARY_OUT || 'timeline-summary.json';
const PAGE_SIZE = 20;
const PROFILE_NAMES = ['SMALL', 'MEDIUM', 'LARGE', 'MANYIDS', 'BIGIDS'];
const CALLER_KEYS = ['supervisor', 'coordinator', 'teamLead', 'approver', 'requester'];
const ENTITY_TYPES = ['WORK_ORDER', 'SERVICE_VISIT', 'WORK_SESSION', 'CORRECTIVE_ACTION', 'REPAIR_REQUEST'];
const FORBIDDEN_KEYS = ['reason', 'correlationId', 'oldValueJson', 'newValueJson', 'newValue', 'oldValue', 'email', 'userName', 'password', 'token'];

const fixture = JSON.parse(open(__ENV.FIXTURE || './fixture.json'));

const unexpectedResponse = new Rate('unexpected_response');
const unexpectedDetail = new Counter('unexpected_response_count');

const hold = HOLD;
const acceptance = (name) => ({
  executor: 'ramping-vus',
  exec: 'positive',
  startVUs: 0,
  stages: [
    { duration: '30s', target: VUS },
    { duration: hold, target: VUS },
    { duration: '10s', target: 0 },
  ],
  gracefulRampDown: '10s',
  tags: { run: name },
});

function scenarios() {
  switch (PROFILE) {
    case 'smoke':
      return { smoke: { executor: 'constant-vus', exec: 'smokeWalk', vus: 1, duration: '35s' } };
    case 'warmup':
      return { warmup: { executor: 'ramping-vus', exec: 'positive', startVUs: 0, stages: [{ duration: '10s', target: 20 }, { duration: '50s', target: 20 }], gracefulRampDown: '5s' } };
    case 'negative':
      return { negative: { executor: 'constant-vus', exec: 'negative', vus: 10, duration: '60s' } };
    default:
      return { [PROFILE]: acceptance(PROFILE) };
  }
}

export const options = {
  scenarios: scenarios(),
  // Sub-metric declarations (always-true thresholds) make k6 export the per-tag statistics used by handleSummary.
  thresholds: {
    'unexpected_response': [{ threshold: 'rate==0', abortOnFail: false }],
    'http_req_duration{traffic:positive}': [{ threshold: 'p(95)<3000', abortOnFail: false }],
    'http_req_duration{traffic:positive,page:first}': ['p(95)>=0'],
    'http_req_duration{traffic:positive,page:deep}': ['p(95)>=0'],
    'http_req_failed{traffic:positive}': ['rate>=0'],
    'http_req_duration{traffic:negative}': ['p(95)>=0'],
    'http_req_failed{traffic:negative}': ['rate>=0'],
  },
  summaryTrendStats: ['avg', 'min', 'med', 'p(90)', 'p(95)', 'p(99)', 'max', 'count'],
  insecureSkipTLSVerify: true,
};

// ---------------------------------------------------------------------------------------------- helpers

function url(workOrderId, query, type) {
  return `${BASE_URL}/api/v1/entities/${type || 'WORK_ORDER'}/${workOrderId}/timeline${query ? `?${query}` : ''}`;
}

function auth(token) {
  return token ? { Authorization: `Bearer ${token}` } : {};
}

function get(target, token, tags, expected) {
  const params = { headers: auth(token), tags };
  if (expected) {
    params.responseCallback = http.expectedStatuses(...expected);
  }
  return http.get(target, params);
}

function fail(reason, tags) {
  unexpectedResponse.add(true, tags);
  unexpectedDetail.add(1, { reason });
  if (__ENV.DEBUG) {
    console.error(`UNEXPECTED: ${reason}`);
  }
  return false;
}

// A deviation inside validatePage: count it and signal "no usable body".
function bad(reason, tags) {
  fail(reason, tags);
  return null;
}

function pass(tags) {
  unexpectedResponse.add(false, tags);
  return true;
}

// Strict contract validation of one 200 page; returns the parsed body or null (and counts the deviation).
function validatePage(response, tags, expectedItems, expectHasMore, maxItems) {
  if (response.status !== 200) {
    fail(`status ${response.status}`, tags);
    return null;
  }
  if (!String(response.headers['Content-Type'] || '').includes('application/json')) {
    fail('content-type', tags);
    return null;
  }
  if (!String(response.headers['Cache-Control'] || '').includes('no-store')) {
    fail('cache-control', tags);
    return null;
  }

  let body;
  try {
    body = response.json();
  } catch (e) {
    fail('json parse', tags);
    return null;
  }

  if (!body || !Array.isArray(body.items) || typeof body.hasMore !== 'boolean') {
    fail('shape', tags);
    return null;
  }
  if (body.items.length > (maxItems || PAGE_SIZE) || (expectedItems !== undefined && body.items.length !== expectedItems)) {
    fail(`items ${body.items.length}`, tags);
    return null;
  }
  if (body.hasMore !== (typeof body.nextCursor === 'string' && body.nextCursor.length > 0) || (expectHasMore !== undefined && body.hasMore !== expectHasMore)) {
    fail('hasMore/nextCursor', tags);
    return null;
  }
  if (body.nextCursor && (body.nextCursor.length > 128 || !/^[A-Za-z0-9_-]+$/.test(body.nextCursor))) {
    fail('cursor format', tags);
    return null;
  }

  let previous = null;
  for (const item of body.items) {
    if (!item.auditId || !item.occurredAt || !item.actionCode || !item.entityId || !item.actor || !item.actor.actorId || !item.actor.kind) {
      return bad('item fields', tags);
    }
    if (ENTITY_TYPES.indexOf(item.entityType) < 0 || ['USER', 'SYSTEM', 'UNKNOWN'].indexOf(item.actor.kind) < 0) {
      return bad('item enum', tags);
    }
    if (!/Z$/.test(item.occurredAt) || (previous !== null && item.occurredAt > previous)) {
      return bad('occurredAt', tags);
    }
    previous = item.occurredAt;
    if (item.details !== null && item.details !== undefined && (typeof item.details !== 'object' || Array.isArray(item.details))) {
      return bad('details shape', tags);
    }
    for (const key of FORBIDDEN_KEYS) {
      if (Object.prototype.hasOwnProperty.call(item, key) || (item.actor && Object.prototype.hasOwnProperty.call(item.actor, key))) {
        return bad(`forbidden key ${key}`, tags);
      }
    }
  }

  pass(tags);
  return body;
}

function login(email) {
  const response = http.post(`${BASE_URL}/api/v1/auth/login`, JSON.stringify({ email, password: fixture.password }), {
    headers: { 'Content-Type': 'application/json' },
    tags: { traffic: 'setup' },
  });
  if (response.status !== 200) {
    throw new Error(`login failed for a fixture user: HTTP ${response.status}`);
  }
  return response.json('accessToken');
}

// ---------------------------------------------------------------------------------------------- setup

export function setup() {
  const tokens = {};
  const keys = PROFILE === 'negative' || PROFILE === 'smoke' ? Object.keys(fixture.users) : CALLER_KEYS;
  for (const key of keys) {
    tokens[key] = login(fixture.users[key]);
  }

  const wanted = PROFILE === 'warmup' ? PROFILE_NAMES : PROFILE === 'negative' || PROFILE === 'smoke' ? ['MEDIUM', 'LARGE'] : [PROFILE.toUpperCase()];
  const data = { tokens, profiles: {}, started: new Date().toISOString() };
  for (const name of wanted) {
    const all = fixture.profiles[name];
    if (!all) {
      throw new Error(`unknown profile ${name}`);
    }
    const entries = all.map((wo) => ({ workOrderId: wo.workOrderId, expected: wo.expectedEvents, cursors: [] }));
    const limit = Math.min(entries.length, PROFILE === 'warmup' ? 3 : DEEP_WOS);
    for (let i = 0; i < limit; i++) {
      entries[i].cursors = collectCursors(entries[i], tokens.supervisor);
    }
    data.profiles[name] = entries;
  }
  return data;
}

// Walks real pages (pageSize 20) and keeps the cursors found at about 50 % and 80 % depth. A timeline of at most
// one page has no cursor: there is no deep page to request.
function collectCursors(entry, token) {
  const cursors = [];
  const pages = Math.ceil(entry.expected / PAGE_SIZE);
  if (pages < 2) {
    return cursors;
  }
  const marks = [Math.floor(pages * 0.5), Math.floor(pages * 0.8)].filter((p) => p >= 1);
  let cursor = null;
  for (let page = 1; page < pages; page++) {
    const response = http.get(url(entry.workOrderId, `pageSize=${PAGE_SIZE}${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ''}`), {
      headers: auth(token),
      tags: { traffic: 'setup' },
    });
    if (response.status !== 200) {
      throw new Error(`setup walk failed HTTP ${response.status}`);
    }
    cursor = response.json('nextCursor');
    if (!cursor) {
      break;
    }
    if (marks.indexOf(page) >= 0) {
      cursors.push(cursor);
    }
  }
  return cursors;
}

// ---------------------------------------------------------------------------------------------- positive load

function pick(list) {
  return list[Math.floor(Math.random() * list.length)];
}

export function positive(data) {
  const profileName = pick(Object.keys(data.profiles));
  const entries = data.profiles[profileName];
  const caller = CALLER_KEYS[(__VU - 1) % CALLER_KEYS.length];
  const token = data.tokens[caller];

  const wantDeep = Math.random() < 0.5;
  const withCursors = entries.filter((e) => e.cursors.length > 0);

  if (wantDeep && withCursors.length > 0) {
    const entry = pick(withCursors);
    const tags = { traffic: 'positive', page: 'deep', profile: profileName };
    const response = get(url(entry.workOrderId, `pageSize=${PAGE_SIZE}&cursor=${encodeURIComponent(pick(entry.cursors))}`), token, tags);
    validatePage(response, tags);
  } else {
    const entry = pick(entries);
    const tags = { traffic: 'positive', page: 'first', profile: profileName };
    const response = get(url(entry.workOrderId, `pageSize=${PAGE_SIZE}`), token, tags);
    validatePage(response, tags, Math.min(PAGE_SIZE, entry.expected), entry.expected > PAGE_SIZE);
  }

  if (THINK > 0) {
    sleep(THINK);
  }
}

// ---------------------------------------------------------------------------------------------- smoke

export function smokeWalk(data) {
  const token = data.tokens.supervisor;
  const entry = data.profiles.MEDIUM[0];
  const tags = { traffic: 'positive', page: 'first', profile: 'SMOKE' };

  // Cursor continuation: walk the whole timeline, exactly once, newest first.
  const ids = [];
  let cursor = null;
  let pages = 0;
  do {
    const response = get(url(entry.workOrderId, `pageSize=${PAGE_SIZE}${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ''}`), token, { ...tags, page: cursor ? 'deep' : 'first' });
    const body = validatePage(response, { ...tags, page: cursor ? 'deep' : 'first' });
    if (!body) {
      return;
    }
    body.items.forEach((item) => ids.push(item.auditId));
    cursor = body.nextCursor || null;
    pages++;
  } while (cursor && pages < 1000);

  if (ids.length !== entry.expected || new Set(ids).size !== ids.length) {
    fail(`walk ${ids.length}/${entry.expected} distinct ${new Set(ids).size}`, tags);
  } else {
    pass(tags);
  }

  // pageSize contract: 100 is honoured, above 100 is clamped to 100, 0 is rejected.
  const large = entry.expected >= 100 ? get(url(LARGE_ID(data), 'pageSize=100'), token, tags) : null;
  if (large) {
    validatePage(large, tags, 100, true, 100);
    validatePage(get(url(LARGE_ID(data), 'pageSize=500'), token, tags), tags, 100, true, 100);
  }
  const zero = get(url(entry.workOrderId, 'pageSize=0'), token, tags, [400]);
  zero.status === 400 ? pass(tags) : fail(`pageSize=0 -> ${zero.status}`, tags);

  // 401 without a token and 405 for a write verb.
  const anon = get(url(entry.workOrderId, ''), null, tags, [401]);
  anon.status === 401 ? pass(tags) : fail(`anonymous -> ${anon.status}`, tags);
  const post = http.post(url(entry.workOrderId, ''), '{}', { headers: { ...auth(token), 'Content-Type': 'application/json' }, tags, responseCallback: http.expectedStatuses(405) });
  post.status === 405 ? pass(tags) : fail(`POST -> ${post.status}`, tags);

  sleep(0.5);
}

function LARGE_ID(data) {
  return data.profiles.LARGE[0].workOrderId;
}

// ---------------------------------------------------------------------------------------------- negative traffic

function problemOk(response, status, tags) {
  if (response.status !== status) {
    return fail(`expected ${status} got ${response.status}`, tags);
  }
  if (!String(response.headers['Content-Type'] || '').includes('application/problem+json') && status !== 401) {
    return fail(`problem content-type for ${status}`, tags);
  }
  const text = String(response.body || '');
  if (/Exception|StackTrace|at RepairRequest|SqlException|HMAC|audit_history|Microsoft\.|stack/i.test(text)) {
    return fail(`sensitive text in ${status} body`, tags);
  }
  return pass(tags);
}

function garbageCursor() {
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_';
  let s = '';
  for (let i = 0; i < 76; i++) {
    s += alphabet[Math.floor(Math.random() * alphabet.length)];
  }
  return s;
}

function tamper(cursor) {
  const i = Math.floor(cursor.length / 2);
  return cursor.slice(0, i) + (cursor[i] === 'A' ? 'B' : 'A') + cursor.slice(i + 1);
}

export function negative(data) {
  const wo = data.profiles.LARGE[0];
  const other = data.profiles.MEDIUM[0];
  const cursor = wo.cursors[0];
  const t = data.tokens;
  const cases = [
    { name: 'no-token', status: 401, url: url(wo.workOrderId, `pageSize=${PAGE_SIZE}`), token: null },
    { name: 'invalid-token', status: 401, url: url(wo.workOrderId, `pageSize=${PAGE_SIZE}`), token: 'not.a.jwt' },
    { name: 'technician', status: 403, url: url(wo.workOrderId, `pageSize=${PAGE_SIZE}`), token: t.technician },
    { name: 'administrator', status: 403, url: url(wo.workOrderId, `pageSize=${PAGE_SIZE}`), token: t.administrator },
    { name: 'cursor-malformed', status: 400, url: url(wo.workOrderId, 'cursor=not-a-cursor'), token: t.supervisor },
    { name: 'cursor-garbage-76', status: 400, url: url(wo.workOrderId, `cursor=${garbageCursor()}`), token: t.supervisor },
    { name: 'cursor-tampered', status: 400, url: url(wo.workOrderId, `cursor=${encodeURIComponent(tamper(cursor))}`), token: t.supervisor },
    { name: 'cursor-foreign-bound', status: 400, url: url(other.workOrderId, `cursor=${encodeURIComponent(cursor)}`), token: t.supervisor },
    { name: 'pagesize-zero', status: 400, url: url(wo.workOrderId, 'pageSize=0'), token: t.supervisor },
    { name: 'missing-work-order', status: 404, url: url(fixture.negative.missingWorkOrderId, `pageSize=${PAGE_SIZE}`), token: t.supervisor },
    { name: 'other-site', status: 404, url: url(fixture.negative.otherSiteWorkOrderId, `pageSize=${PAGE_SIZE}`), token: t.supervisor },
    { name: 'other-tenant', status: 404, url: url(fixture.negative.otherTenantWorkOrderId, `pageSize=${PAGE_SIZE}`), token: t.supervisor },
    { name: 'requester-not-owner', status: 404, url: url(wo.workOrderId, `pageSize=${PAGE_SIZE}`), token: t.requesterOther },
    { name: 'site-b-supervisor', status: 404, url: url(wo.workOrderId, `pageSize=${PAGE_SIZE}`), token: t.supervisorOtherSite },
    { name: 'unsupported-entity-type', status: 404, url: url(wo.workOrderId, `pageSize=${PAGE_SIZE}`, 'USER'), token: t.supervisor },
  ];

  const chosen = cases[(__ITER + __VU) % cases.length];
  const tags = { traffic: 'negative', case: chosen.name };
  const response = get(chosen.url, chosen.token, tags, [chosen.status]);
  problemOk(response, chosen.status, tags);
  sleep(0.1);
}

// ---------------------------------------------------------------------------------------------- summary

function stats(metric) {
  if (!metric || !metric.values) {
    return null;
  }
  const v = metric.values;
  return { count: v.count, avg: v.avg, p50: v.med, p90: v['p(90)'], p95: v['p(95)'], p99: v['p(99)'], max: v.max };
}

function rate(metric) {
  return metric && metric.values ? metric.values.rate : null;
}

export function handleSummary(data) {
  const m = data.metrics;
  const scenarioSeconds = PROFILE === 'smoke' ? 35 : PROFILE === 'negative' ? 60 : PROFILE === 'warmup' ? 60 : 30 + toSeconds(HOLD) + 10;
  const positive = stats(m['http_req_duration{traffic:positive}']);
  const result = {
    profile: PROFILE.toUpperCase(),
    think: THINK,
    vus: PROFILE === 'smoke' ? 1 : PROFILE === 'negative' ? 10 : PROFILE === 'warmup' ? 20 : VUS,
    scenarioSeconds,
    positive,
    positiveRps: positive ? positive.count / scenarioSeconds : null,
    first: stats(m['http_req_duration{traffic:positive,page:first}']),
    deep: stats(m['http_req_duration{traffic:positive,page:deep}']),
    negative: stats(m['http_req_duration{traffic:negative}']),
    failedRatePositive: rate(m['http_req_failed{traffic:positive}']),
    failedRateNegative: rate(m['http_req_failed{traffic:negative}']),
    unexpectedResponseRate: m.unexpected_response ? m.unexpected_response.values.rate : null,
    unexpectedResponseCount: m.unexpected_response ? m.unexpected_response.values.passes : null,
    iterations: m.iterations ? m.iterations.values.count : null,
    p95GateMet: positive ? positive.p95 <= 3000 : null,
    thresholdsFailed: Object.keys(data.metrics).filter((k) => data.metrics[k].thresholds && Object.values(data.metrics[k].thresholds).some((t) => t.ok === false)),
    finishedAt: new Date().toISOString(),
  };

  const line = (label, s) => (s ? `${label.padEnd(10)} n=${s.count} p50=${s.p50.toFixed(1)} p90=${s.p90.toFixed(1)} p95=${s.p95.toFixed(1)} p99=${s.p99.toFixed(1)} max=${s.max.toFixed(1)} ms` : `${label.padEnd(10)} n/a`);
  const text = [
    `PROFILE ${result.profile} think=${THINK}s vus=${result.vus}`,
    line('positive', result.positive),
    line('first', result.first),
    line('deep', result.deep),
    line('negative', result.negative),
    `rps(positive)=${result.positiveRps ? result.positiveRps.toFixed(1) : 'n/a'} unexpected=${result.unexpectedResponseCount} (rate ${result.unexpectedResponseRate}) http_failed(positive)=${result.failedRatePositive}`,
    `p95 gate (<= 3000 ms): ${result.p95GateMet}`,
    '',
  ].join('\n');

  return { [SUMMARY_OUT]: JSON.stringify(result, null, 2), stdout: text };
}

function toSeconds(duration) {
  const m = /^(?:(\d+)m)?(?:(\d+)s)?$/.exec(duration);
  return (m && m[1] ? Number(m[1]) * 60 : 0) + (m && m[2] ? Number(m[2]) : 0);
}
