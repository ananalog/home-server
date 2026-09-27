import type { Device, Json, Point } from './types';

export function fmtValue(p: Point, v: Json | undefined): string {
  if (v === undefined || v === null) return '—';
  if (p.type === 'enum' && typeof v === 'number') return label(p.options[v] ?? String(v));
  if (p.type === 'bool' && typeof v === 'boolean') return v ? 'вкл' : 'выкл';
  if (typeof v === 'number') return Number.isInteger(v) ? String(v) : v.toFixed(Math.abs(v) >= 100 ? 0 : 1);
  return label(String(v));
}

// Human labels for common enum values reported by devices.
const labels: Record<string, string> = {
  preheat: 'прогрев',
  ok: 'норма',
  sensor_error: 'ошибка датчика',
  co2: 'CO2',
  co2_graph: 'CO2 + график',
  night: 'ночь',
  off: 'выкл',
};

export const label = (s: string) => labels[s] ?? s;

/** 0 — normal, 1 — warning, 2 — alarm (by the point thresholds). */
export function level(p: Point, v: Json | undefined): 0 | 1 | 2 {
  if (typeof v !== 'number') return 0;
  if (p.thrAlarm !== undefined && p.thrAlarm !== null && v >= p.thrAlarm) return 2;
  if (p.thrWarn !== undefined && p.thrWarn !== null && v >= p.thrWarn) return 1;
  return 0;
}

/** The point shown big on a tile. */
export function mainPoint(d: Device): Point | undefined {
  const visible = d.points.filter((p) => !p.advanced && p.ui !== 'hidden');
  return (
    visible.find((p) => p.kind === 'actuator' && p.type === 'bool') ??
    visible.find((p) => p.kind === 'sensor' && p.ui === 'gauge') ??
    visible.find((p) => p.kind === 'actuator') ??
    visible.find((p) => p.kind === 'sensor' && p.type !== 'str')
  );
}

export function ago(iso?: string | number): string {
  if (!iso) return '';
  const t = typeof iso === 'number' ? iso : Date.parse(iso);
  const s = Math.round((Date.now() - t) / 1000);
  if (s < 60) return 'только что';
  if (s < 3600) return `${Math.round(s / 60)} мин назад`;
  if (s < 86400) return `${Math.round(s / 3600)} ч назад`;
  return new Date(t).toLocaleDateString('ru-RU');
}

export function bytes(n: number): string {
  if (n >= 1 << 30) return `${(n / (1 << 30)).toFixed(1)} ГБ`;
  if (n >= 1 << 20) return `${(n / (1 << 20)).toFixed(1)} МБ`;
  if (n >= 1 << 10) return `${(n / 1024).toFixed(0)} КБ`;
  return `${n} Б`;
}

export const roleName = (r: string) => ({ Admin: 'админ', User: 'пользователь', Viewer: 'наблюдатель' })[r] ?? r;

export const otaName = (s: string) =>
  ({ Pending: 'в очереди', Running: 'загрузка', Rebooting: 'перезагрузка', Done: 'готово', Failed: 'ошибка', RolledBack: 'откат', Cancelled: 'отменено' })[s] ?? s;
