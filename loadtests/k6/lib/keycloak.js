import http from 'k6/http';

const KEYCLOAK = __ENV.KEYCLOAK || 'http://localhost:5260';
const PASSWORD = 'password';

function token(realm, clientId, username, password) {
  const res = http.post(`${KEYCLOAK}/realms/${realm}/protocol/openid-connect/token`, {
    grant_type: 'password', client_id: clientId, username, password,
  });
  if (res.status !== 200) throw new Error(`token for ${username}: ${res.status} ${res.body}`);
  return res.json('access_token');
}

/** Creates (idempotently) `count` customers named loadtest-001.. and returns one access token per customer. */
export function customerTokens(count) {
  const admin = token('master', 'admin-cli', 'admin', 'admin');
  const headers = { Authorization: `Bearer ${admin}`, 'Content-Type': 'application/json' };
  const tokens = [];
  for (let i = 1; i <= count; i++) {
    const username = `loadtest-${String(i).padStart(3, '0')}`;
    const created = http.post(`${KEYCLOAK}/admin/realms/tickethub/users`, JSON.stringify({
      username, email: `${username}@example.com`, firstName: 'Load', lastName: 'Test',
      enabled: true, emailVerified: true,
      credentials: [{ type: 'password', value: PASSWORD, temporary: false }],
    }), { headers });
    if (created.status !== 201 && created.status !== 409) throw new Error(`create ${username}: ${created.status}`);
    tokens.push(token('tickethub', 'tickethub-cli', username, PASSWORD));
  }
  return tokens;
}
