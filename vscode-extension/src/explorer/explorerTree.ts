import * as vscode from 'vscode';
import { ConnectionStore } from '../connections/store';
import { Logger } from '../logger';
import { findProfile } from '../query/editorState';
import { CategoryFilters } from '../tree/categoryFilters';
import { CATEGORIES, parseObjectList } from './catalog';
import { qualified } from './sqlText';
import type { ExplorerClient } from './explorerClient';
import {
  CLOSED_MESSAGE, CategoryNode, ConnectionNode, EMPTY_MESSAGE, ExplorerNode, ItemSpec, ObjectChildren, ObjectNode,
  categoryKey, childFolderNodes, childNodes, childrenKey, connectionPrefix, describeNode, errorNode, objectNodes, parseChildren,
  rootCategories, subCategories,
} from './treeModel';

export type { ExplorerNode } from './treeModel';

type ObjectRows = ReturnType<typeof parseObjectList>;

/**
 * Connections -> categories -> objects -> child folders -> children. Every call goes through the
 * private read-only ExplorerClient and passes `connection`. Failures become message nodes; getChildren never throws.
 */
/** globalState key of the per-group name filters. */
const FILTERS_KEY = 'msSqlMcp.categoryFilters';

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
  /** Name filters per (connection, group), kept in globalState across restarts. */
  private readonly filters: CategoryFilters;
  private readonly subs: vscode.Disposable[];

  constructor(
    private readonly store: ConnectionStore, private readonly explorer: ExplorerClient, private readonly log: Logger,
    private readonly memento?: vscode.Memento,
  ) {
    this.filters = new CategoryFilters(memento?.get(FILTERS_KEY));
    // No explorer.reset() here: ExplorerClient resets itself on store change (debounced), and the tree
    // refreshes again on its onDidReset once the new profile set applies.
    this.subs = [
      store.onDidChange(() => this.refresh()),
      explorer.onDidReset(() => this.refresh()),
    ];
  }

  /** The name filter of one group of one connection ('' when none). */
  filterOf(connection: string, category: string): string {
    return this.filters.get(connection, category);
  }

  /** How many groups are filtered (all connections). */
  get filterCount(): number {
    return this.filters.count;
  }

  /** Sets (empty: clears) one group's filter and re-renders the tree. */
  setFilter(connection: string, category: string, text: string): void {
    if (this.filters.set(connection, category, text)) this.filtersChanged();
  }

  clearAllFilters(): void {
    if (this.filters.clearAll()) this.filtersChanged();
  }

  private filtersChanged(): void {
    void this.memento?.update(FILTERS_KEY, this.filters.toState());
    this.emitter.fire(undefined);
  }

  /** Clears the cache under `node` (everything when omitted) and re-renders it. */
  refresh(node?: ExplorerNode): void {
    if (!node) {
      this.lists.clear();
      this.children.clear();
      this.nodes.clear();
    } else if (node.kind === 'connection') {
      this.clearPrefix(node.profile.name);
    } else if (node.kind === 'category') {
      const ids = [node.def.id, ...CATEGORIES.filter(c => c.parent === node.def.id).map(c => c.id)];
      for (const id of ids) this.lists.delete(categoryKey(node.connection, id));
      if (node.def.childFolders) this.clearPrefix(node.connection, this.children);
    } else if (node.kind === 'object') {
      this.children.delete(childrenKey(node.ref.connection, node.ref.schema, node.ref.name));
    }
    this.emitter.fire(node);
  }

  getTreeItem(node: ExplorerNode): vscode.TreeItem {
    const connection = node.kind === 'object' || node.kind === 'child' ? node.ref.connection : undefined;
    const history = connection !== undefined && findProfile(this.store.list(), connection)?.ddlHistory === true;
    const filter = node.kind === 'category' && node.def.listType ? this.filters.get(node.connection, node.def.id) : undefined;
    return toTreeItem(node, describeNode(node, this.counts(node), { history, filter }));
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
    const term = this.filters.get(node.connection, node.def.id);
    if (!hadList && term) setTimeout(() => this.emitter.fire(node), 0);
    const { nodes, total } = objectNodes(node.connection, node.def, rows, term);
    if (nodes.length) return nodes;
    return [{ kind: 'message', text: total ? `No matches for '${term}'` : EMPTY_MESSAGE, isError: false }];
  }

  private objectChildren(node: ObjectNode): Promise<ObjectChildren> {
    const { connection, schema, name } = node.ref;
    const tool = node.def.id === 'views' ? 'describe_view' : 'describe_table';
    return this.cached(this.children, childrenKey(connection, schema, name), async () =>
      parseChildren(node.def, await this.explorer.call(connection, tool, { name: qualified(schema, name) }), schema ?? 'dbo', name));
  }

  private counts(node: ExplorerNode): { shown: number; total: number } | undefined {
    if (node.kind !== 'category' || !node.def.listType) return undefined;
    const term = this.filters.get(node.connection, node.def.id);
    if (!term) return undefined;
    const entry = this.lists.get(categoryKey(node.connection, node.def.id));
    const rows = entry && this.loaded.get(entry);
    if (!rows) return undefined;
    return { shown: objectNodes(node.connection, node.def, rows, term).nodes.length, total: rows.length };
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

  private clearPrefix(connection: string, only?: Map<string, unknown>): void {
    const prefix = connectionPrefix(connection);
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
  if (spec.resourceUri) item.resourceUri = vscode.Uri.parse(spec.resourceUri);
  const iconColor = node.kind === 'message' && node.isError ? 'errorForeground' : spec.iconColor;
  item.iconPath = iconColor ? new vscode.ThemeIcon(spec.icon, new vscode.ThemeColor(iconColor)) : new vscode.ThemeIcon(spec.icon);
  if (spec.command) item.command = { command: spec.command, title: 'Show DDL', arguments: [node] };
  return item;
}
