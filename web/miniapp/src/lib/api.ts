import type {
  AccessRequest, Device, EventItem, Firmware, History, LogLine, Me, NetStatus, NetworkCfg, OtaJob, Role, Room,
  SystemInfo, User, ValueDto, Json,
} from './types';

export class ApiError extends Error {
  constructor(public status: number, message: string) {
    super(message);
  }
}

let token: string | null = sessionStorageGet('home.token');

function sessionStorageGet(k: string): string | null {
  try {
    return sessionStorage.getItem(k);
  } catch {
    return null;
  }
}

export function setToken(t: string | null) {
  token = t;
  try {
    if (t) sessionStorage.setItem('home.token', t);
    else sessionStorage.removeItem('home.token');
  } catch {
    /* private mode */
  }
}

export const getToken = () => token;

async function call<T>(method: string, url: string, body?: unknown): Promise<T> {
  const headers: Record<string, string> = {};
  if (token) headers.Authorization = `Bearer ${token}`;
  let payload: BodyInit | undefined;
  if (body instanceof FormData) payload = body;
  else if (body !== undefined) {
    headers['Content-Type'] = 'application/json';
    payload = JSON.stringify(body);
  }
  const resp = await fetch(`api/v1/${url}`, { method, headers, body: payload });
  if (!resp.ok) {
    let msg = resp.statusText;
    try {
      msg = (await resp.json()).error ?? msg;
    } catch {
      /* not json */
    }
    throw new ApiError(resp.status, msg);
  }
  if (resp.status === 204) return undefined as T;
  const text = await resp.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

const e = encodeURIComponent;

export const api = {
  authTelegram: (initData: string) => call<{ token: string; user: User }>('POST', 'auth/telegram', { initData }),
  authDev: () => call<{ token: string; user: User }>('POST', 'auth/dev'),
  me: () => call<Me>('GET', 'me'),
  setMyNotify: (notify: boolean) => call<User>('PATCH', 'me', { telegramId: 0, notify }),
  system: () => call<SystemInfo>('GET', 'system'),

  devices: () => call<Device[]>('GET', 'devices'),
  device: (id: string) => call<Device>('GET', `devices/${e(id)}`),
  updateDevice: (id: string, name?: string, room?: string) => call<Device>('PATCH', `devices/${e(id)}`, { name, room }),
  adopt: (id: string) => call<void>('POST', `devices/${e(id)}/adopt`),
  reject: (id: string) => call<void>('POST', `devices/${e(id)}/reject`),
  remove: (id: string) => call<void>('DELETE', `devices/${e(id)}`),
  set: (id: string, key: string, value: Json) => call<ValueDto>('POST', `devices/${e(id)}/points/${e(key)}`, { value }),
  invoke: (id: string, key: string, arg?: number) => call<{ text?: string }>('POST', `devices/${e(id)}/actions/${e(key)}`, { arg }),
  reboot: (id: string) => call<void>('POST', `devices/${e(id)}/reboot`),
  identify: (id: string, seconds = 10) => call<void>('POST', `devices/${e(id)}/identify`, { seconds }),
  factoryReset: (id: string, mode: 'settings' | 'firmware' | 'all') => call<void>('POST', `devices/${e(id)}/factory-reset`, { mode }),
  network: (id: string) => call<NetStatus>('GET', `devices/${e(id)}/network`),
  setNetwork: (id: string, n: NetworkCfg) => call<void>('PUT', `devices/${e(id)}/network`, n),
  history: (id: string, point: string, from: number, to: number, step = 0) =>
    call<History>('GET', `devices/${e(id)}/history?point=${e(point)}&from=${from}&to=${to}&step=${step}`),
  logs: (id: string) => call<LogLine[]>('GET', `devices/${e(id)}/logs`),
  events: (device?: string, limit = 100) => call<EventItem[]>('GET', `events?limit=${limit}${device ? `&device=${e(device)}` : ''}`),

  rooms: () => call<Room[]>('GET', 'rooms'),
  saveRoom: (r: Room) => call<Room>('POST', 'rooms', r),
  removeRoom: (id: string) => call<void>('DELETE', `rooms/${e(id)}`),

  users: () => call<User[]>('GET', 'users'),
  upsertUser: (telegramId: number, role?: Role, name?: string, notify?: boolean) => call<User>('POST', 'users', { telegramId, role, name, notify }),
  removeUser: (id: number) => call<void>('DELETE', `users/${id}`),
  accessRequests: () => call<AccessRequest[]>('GET', 'access-requests'),
  approve: (id: number, role: Role) => call<User>('POST', `access-requests/${id}/approve`, { telegramId: id, role }),
  deny: (id: number) => call<void>('POST', `access-requests/${id}/deny`),

  firmware: () => call<Firmware[]>('GET', 'firmware'),
  uploadFirmware: (file: File, channel = 'stable') => {
    const f = new FormData();
    f.append('file', file);
    f.append('channel', channel);
    return call<Firmware>('POST', 'firmware', f);
  },
  removeFirmware: (id: number) => call<void>('DELETE', `firmware/${id}`),
  startOta: (firmwareId: number, deviceIds: string[] | null, allOfModel: boolean, canary: boolean) =>
    call<OtaJob>('POST', 'ota/jobs', { firmwareId, deviceIds, allOfModel, canary }),
  otaJobs: () => call<OtaJob[]>('GET', 'ota/jobs'),
  cancelOta: (id: number) => call<void>('POST', `ota/jobs/${id}/cancel`),
};

/** Live events (server-sent events); reconnects automatically. */
export function openStream(onEvent: (type: string, data: any) => void, logsOf?: string): () => void {
  let es: EventSource | null = null;
  let closed = false;
  const types = ['device', 'values', 'online', 'removed', 'event', 'ota', 'log'];
  const connect = () => {
    if (closed) return;
    const q = new URLSearchParams();
    if (token) q.set('access_token', token);
    if (logsOf) q.set('logs', logsOf);
    es = new EventSource(`api/v1/stream?${q}`);
    for (const t of types) es.addEventListener(t, (m) => onEvent(t, JSON.parse((m as MessageEvent).data)));
    es.onerror = () => {
      es?.close();
      if (!closed) setTimeout(connect, 3000);
    };
  };
  connect();
  return () => {
    closed = true;
    es?.close();
  };
}
