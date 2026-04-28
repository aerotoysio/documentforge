'use client';

import { useEffect, useState } from 'react';
import { findById, replaceById, deleteById } from '@/lib/api';
import type { TabContext } from '../studio-types';

export function InspectorTab({ tab, ctx }: { tab: any; ctx: TabContext }) {
  const collection: string = tab.collection;
  const docId: string = tab.docId;

  const [draft, setDraft] = useState<string>(tab.initialJson ?? '');
  const [original, setOriginal] = useState<string>(tab.initialJson ?? '');
  const [msg, setMsg] = useState<{ kind: 'ok' | 'err'; text: string } | null>(null);
  const [loading, setLoading] = useState(!tab.initialJson);

  useEffect(() => {
    if (tab.initialJson) return;
    let cancelled = false;
    setLoading(true);
    findById(collection, docId)
      .then(d => {
        if (cancelled) return;
        const txt = JSON.stringify(d, null, 2);
        setDraft(txt);
        setOriginal(txt);
      })
      .catch(e => { if (!cancelled) setMsg({ kind: 'err', text: e.message || String(e) }); })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [collection, docId, tab.initialJson]);

  const dirty = draft !== original;

  async function save() {
    setMsg(null);
    let parsed: any;
    try { parsed = JSON.parse(draft); }
    catch (e: any) { setMsg({ kind: 'err', text: 'Invalid JSON: ' + e.message }); return; }
    try {
      await replaceById(collection, docId, parsed);
      setOriginal(draft);
      setMsg({ kind: 'ok', text: 'Saved.' });
      ctx.notifyChanged(collection);
    } catch (e: any) {
      setMsg({ kind: 'err', text: e.message || String(e) });
    }
  }

  async function remove() {
    if (!confirm(`Delete document ${docId} from "${collection}"?\nThis cannot be undone.`)) return;
    try {
      await deleteById(collection, docId);
      setMsg({ kind: 'ok', text: 'Deleted.' });
      ctx.notifyChanged(collection);
      ctx.closeTab(tab.id);
    } catch (e: any) {
      setMsg({ kind: 'err', text: e.message || String(e) });
    }
  }

  function reset() {
    setDraft(original);
    setMsg(null);
  }

  return (
    <div className="inspector-tab">
      <div className="inspector-meta">
        <span><strong>Collection</strong> {collection}</span>
        <span><strong>_id</strong> <span className="inspector-id">{docId}</span></span>
        {dirty && <span style={{ color: 'var(--red)', fontFamily: 'var(--mono)', fontSize: 11 }}>● modified</span>}
      </div>
      {loading ? (
        <div style={{ color: 'var(--gray-500)' }}>Loading document…</div>
      ) : (
        <textarea
          className="json-editor"
          value={draft}
          onChange={e => setDraft(e.target.value)}
          spellCheck={false}
        />
      )}
      <div className="inspector-actions">
        <button className="save" onClick={save} disabled={!dirty || loading}>Save (PUT)</button>
        <button onClick={reset} disabled={!dirty}>Reset</button>
        <button className="delete" onClick={remove} disabled={loading}>Delete</button>
        {msg && <span className={`msg ${msg.kind}`}>{msg.text}</span>}
      </div>
    </div>
  );
}
