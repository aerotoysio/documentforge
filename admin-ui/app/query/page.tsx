'use client';

import { useState } from 'react';
import { query } from '@/lib/api';

const EXAMPLES = [
  "SELECT * FROM orders WHERE pnr = 'ABC123'",
  "SELECT * FROM orders WHERE passenger.lastName = 'Smith' LIMIT 10",
  "SELECT status, COUNT(*) FROM orders GROUP BY status",
  "SELECT * FROM orders WHERE tickets[0].fare.total > 500 ORDER BY tickets[0].fare.total DESC LIMIT 20",
  "INSERT INTO users VALUES { \"name\": \"Alice\", \"age\": 30 }",
  "CREATE UNIQUE INDEX idx_pnr ON orders (pnr)",
  "CREATE INDEX idx_status_created ON orders (status, createdAt)"
];

export default function QueryPage() {
  const [sql, setSql] = useState(EXAMPLES[0]);
  const [result, setResult] = useState<any>(null);
  const [running, setRunning] = useState(false);

  async function run() {
    setRunning(true);
    try { setResult(await query(sql)); }
    catch (e: any) { setResult({ error: e.message }); }
    finally { setRunning(false); }
  }

  return (
    <>
      <div className="eyebrow">Query Console</div>
      <h1 className="page-title">Run SQL</h1>

      <div className="card">
        <h3>SQL</h3>
        <textarea
          value={sql}
          onChange={(e) => setSql(e.target.value)}
          rows={5}
          onKeyDown={(e) => { if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') run(); }}
        />
        <div className="toolbar" style={{ marginTop: 12 }}>
          <button className="btn" onClick={run} disabled={running}>
            {running ? 'Running...' : 'Execute'} <span style={{ fontSize: 11, fontFamily: 'var(--mono)', opacity: 0.7 }}>Ctrl+Enter</span>
          </button>
          <span style={{ color: 'var(--gray-500)', fontSize: 13 }}>
            {result?.plan && (
              <>Plan: <code style={{ background: 'var(--gray-100)', padding: '2px 6px' }}>{result.plan}</code></>
            )}
          </span>
          <div className="spacer" />
          {result?.executionTimeMs != null && (
            <span className="pill gray">{result.executionTimeMs.toFixed(2)} ms</span>
          )}
          {result?.count != null && (
            <span className="pill green">{result.count} row(s)</span>
          )}
        </div>
      </div>

      <h2>Results</h2>
      {result?.error && (
        <div className="card" style={{ borderLeft: '3px solid var(--red)' }}>
          <h3 style={{ color: 'var(--red)' }}>Error</h3>
          <pre className="code-block">{result.error}</pre>
        </div>
      )}
      {result?.documents?.length > 0 && (
        <pre className="code-block">{JSON.stringify(result.documents, null, 2)}</pre>
      )}
      {result?.message && !result.documents?.length && (
        <div className="card"><p>{result.message}</p></div>
      )}

      <h2>Examples</h2>
      <div className="card">
        {EXAMPLES.map((ex, i) => (
          <div
            key={i}
            onClick={() => setSql(ex)}
            style={{
              fontFamily: 'var(--mono)', fontSize: 13, padding: '8px 10px',
              cursor: 'pointer', borderBottom: i < EXAMPLES.length - 1 ? '1px solid var(--gray-200)' : 'none',
              color: 'var(--gray-900)'
            }}
            onMouseEnter={(e) => (e.currentTarget.style.background = 'var(--gray-100)')}
            onMouseLeave={(e) => (e.currentTarget.style.background = 'transparent')}
          >
            {ex}
          </div>
        ))}
      </div>
    </>
  );
}
