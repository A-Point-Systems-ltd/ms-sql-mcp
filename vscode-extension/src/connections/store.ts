import * as vscode from 'vscode';
import { ConnectionProfile, validateProfile } from './profile';

const KEY = 'msSqlMcp.connections';
const same = (a: string, b: string) => a.toLowerCase() === b.toLowerCase();
const secretKey = (name: string) => `msSqlMcp.password.${name}`;

export class ConnectionStore {
  private readonly emitter = new vscode.EventEmitter<void>();
  readonly onDidChange = this.emitter.event;

  constructor(private readonly state: vscode.Memento, private readonly secrets: vscode.SecretStorage) {}

  list(): ConnectionProfile[] {
    return this.state.get<ConnectionProfile[]>(KEY, []);
  }

  async upsert(p: ConnectionProfile, password?: string): Promise<void> {
    const errors = validateProfile(p);
    if (errors.length) throw new Error(errors.join(' '));
    const others = this.list().filter(x => !same(x.name, p.name));
    await this.state.update(KEY, [...others, p].sort((a, b) => a.name.localeCompare(b.name)));
    if (p.auth !== 'sql') await this.secrets.delete(secretKey(p.name));
    else if (password !== undefined) await this.secrets.store(secretKey(p.name), password);
    this.emitter.fire();
  }

  async remove(name: string): Promise<void> {
    const target = this.list().find(x => same(x.name, name));
    await this.state.update(KEY, this.list().filter(x => !same(x.name, name)));
    await this.secrets.delete(secretKey(target?.name ?? name));
    this.emitter.fire();
  }

  async setOpen(name: string, open: boolean): Promise<void> {
    await this.state.update(KEY, this.list().map(x => (same(x.name, name) ? { ...x, open } : x)));
    this.emitter.fire();
  }

  async passwords(): Promise<Map<string, string>> {
    const map = new Map<string, string>();
    for (const p of this.list().filter(x => x.auth === 'sql')) {
      const pwd = await this.secrets.get(secretKey(p.name));
      if (pwd !== undefined) map.set(p.name, pwd);
    }
    return map;
  }
}
