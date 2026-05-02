/** @type {import('next').NextConfig} */
const nextConfig = {
  reactStrictMode: true,
  // Produce a self-contained Node server in .next/standalone — required for
  // the lean Docker runtime stage (~200 MB vs ~1.5 GB of node_modules).
  output: 'standalone',
};

export default nextConfig;
