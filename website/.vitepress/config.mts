import { defineConfig } from 'vitepress'

const repo = 'https://github.com/justcoding121/titanium-web-proxy'

export default defineConfig({
  title: 'Titanium Web Proxy',
  description:
    'High-performance HTTP(S) proxy for .NET — MITM library, reverse/edge CLI, Plus ops plugin, and Inspector.',
  lang: 'en-US',
  // Served at https://titaniumproxy.com via CloudFront (origin path maps GitHub Pages project URL).
  base: '/',
  cleanUrls: true,
  lastUpdated: true,
  ignoreDeadLinks: true,
  head: [
    ['link', { rel: 'icon', href: '/logo.svg', type: 'image/svg+xml' }],
    ['meta', { name: 'theme-color', content: '#2B3A4A' }],
  ],
  themeConfig: {
    logo: '/logo.svg',
    siteTitle: 'Titanium Web Proxy',
    nav: [
      { text: 'Docs', link: '/docs/getting-started' },
      { text: 'Download', link: '/download' },
      { text: 'Releases', link: '/releases' },
      { text: 'API', link: '/api/Titanium.Web.Proxy.ProxyServer.html', target: '_blank' },
      { text: 'GitHub', link: repo },
    ],
    sidebar: {
      '/docs/': [
        {
          text: 'Start here',
          items: [
            { text: 'Getting started', link: '/docs/getting-started' },
            { text: 'Install', link: '/docs/install' },
            { text: 'Editions & licenses', link: '/docs/editions' },
          ],
        },
        {
          text: 'Products',
          items: [
            { text: 'CLI (`titanium` / `twp`)', link: '/docs/cli' },
            { text: 'Configuration (`twp.yaml`)', link: '/docs/configuration' },
            { text: 'Library (embed)', link: '/docs/library' },
            { text: 'Plus', link: '/docs/plus' },
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
            { text: 'Security', link: '/docs/security' },
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
      message: 'Core & CLI: MIT · Plus & Inspector: PolyForm Noncommercial',
      copyright: 'Copyright © 2015–present Jehonathan Thomas',
    },
  },
})
