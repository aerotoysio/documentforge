'use client';

import { useEffect, useState } from 'react';
import { getStats, API_URL } from '@/lib/api';

export default function Dashboard() {
  const [stats, setStats] = useState<any>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const load = async () => {
      try {
        setStats(await getStats());
        setError(null);
      } catch (e: any) {
        setError(e.message);
      }
    };
    load();
    const t = setInterval(load, 3000);
    return () => clearInterval(t);
  }, []);

  if (error) return (
    <>
      <div className="eyebrow">Dashboard</div>
      <h1 className="page-title">Overview</h1>
      <div className="card" style={{ borderLeft: '3px solid var(--red)' }}>
        <h3>Can&apos;t reach the database API</h3>
        <p>Tried <code style={{ background: 'var(--gray-100)', padding: '2px 6px' }}>{API_URL}</code> — {error}</p>
        <h3 style={{ marginTop: 20 }}>Start the API:</h3>
        <pre className="code-block">{`# From the repo root — dev mode
dotnet run --project src/DocumentForge.Cli -- serve --port 5000 --data-dir ./data

# Or with the published binary
./dist/win-x64/dfdb.exe serve --port 5000 --data-dir ./data
# → listens on http://localhost:5000`}</pre>
        <h3 style={{ marginTop: 20 }}>Or point this UI at a different host:</h3>
        <pre className="code-block">{`# In admin-ui/.env.local
NEXT_PUBLIC_DFDB_URL=https://dfdb.internal:5500`}</pre>
      </div>
    </>
  );

  if (!stats) return <p>Loading...</p>;

  const totalDocs = stats.collections?.reduce((sum: number, c: any) => sum + (c.documentCount || 0), 0) ?? 0;
  const totalIndexes = stats.collections?.reduce((sum: number, c: any) => sum + (c.indexes?.length || 0), 0) ?? 0;

  return (
    <>
      <div className="eyebrow">Dashboard</div>
      <h1 className="page-title">Overview</h1>

      <div className="grid grid-4">
        <div className="stat">
          <div className="label">File size</div>
          <div className="num">{stats.fileSizeMb?.toFixed?.(1) ?? 0} <span style={{ fontSize: 18, color: 'var(--gray-500)' }}>MB</span></div>
          <div className="sub">{stats.filePath}</div>
        </div>
        <div className="stat">
          <div className="label">Pages</div>
          <div className="num">{stats.pageCount?.toLocaleString() ?? 0}</div>
          <div className="sub">{stats.cachedPages?.toLocaleString() ?? 0} cached ({stats.dirtyPages ?? 0} dirty)</div>
        </div>
        <div className="stat">
          <div className="label">Documents</div>
          <div className="num">{totalDocs.toLocaleString()}</div>
          <div className="sub">across {stats.collections?.length ?? 0} collection(s)</div>
        </div>
        <div className="stat">
          <div className="label">Indexes</div>
          <div className="num">{totalIndexes}</div>
          <div className="sub">total across all collections</div>
        </div>
      </div>

      <h2>Collections</h2>
      {stats.collections?.length === 0 ? (
        <p style={{ color: 'var(--gray-500)' }}>No collections yet. Run an INSERT from the query console to create one.</p>
      ) : (
        <table className="table">
          <thead>
            <tr><th>Name</th><th>Documents</th><th>Indexes</th></tr>
          </thead>
          <tbody>
            {stats.collections?.map((c: any) => (
              <tr key={c.name}>
                <td><a href={`/collections?name=${c.name}`}>{c.name}</a></td>
                <td>{c.documentCount?.toLocaleString()}</td>
                <td>
                  {c.indexes?.length === 0 ? <span className="pill gray">none</span> : (
                    c.indexes?.map((i: any) => (
                      <span key={i.name} className="pill" style={{ marginRight: 6 }}>
                        {i.name} {i.unique && <span style={{ color: 'var(--red)' }}>·unique</span>}
                      </span>
                    ))
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      <h2>Live at</h2>
      <div className="setting-row"><span className="key">API endpoint</span><span className="val">{API_URL}</span></div>
      <div className="setting-row"><span className="key">Auto-refresh</span><span className="val">every 3 seconds</span></div>
    </>
  );
}
