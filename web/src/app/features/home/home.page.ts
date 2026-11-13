import { Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { OidcSecurityService } from 'angular-auth-oidc-client';

interface Feature {
  title: string;
  text: string;
  status: 'live' | 'next' | 'planned';
  icon: string; // SVG path data, 24x24
}

@Component({
  selector: 'bp-home-page',
  imports: [RouterLink],
  template: `
    <section class="hero page">
      <div class="hero-copy">
        <span class="eyebrow"><span class="dot"></span> Agent-first home improvement</span>
        <h1>Plan the project.<br /><span class="grad">Let the agents do the legwork.</span></h1>
        <p class="lede">
          BuilderPro pairs AI agents with a strict domain: agents interpret and propose, while prices,
          dates and approvals always come from the platform.
        </p>

        @if (isAuthenticated()) {
          <div class="cta" data-testid="home-signed-in">
            <a routerLink="/me" class="btn btn-primary">See who you are to the platform →</a>
            <span class="hint">You're signed in.</span>
          </div>
        } @else {
          <div class="cta" data-testid="home-signed-out">
            <a routerLink="/me" class="btn btn-primary">Sign in to continue →</a>
            <span class="hint">
              This page is public. <em>Who am I</em> is protected, so opening it sends you to sign in.
            </span>
          </div>
          <div class="testers">
            <span class="badge">Local test users</span>
            <code>alice</code><code>bob</code><code>carol</code>
            <span class="pw">password <code>Passw0rd!</code></span>
          </div>
        }
      </div>

      <div class="hero-art" aria-hidden="true">
        <div class="blueprint">
          <svg viewBox="0 0 200 160" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linejoin="round">
            <path d="M20 80 100 20l80 60" />
            <path d="M35 70v70h130V70" />
            <rect x="55" y="92" width="30" height="48" />
            <rect x="110" y="92" width="36" height="26" />
            <path d="M128 92v26M110 105h36" />
            <path d="M140 38V18h14v31" />
            <path d="M10 148h180" stroke-dasharray="4 4" />
          </svg>
          <div class="chip c1"><span class="badge badge-ok">● Catalog</span></div>
          <div class="chip c2"><span class="badge badge-accent">Quote drafted</span></div>
          <div class="chip c3"><span class="badge badge-info">Agent · reading</span></div>
        </div>
      </div>
    </section>

    <section class="page features">
      @for (f of features; track f.title) {
        <article class="card feature">
          <div class="card-head">
            <span class="icon-tile"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path [attr.d]="f.icon" /></svg></span>
            <div>
              <h2>{{ f.title }}</h2>
              @switch (f.status) {
                @case ('live') { <span class="badge badge-ok">Live</span> }
                @case ('next') { <span class="badge badge-accent">Up next</span> }
                @default { <span class="badge">Planned</span> }
              }
            </div>
          </div>
          <p>{{ f.text }}</p>
        </article>
      }
    </section>
  `,
  styles: `
    .hero {
      display: grid; grid-template-columns: 1.15fr .85fr; gap: 3rem; align-items: center;
      padding-top: 4rem; padding-bottom: 2rem;
    }
    .dot { width: .5rem; height: .5rem; border-radius: 50%; background: var(--accent); box-shadow: 0 0 0 4px var(--accent-soft); }
    h1 { font-size: clamp(2.2rem, 4.5vw, 3.4rem); font-weight: 700; }
    .grad {
      background: linear-gradient(100deg, var(--accent) 10%, var(--accent-strong) 60%, #f59e0b);
      -webkit-background-clip: text; background-clip: text; color: transparent;
    }
    .lede { font-size: 1.1rem; color: var(--text-muted); max-width: 36rem; margin: 1.25rem 0 1.75rem; }
    .cta { display: flex; flex-wrap: wrap; align-items: center; gap: .9rem 1.1rem; }
    .cta .btn { padding: .75rem 1.3rem; font-size: .95rem; }
    .hint { color: var(--text-muted); font-size: .88rem; }
    .testers { display: flex; flex-wrap: wrap; align-items: center; gap: .45rem; margin-top: 1.5rem; font-size: .85rem; }
    .pw { color: var(--text-muted); margin-left: .25rem; }

    .blueprint {
      position: relative; aspect-ratio: 5 / 4; border-radius: var(--radius-lg);
      color: color-mix(in srgb, var(--accent) 70%, var(--text));
      background:
        linear-gradient(var(--border) 1px, transparent 1px) 0 0 / 22px 22px,
        linear-gradient(90deg, var(--border) 1px, transparent 1px) 0 0 / 22px 22px,
        var(--surface);
      border: 1px solid var(--border); box-shadow: var(--shadow-lg);
      display: grid; place-items: center; padding: 2rem;
    }
    .blueprint svg { width: 78%; height: auto; }
    .chip { position: absolute; filter: drop-shadow(0 6px 14px rgba(0,0,0,.12)); animation: float 6s ease-in-out infinite; }
    .chip .badge { background: var(--surface); border: 1px solid var(--border); padding: .35rem .75rem; font-size: .8rem; }
    .c1 { top: 12%; left: -6%; }
    .c2 { bottom: 16%; right: -5%; animation-delay: -2s; }
    .c3 { top: 6%; right: 8%; animation-delay: -4s; }
    @keyframes float { 50% { transform: translateY(-8px); } }

    .features {
      display: grid; grid-template-columns: repeat(auto-fit, minmax(15rem, 1fr)); gap: 1.1rem;
      padding-top: 1rem;
    }
    .feature { transition: transform .2s ease, box-shadow .2s ease; }
    .feature:hover { transform: translateY(-3px); box-shadow: var(--shadow-lg); }
    .feature h2 { margin-bottom: .35rem; }
    .feature p { margin: 0; color: var(--text-muted); font-size: .92rem; }

    @media (max-width: 860px) {
      .hero { grid-template-columns: 1fr; padding-top: 2.5rem; gap: 2rem; }
      .hero-art { max-width: 28rem; }
      .c1 { left: 2%; } .c2 { right: 2%; }
    }
  `,
})
export class HomePage {
  private readonly oidc = inject(OidcSecurityService);
  protected readonly isAuthenticated = computed(() => this.oidc.authenticated().isAuthenticated);

  protected readonly features: Feature[] = [
    {
      title: 'Secure by default',
      text: 'Sign in with any OIDC provider. Your tenant comes from platform memberships, never from a token alone.',
      status: 'live',
      icon: 'M12 3 4 6v6c0 4.5 3.4 8.3 8 9 4.6-.7 8-4.5 8-9V6l-8-3Zm-3 9 2 2 4-4',
    },
    {
      title: 'Retailer catalog',
      text: 'Each retailer sees only its own products and list prices, isolated at every query.',
      status: 'live',
      icon: 'M3 7h18M3 7l2-3h14l2 3M3 7v12a1 1 0 0 0 1 1h16a1 1 0 0 0 1-1V7M9 11h6',
    },
    {
      title: 'Exact quotes',
      text: 'Totals, rounding and currency are computed by domain code. Same request, same price, every time.',
      status: 'next',
      icon: 'M7 3h10a2 2 0 0 1 2 2v14l-3-2-2 2-2-2-2 2-2-2-3 2V5a2 2 0 0 1 2-2Zm2 5h6M9 12h6',
    },
    {
      title: 'Helpful agents',
      text: 'Agents answer questions and draft quotes using platform tools. A person approves every side effect.',
      status: 'planned',
      icon: 'M12 3v3M5 9h14a2 2 0 0 1 2 2v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-6a2 2 0 0 1 2-2Zm4 4v1m6-1v1',
    },
  ];
}
