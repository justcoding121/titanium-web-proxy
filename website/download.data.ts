/// <reference types="vitepress/client" />
import { defineLoader } from 'vitepress'

export type CliRid =
  | 'win-x64'
  | 'linux-x64'
  | 'linux-arm64'
  | 'linux-musl-x64'
  | 'linux-musl-arm64'
  | 'osx-x64'
  | 'osx-arm64'

export interface DownloadAsset {
  name: string
  url: string
  tag: string
}

export interface DownloadLinks {
  cli: Partial<Record<CliRid, DownloadAsset>>
  inspector: {
    msi?: DownloadAsset
    zip?: DownloadAsset
  } & Partial<Record<CliRid, DownloadAsset>>
  releasesUrl: string
  latestTag: string | null
}

declare const data: DownloadLinks
export { data }

const RELEASES = 'https://github.com/justcoding121/titanium-web-proxy/releases'

const CLI_RIDS: CliRid[] = [
  'win-x64',
  'linux-x64',
  'linux-arm64',
  'linux-musl-x64',
  'linux-musl-arm64',
  'osx-x64',
  'osx-arm64',
]

export default defineLoader({
  async load(): Promise<DownloadLinks> {
    const empty: DownloadLinks = {
      cli: {},
      inspector: {},
      releasesUrl: RELEASES,
      latestTag: null,
    }

    const headers: Record<string, string> = {
      Accept: 'application/vnd.github+json',
      'User-Agent': 'titaniumproxy-website',
      'X-GitHub-Api-Version': '2022-11-28',
    }
    const token = process.env.GITHUB_TOKEN
    if (token) headers.Authorization = `Bearer ${token}`

    try {
      const res = await fetch(
        'https://api.github.com/repos/justcoding121/titanium-web-proxy/releases?per_page=40',
        { headers },
      )
      if (!res.ok) return empty
      const releases = (await res.json()) as Array<{
        tag_name: string
        prerelease: boolean
        assets: Array<{ name: string; browser_download_url: string }>
      }>

      const out: DownloadLinks = {
        cli: {},
        inspector: {},
        releasesUrl: RELEASES,
        latestTag: releases.find((r) => !r.prerelease)?.tag_name ?? null,
      }

      for (const r of releases) {
        for (const a of r.assets ?? []) {
          const asset = { name: a.name, url: a.browser_download_url, tag: r.tag_name }
          for (const rid of CLI_RIDS) {
            if (a.name === `Titanium.Cli-${rid}.zip` && !out.cli[rid]) out.cli[rid] = asset
            if (a.name === `TitaniumInspector-${rid}.zip` && !out.inspector[rid]) out.inspector[rid] = asset
          }
          if (a.name === 'TitaniumInspector-win-x64.msi' && !out.inspector.msi) out.inspector.msi = asset
          if (a.name === 'TitaniumInspector-win-x64.zip' && !out.inspector.zip) out.inspector.zip = asset
        }
      }
      return out
    } catch {
      return empty
    }
  },
})
