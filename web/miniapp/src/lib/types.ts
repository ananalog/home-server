// Mirrors Home.Client/Contracts.cs (JSON is camelCase, enums are strings).

export type Role = 'Viewer' | 'User' | 'Admin';
export type DeviceState = 'New' | 'Adopted' | 'Blocked';
export type OtaStatus = 'Pending' | 'Running' | 'Rebooting' | 'Done' | 'Failed' | 'RolledBack' | 'Cancelled';
export type Json = boolean | number | string | null;

export interface Point {
  id: number;
  key: string;
  title: string;
  kind: 'sensor' | 'actuator' | 'setting' | 'action';
  type: 'bool' | 'i32' | 'f32' | 'str' | 'enum';
  unit?: string;
  min?: number;
  max?: number;
  step?: number;
  options: string[];
  ui: 'auto' | 'gauge' | 'switch' | 'slider' | 'button' | 'select' | 'text' | 'hidden';
  history: boolean;
  readOnly: boolean;
  advanced: boolean;
  persist: boolean;
  thrWarn?: number;
  thrAlarm?: number;
  hasArg: boolean;
  argDefault?: number;
}

export interface ValueDto {
  value: Json;
  ts: number;
}

export interface OtaProgress {
  jobId: number;
  version: string;
  status: OtaStatus;
  progress: number;
  error?: string;
}

export interface Device {
  id: string;
  modelId: number;
  model: string;
  hwRev: number;
  name: string;
  room?: string;
  state: DeviceState;
  online: boolean;
  fwVersion?: string;
  bootPartition?: string;
  pendingVerify: boolean;
  ip?: string;
  lastSeen?: string;
  firstSeen: string;
  points: Point[];
  values: Record<string, ValueDto>;
  ota?: OtaProgress;
}

export interface Room {
  id: string;
  name: string;
  sort: number;
}

export interface User {
  telegramId: number;
  name: string;
  role: Role;
  notify: boolean;
  createdAt: string;
  addedBy?: string;
}

export interface AccessRequest {
  telegramId: number;
  name: string;
  username?: string;
  requestedAt: string;
}

export interface Me {
  telegramId?: number;
  name: string;
  role: Role;
  local: boolean;
}

export interface Firmware {
  id: number;
  modelId: number;
  model: string;
  version: string;
  project: string;
  sha256: string;
  size: number;
  channel: string;
  notes?: string;
  uploadedAt: string;
}

export interface OtaItem {
  deviceId: string;
  deviceName: string;
  status: OtaStatus;
  progress: number;
  error?: string;
  fromVersion?: string;
}

export interface OtaJob {
  id: number;
  firmwareId: number;
  model: string;
  version: string;
  status: OtaStatus;
  canary: boolean;
  createdAt: string;
  createdBy?: string;
  items: OtaItem[];
}

export interface HistoryPoint {
  t: number;
  min?: number;
  max?: number;
  avg?: number;
}

export interface History {
  point: string;
  from: number;
  to: number;
  step: number;
  points: HistoryPoint[];
}

export interface EventItem {
  id: number;
  ts: string;
  deviceId?: string;
  kind: string;
  text: string;
  actor?: string;
}

export interface LogLine {
  ts: number;
  level: string;
  tag: string;
  text: string;
}

export interface NetworkCfg {
  mode: 'dhcp' | 'static';
  ip?: string;
  prefix?: number;
  gateway?: string;
  dns1?: string;
  dns2?: string;
  hostname?: string;
}

export interface NetStatus {
  state: string;
  current?: NetworkCfg;
  rssi?: number;
  ssid?: string;
  server?: string;
  mac?: string;
}

export interface SystemInfo {
  version: string;
  startedAt: string;
  devices: number;
  online: number;
  newDevices: number;
  dbSize: number;
  diskFree: number;
  publicUrl?: string;
  botConfigured: boolean;
  botUsername?: string;
  devicePort: number;
  protocolMajor: number;
  protocolMinor: number;
}
