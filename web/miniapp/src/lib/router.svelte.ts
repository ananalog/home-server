import { setBack } from './tg';

/** Minimal hash router: #/, #/device/<id>, #/new, #/firmware, #/settings, #/events, #/logs/<id>. */
class Router {
  path = $state(location.hash.slice(1) || '/');

  constructor() {
    window.addEventListener('hashchange', () => {
      this.path = location.hash.slice(1) || '/';
      this.syncBack();
    });
    this.syncBack();
  }

  get parts(): string[] {
    return this.path.split('/').filter(Boolean).map(decodeURIComponent);
  }

  go(path: string) {
    location.hash = path;
  }

  back() {
    if (history.length > 1) history.back();
    else this.go('/');
  }

  private syncBack() {
    setBack(this.path === '/' ? null : () => this.back());
  }
}

export const router = new Router();
