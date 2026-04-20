import './globals.css';
import Link from 'next/link';
import type { Metadata } from 'next';

export const metadata: Metadata = {
  title: 'DocumentForge Admin',
  description: 'Cluster management for DocumentForge',
};

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="en">
      <head>
        <link rel="preconnect" href="https://fonts.googleapis.com" />
        <link rel="preconnect" href="https://fonts.gstatic.com" crossOrigin="" />
        <link
          href="https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600;700;800&family=JetBrains+Mono:wght@400;500;600&display=swap"
          rel="stylesheet"
        />
      </head>
      <body>
        <div className="layout">
          <aside className="sidebar">
            <div className="brand">
              <span className="brand-dot" />
              DocumentForge
            </div>
            <nav>
              <Link href="/">Dashboard</Link>
              <Link href="/query">Query console</Link>
              <Link href="/collections">Collections</Link>
              <Link href="/cluster">Cluster</Link>
              <Link href="/rebalance">Rebalance</Link>
              <Link href="/settings">Settings</Link>
            </nav>
            <div style={{ marginTop: 'auto', fontSize: 11, color: '#666', fontFamily: 'var(--mono)', letterSpacing: '0.05em', paddingTop: 40 }}>
              v0.1.0 · MIT
            </div>
          </aside>
          <main>{children}</main>
        </div>
      </body>
    </html>
  );
}
