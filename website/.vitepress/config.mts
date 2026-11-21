import { defineConfig } from 'vitepress'

const repo = 'https://github.com/justcoding121/titanium-web-proxy'
const siteUrl = 'https://titaniumproxy.com'
const ogImage = `${siteUrl}/og.jpg`
const heroDescription = 'A high performing HTTP(S) proxy server.'

function canonicalUrl(page: string): string {
  const stem = page.replace(/\.md$/, '').replace(/\/index$/, '')
  if (stem === 'index') return `${siteUrl}/`
  if (stem === 'download' || stem === 'releases') return `${siteUrl}/${stem}/`
  return `${siteUrl}/${stem}`
}

export default defineConfig({
  title: 'Titanium Web Proxy',
  description:
    'High-performance HTTP(S) proxy — reverse/edge CLI, Plus ops, and Inspector on Windows, Linux, and macOS. Optional .NET library via NuGet.',
  lang: 'en-US',
  // CloudFront serves titaniumproxy.com from the GitHub Pages project origin with
  // path mapping, so asset URLs must be site-root absolute (`/assets/...`), not
  // `/titanium-web-proxy/assets/...`. The github.io project URL is secondary.
  base: '/',
  cleanUrls: true,
  lastUpdated: true,
  ignoreDeadLinks: true,
  // Shared screenshots live under wiki/images (README + wiki + site); allow Vite to resolve them.
  vite: {
    server: {
      fs: {
        allow: ['..'],
      },
    },
  },
  head: [
    ['link', { rel: 'icon', href: '/logo.svg', type: 'image/svg+xml' }],
    ['meta', { name: 'theme-color', content: '#2B3A4A' }],
    ['meta', { property: 'og:image', content: ogImage }],
    ['meta', { name: 'twitter:card', content: 'summary_large_image' }],
    ['meta', { name: 'twitter:image', content: ogImage }],
  ],
  transformHead({ page, title, description }) {
    const url = canonicalUrl(page)
    const summary = page === 'index.md' ? heroDescription : description
    return [
      ['meta', { property: 'og:title', content: title }],
      ['meta', { property: 'og:description', content: summary }],
      ['meta', { property: 'og:url', content: url }],
      ['meta', { property: 'og:type', content: 'website' }],
      ['meta', { name: 'twitter:title', content: title }],
      ['meta', { name: 'twitter:description', content: summary }],
      ['link', { rel: 'canonical', href: url }],
    ]
  },
  themeConfig: {
    logo: '/logo.svg',
    siteTitle: 'Titanium Web Proxy',
    nav: [
      { text: 'Docs', link: '/docs/getting-started' },
      { text: 'Licensing', link: '/#licensing' },
      // Trailing slash: with only dir/index.html, GitHub Pages 301s `/download` →
      // github.io when the Pages custom-domain CNAME is not active on the edge.
      { text: 'Download', link: '/download/' },
      { text: 'Releases', link: '/releases/' },
      { text: 'API', link: '/api/Titanium.Web.Proxy.ProxyServer.html', target: '_blank' },
      { text: 'GitHub', link: repo },
    ],
    sidebar: {
      '/docs/': [
        {
          text: 'Start here',
          items: [
            { text: 'Getting started', link: '/docs/getting-started' },
            { text: 'Features', link: '/docs/features' },
            { text: 'Capability coverage', link: '/docs/capabilities' },
            { text: 'Install', link: '/docs/install' },
            { text: 'Editions & licenses', link: '/docs/editions' },
            { text: 'Migrating from 7.x', link: '/docs/migration-7-to-8' },
          ],
        },
        {
          text: 'Products',
          items: [
            { text: 'CLI (`titanium` / `twp`)', link: '/docs/cli' },
            { text: 'Configuration (`twp.yaml`)', link: '/docs/configuration' },
            { text: 'Library (embed)', link: '/docs/library' },
            { text: 'Plus', link: '/docs/plus' },
            { text: 'Manager', link: '/docs/manager' },
            { text: 'xDS (ADS client)', link: '/docs/xds' },
            { text: 'Inspector', link: '/docs/inspector' },
          ],
        },
        {
          text: 'Guides',
          items: [
            { text: 'Performance', link: '/docs/performance' },
            { text: 'Protocol support', link: '/docs/protocol-support' },
            { text: 'HTTP/3', link: '/docs/http3' },
            { text: 'Streaming bodies', link: '/docs/streaming-bodies' },
            { text: 'gRPC-JSON transcoding', link: '/docs/grpc-json-transcoding' },
            { text: 'Per-route plugins', link: '/docs/plugins' },
            { text: 'Scripting', link: '/docs/scripting' },
            { text: 'Kubernetes', link: '/docs/kubernetes' },
            { text: 'Gateway API', link: '/docs/gateway-api' },
            { text: 'Ingress', link: '/docs/ingress' },
            { text: 'Security', link: '/docs/security' },
            { text: 'Limits and escape hatches', link: '/docs/limits' },
            { text: 'Why did my request fail', link: '/docs/troubleshooting' },
          ],
        },
      ],
    },
    socialLinks: [{ icon: 'github', link: repo }],
    search: { provider: 'local' },
    editLink: {
      pattern: `${repo}/edit/develop/website/:path`,
      text: 'Edit this page on GitHub',
    },
    footer: {
      message:
        'Core, CLI, and website: MIT · <a href="/#licensing">Licensing</a>',
      copyright: 'Copyright © 2015–present Jehonathan Thomas',
    },
  },
})
