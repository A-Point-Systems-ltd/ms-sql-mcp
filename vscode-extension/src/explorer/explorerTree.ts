import * as vscode from 'vscode';
import { ConnectionStore } from '../connections/store';
import { Logger } from '../logger';
import { normalizeFilter } from '../tree/filter';
import { CATEGORIES, parseObjectList } from './catalog';
import { qualified } from './sqlText';
import type { ExplorerClient } from './explorerClient';
import {
  CLOSED_MESSAGE, CategoryNode, ConnectionNode, EMPTY_MESSAGE, ExplorerNode, ItemSpec, ObjectChildren, ObjectNode,
  categoryKey, childFolderNodes, childNodes, childrenKey, describeNode, errorNode, objectNodes, parseChildren,
  rootCategories, subCategories,
} from './treeModel';

export type { ExplorerNode } from './treeModel';

type ObjectRows = ReturnType<typeof parseObjectList>;

/**
 * Connections -> categories -> objects -> child folders -> children. Every call goes through the
 * private read-only ExplorerClient and passes `connection`. Failures become message nodes; getChildren never throws.
 */
export class ExplorerTreeProvider implements vscode.TreeDataProvider<ExplorerNode>, vscode.Disposable {
  private readonly emitter = new vscode.EventEmitter<ExplorerNode | undefined>();
  readonly onDidChangeTreeData = this.emitter.event;

  /**
   * `${connection}|${categoryId}` -> list; `${connection}|${schema}.${name}|children` -> children.
   * Promises give single-flight; a clear removes the entry, so a fetch still in flight cannot repopulate it.
   */
  private readonly lists = new Map<string, Promise<ObjectRows>>();
  private readonly children = new Map<string, Promise<ObjectChildren>>();
  /** Resolved lists, for the synchronous "n of m" in getTreeItem. */
  private readonly loaded = new WeakMap<Promise<ObjectRows>, ObjectRows>();
  /** Connection/category node instances, kept so onDidChangeTreeData(node) targets the element the view knows. */
  private readonly nodes = new Map<string, ConnectionNode | CategoryNode>();
  private term = '';
  private readonly subs: vscode.Disposable[];

  constructor(private readonly store: ConnectionStore, private readonly explorer: ExplorerClient, private readonly log: Logger) {
    // A profile change also resets the explorer process (ExplorerClient subscribes to the store itself,
    // debounced); its onDidReset refreshes again once the new process set applies.
    this.subs = [
      store.onDidChange(() => this.refresh()),
      explorer.onDidReset(() => this.refresh()),
    ];
  }

  get filter(): string {
    return this.term;
  }

  setFilter(text: string): void {
    const term = normalizeFilter(text);
    if (term === this.term) return;
    this.term = term;
    this.emitter.fire(undefined);
  }

  /** Clears the cache under `node` (everything when omitted) and re-renders it. */
  refresh(node?: ExplorerNode): void {
    if (!node) {
      this.lists.clear();
      this.children.clear();
      this.nodes.clear();
    } else if (node.kind === 'connection') {
      this.clearPrefix(`${node.profile.name}|`);
    } else if (node.kind === 'category') {
      const ids = [node.def.id, ...CATEGORIES.filter(c => c.parent === node.def.id).map(c => c.id)];
      for (const id of ids) this.lists.delete(categoryKey(node.connection, id));
      if (node.def.childFolders) this.clearPrefix(`${node.connection}|`, this.children);
    } else if (node.kind === 'object') {
      this.children.delete(childrenKey(node.ref.connection, node.ref.schema, node.ref.name));
    }
    this.emitter.fire(node);
  }

  getTreeItem(node: ExplorerNode): vscode.TreeItem {
    return toTreeItem(node, describeNode(node, this.counts(node)));
  }

  async getChildren(node?: ExplorerNode): Promise<ExplorerNode[]> {
    try {
      if (!node) return this.store.list().map(profile => this.intern({ kind: 'connection', profile }));
      switch (node.kind) {
        case 'connection':
          return node.profile.open
            ? rootCategories(node.profile.name).map(n => this.intern(n))
            : [{ kind: 'message', text: CLOSED_MESSAGE, isError: false }];
        case 'category':
          return await this.categoryChildren(node);
        case 'object':
          // Fetch once here so every folder below reads the same cached describe_* result.
          await this.objectChildren(node);
          return childFolderNodes(node);
        case 'childFolder': {
          const parent: ObjectNode = { kind: 'object', ref: node.parentRef, def: node.parentRef.scriptType === 'View' ? byId('views') : byId('tables') };
          const kids = childNodes(node.connection, node.folder, await this.objectChildren(parent));
          return kids.length ? kids : [{ kind: 'message', text: EMPTY_MESSAGE, isError: false }];
        }
        default:
          return [];
      }
    } catch (err) {
      this.log.warn('explorer', `Loading children failed: ${err instanceof Error ? err.message : String(err)}`);
      return [errorNode(err)];
    }
  }

  dispose(): void {
    for (const s of this.subs) s.dispose();
    this.emitter.dispose();
  }

  private async categoryChildren(node: CategoryNode): Promise<ExplorerNode[]> {
    if (!node.def.listType) return subCategories(node.connection, node.def).map(n => this.intern(n));
    const key = categoryKey(node.connection, node.def.id);
    const hadList = this.lists.has(key);
    const pending = this.cached(this.lists, key, async () =>
      parseObjectList(await this.explorer.call(node.connection, 'list_objects', { objectType: node.def.listType }), node.def));
    const rows = await pending;
    this.loaded.set(pending, rows);
    // The category's "n of m" is only known now: re-render it once after a fresh load.
    if (!hadList && this.term) setTimeout(() => this.emitter.fire(node), 0);
    const { nodes, total } = objectNodes(node.connection, node.def, rows, this.term);
    if (nodes.length) return nodes;
    return [{ kind: 'message', text: total ? `No matches for '${this.term}'` : EMPTY_MESSAGE, isError: false }];
  }

  private objectChildren(node: ObjectNode): Promise<ObjectChildren> {
    const { connection, schema, name } = node.ref;
    const tool = node.def.id === 'views' ? 'describe_view' : 'describe_table';
    return this.cached(this.children, childrenKey(connection, schema, name), async () =>
      parseChildren(node.def, await this.explorer.call(connection, tool, { name: qualified(schema, name) }), schema ?? 'dbo', name));
  }

  private counts(node: ExplorerNode): { shown: number; total: number } | undefined {
    if (!this.term || node.kind !== 'category' || !node.def.listType) return undefined;
    const entry = this.lists.get(categoryKey(node.connection, node.def.id));
    const rows = entry && this.loaded.get(entry);
    if (!rows) return undefined;
    return { shown: objectNodes(node.connection, node.def, rows, this.term).nodes.length, total: rows.length };
  }

  private cached<T>(map: Map<string, Promise<T>>, key: string, load: () => Promise<T>): Promise<T> {
    const existing = map.get(key);
    if (existing) return existing;
    const p = load();
    map.set(key, p);
    // Failures are not cached: the next expand (or Refresh) retries.
    p.catch(() => {
      if (map.get(key) === p) map.delete(key);
    });
    return p;
  }

  private clearPrefix(prefix: string, only?: Map<string, unknown>): void {
    for (const map of only ? [only] : [this.lists, this.children]) {
      for (const key of [...map.keys()]) if (key.startsWith(prefix)) map.delete(key);
    }
    if (!only) for (const key of [...this.nodes.keys()]) if (key.startsWith(`cat|${prefix}`)) this.nodes.delete(key);
  }

  private intern<N extends ConnectionNode | CategoryNode>(node: N): N {
    const id = describeNode(node).id!;
    const existing = this.nodes.get(id) as N | undefined;
    if (existing) {
      // Keep the instance, refresh its data (a connection's profile may have been edited).
      Object.assign(existing, node);
      return existing;
    }
    this.nodes.set(id, node);
    return node;
  }
}

const byId = (id: string) => CATEGORIES.find(c => c.id === id)!;

function toTreeItem(node: ExplorerNode, spec: ItemSpec): vscode.TreeItem {
  const item = new vscode.TreeItem(spec.label, spec.collapsible ? vscode.TreeItemCollapsibleState.Collapsed : vscode.TreeItemCollapsibleState.None);
  if (spec.id) item.id = spec.id;
  if (spec.description) item.description = spec.description;
  if (spec.tooltip) item.tooltip = spec.tooltip;
  item.contextValue = spec.contextValue;
  item.iconPath = node.kind === 'message' && node.isError
    ? new vscode.ThemeIcon(spec.icon, new vscode.ThemeColor('errorForeground'))
    : new vscode.ThemeIcon(spec.icon);
  if (spec.command) item.command = { command: spec.command, title: 'Show DDL', arguments: [node] };
  return item;
}
