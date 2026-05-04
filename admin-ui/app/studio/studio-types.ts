// Shared types for studio components.

export type TabKind = 'query' | 'browse' | 'inspector' | 'indexes';

export interface BaseTab {
  id: string;             // unique within the workspace
  kind: TabKind;
  title: string;
  refreshKey?: number;    // bump to force a child re-fetch
  connectionId?: string;  // pinned at tab creation; switching the global active
                          // connection no longer yanks open tabs out from under
                          // the user. Undefined = follow the global active.
}

export interface QueryTabState extends BaseTab {
  kind: 'query';
  initialSql?: string;
}

export interface BrowseTabState extends BaseTab {
  kind: 'browse';
  collection: string;
}

export interface InspectorTabState extends BaseTab {
  kind: 'inspector';
  collection: string;
  docId: string;
  initialJson?: string;
}

export interface IndexesTabState extends BaseTab {
  kind: 'indexes';
  collection: string;
}

export type Tab = QueryTabState | BrowseTabState | InspectorTabState | IndexesTabState;

export interface StatusInfo {
  endpoint?: string;
  node?: string;
  role?: string;
  readOnly?: boolean;
  plan?: string;
  executionMs?: number;
  rows?: number;
  affected?: number;
}

export interface TabContext {
  setStatus: (s: Partial<StatusInfo>) => void;
  openInspector: (collection: string, doc: any) => void;
  closeTab: (tabId: string) => void;
  refreshTab: (tabId: string) => void;
  setTabConnection: (tabId: string, connectionId: string) => void;
  notifyChanged: (collection: string) => void;
}
