import { toOpenIdConfiguration } from './auth.config';

describe('toOpenIdConfiguration', () => {
  const cfg = toOpenIdConfiguration(
    { authority: 'http://localhost:8080/realms/builderpro', clientId: 'builderpro-web', scope: 'openid profile email' },
    'http://localhost:4200',
  );

  it('uses Authorization Code + PKCE', () => {
    expect(cfg.responseType).toBe('code');
  });

  it('sends tokens only to our own API', () => {
    expect(cfg.secureRoutes).toEqual(['/api/']);
  });

  it('returns to the app after login and logout', () => {
    expect(cfg.redirectUrl).toBe('http://localhost:4200');
    expect(cfg.postLogoutRedirectUri).toBe('http://localhost:4200');
  });

  it('takes the identity provider from the server, not from the build', () => {
    expect(cfg.authority).toBe('http://localhost:8080/realms/builderpro');
    expect(cfg.clientId).toBe('builderpro-web');
  });
});
