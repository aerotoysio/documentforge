'use client';

import { ConnectionsProvider } from '@/lib/connections-context';

/**
 * Client wrapper for global providers. Lives in its own file so
 * the root layout.tsx can stay a server component.
 */
export function ClientProviders({ children }: { children: React.ReactNode }) {
  return <ConnectionsProvider>{children}</ConnectionsProvider>;
}
