import './globals.css';
import type { Metadata } from 'next';
import { ClientProviders } from './client-providers';
import { Sidebar } from './Sidebar';

export const metadata: Metadata = {
  title: 'DocumentForge Admin',
  description: 'Multi-connection management UI for DocumentForge',
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
        <ClientProviders>
          <div className="layout">
            <Sidebar />
            <main>{children}</main>
          </div>
        </ClientProviders>
      </body>
    </html>
  );
}
