import { api, openStream, setToken, ApiError, getToken } from './api';
import { tg } from './tg';
import type { Device, Me, OtaJob, Room } from './types';

type Phase = 'loading' | 'ready' | 'no-access' | 'no-telegram' | 'error';

/** Global reactive state of the app. */
class Store {
  phase = $state<Phase>('loading');
  error = $state('');
  noAccessId = $state<number | null>(null);
  me = $state<Me | null>(null);
  devices = $state<Record<string, Device>>({});
  rooms = $state<Room[]>([]);
  otaJobs = $state<OtaJob[]>([]);
  toast = $state<{ text: string; bad: boolean } | null>(null);
  private closeStream: (() => void) | null = null;
  private toastTimer = 0;

  get isAdmin() {
    return this.me?.role === 'Admin';
  }

  get canControl() {
    return this.me?.role === 'Admin' || this.me?.role === 'User';
  }

  get list(): Device[] {
    return Object.values(this.devices).sort((a, b) => a.name.localeCompare(b.name, 'ru'));
  }

  get adopted(): Device[] {
    return this.list.filter((d) => d.state === 'Adopted');
  }

  get newDevices(): Device[] {
    return this.list.filter((d) => d.state === 'New');
  }

  async start() {
    try {
      if (!getToken()) await this.login();
      await this.load();
    } catch (e) {
      if (e instanceof ApiError && e.status === 401 && getToken()) {
        setToken(null);
        try {
          await this.login();
          await this.load();
          return;
        } catch (e2) {
          this.fail(e2);
          return;
        }
      }
      this.fail(e);
    }
  }

  private fail(e: unknown) {
    if (this.phase === 'no-access' || this.phase === 'no-telegram') return;
    this.phase = 'error';
    this.error = e instanceof Error ? e.message : String(e);
  }

  private async login() {
    try {
      const r = tg ? await api.authTelegram(tg.initData) : await api.authDev();
      setToken(r.token);
    } catch (e) {
      if (e instanceof ApiError && e.status === 403 && e.message.startsWith('no_access:')) {
        this.noAccessId = Number(e.message.split(':')[1]);
        this.phase = 'no-access';
      } else if (!tg && e instanceof ApiError && e.status === 404) this.phase = 'no-telegram';
      throw e;
    }
  }

  async load() {
    const [me, devices, rooms] = await Promise.all([api.me(), api.devices(), api.rooms()]);
    this.me = me;
    this.rooms = rooms;
    this.devices = Object.fromEntries(devices.map((d) => [d.id, d]));
    this.phase = 'ready';
    this.closeStream?.();
    this.closeStream = openStream((t, d) => this.onEvent(t, d));
    if (this.isAdmin) api.otaJobs().then((j) => (this.otaJobs = j)).catch(() => {});
  }

  private onEvent(type: string, data: any) {
    switch (type) {
      case 'device':
        this.devices[data.id] = data;
        break;
      case 'values': {
        const d = this.devices[data.deviceId];
        if (d) d.values = { ...d.values, ...data.values };
        break;
      }
      case 'online': {
        const d = this.devices[data.deviceId];
        if (d) d.online = data.online;
        break;
      }
      case 'removed':
        delete this.devices[data.deviceId];
        break;
      case 'ota': {
        const i = this.otaJobs.findIndex((j) => j.id === data.id);
        if (i >= 0) this.otaJobs[i] = data;
        else this.otaJobs = [data, ...this.otaJobs];
        break;
      }
    }
  }

  async refreshRooms() {
    this.rooms = await api.rooms();
  }

  notify(text: string, bad = false) {
    this.toast = { text, bad };
    clearTimeout(this.toastTimer);
    this.toastTimer = window.setTimeout(() => (this.toast = null), bad ? 4000 : 2000);
  }

  /** Runs an action and shows errors as a toast. */
  async run<T>(fn: () => Promise<T>, ok?: string): Promise<T | undefined> {
    try {
      const r = await fn();
      if (ok) this.notify(ok);
      return r;
    } catch (e) {
      this.notify(e instanceof Error ? e.message : String(e), true);
      return undefined;
    }
  }
}

export const store = new Store();
