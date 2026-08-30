/// <reference types="vitepress/client" />
import { defineLoader } from 'vitepress'
import MarkdownIt from 'markdown-it'

const md = new MarkdownIt({
  html: false,
  linkify: true,
  typographer: false,
})

// Open release-note links in a new tab.
const defaultLinkOpen =
  md.renderer.rules.link_open ??
  ((tokens, idx, options, _env, self) => self.renderToken(tokens, idx, options))
md.renderer.rules.link_open = (tokens, idx, options, env, self) => {
  tokens[idx].attrSet('target', '_blank')
  tokens[idx].attrSet('rel', 'noreferrer')
  return defaultLinkOpen(tokens, idx, options, env, self)
}

export interface ReleaseAsset {
  id: number
  name: string
  browser_download_url: string
}

export interface ReleaseItem {
  id: number
  tag_name: string
  html_url: string
  published_at: string | null
  prerelease: boolean
  body: string | null
  body_html: string | null
  assets: ReleaseAsset[]
}

declare const data: ReleaseItem[]
export { data }

export default defineLoader({
  async load(): Promise<ReleaseItem[]> {
    const headers: Record<string, string> = {
      Accept: 'application/vnd.github+json',
      'User-Agent': 'titaniumproxy-website',
      'X-GitHub-Api-Version': '2022-11-28',
    }
    const token = process.env.GITHUB_TOKEN
    if (token) {
      headers.Authorization = `Bearer ${token}`
    }

    try {
      const res = await fetch(
        'https://api.github.com/repos/justcoding121/titanium-web-proxy/releases?per_page=15',
        { headers },
      )
      if (!res.ok) {
        console.warn(`[releases.data] GitHub API ${res.status}`)
        return []
      }
      const raw = (await res.json()) as Array<{
        id: number
        tag_name: string
        html_url: string
        published_at: string | null
        prerelease: boolean
        body: string | null
        assets: Array<{ id: number; name: string; browser_download_url: string }>
      }>
      return raw.map((r) => ({
        id: r.id,
        tag_name: r.tag_name,
        html_url: r.html_url,
        published_at: r.published_at,
        prerelease: r.prerelease,
        body: r.body,
        body_html: r.body ? md.render(r.body) : null,
        assets: (r.assets ?? []).map((a) => ({
          id: a.id,
          name: a.name,
          browser_download_url: a.browser_download_url,
        })),
      }))
    } catch (err) {
      console.warn('[releases.data] fetch failed', err)
      return []
    }
  },
})
