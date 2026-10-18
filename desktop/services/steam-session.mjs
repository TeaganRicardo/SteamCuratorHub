/** Inspect token claims for routing only. Steam authenticates the token itself. */
export function refreshTokenIdentity(token, now = Date.now()) {
  let claims;
  try {
    if (typeof token !== 'string' || token.length > 8000 || !/^[\w-]+\.[\w-]+\.[\w-]+$/.test(token)) throw new Error();
    claims = JSON.parse(Buffer.from(token.split('.')[1], 'base64url').toString('utf8'));
    if (claims.iss !== 'steam' || !Array.isArray(claims.aud) || !claims.aud.includes('client') ||
        typeof claims.sub !== 'string' || !/^\d{17}$/.test(claims.sub) || !Number.isFinite(claims.exp)) throw new Error();
  } catch { throw Object.assign(new Error('invalid_steam_session'), { code: 'invalid_steam_session' }); }
  if (claims.exp * 1000 <= now) throw Object.assign(new Error('steam_session_expired'), { code: 'steam_session_expired' });
  return claims.sub;
}
