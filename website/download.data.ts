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

type GhRelease = {
  tag_name: string
  prerelease: boolean
  assets: Array<{ name: string; browser_download_url: string }>
}

function githubHeaders(): Record<string, string> {
  const headers: Record<string, string> = {
    Accept: 'application/vnd.github+json',
    'User-Agent': 'titaniumproxy-website',
    'X-GitHub-Api-Version': '2022-11-28',
  }
  const token = process.env.GITHUB_TOKEN
  if (token) headers.Authorization = `Bearer ${token}`
  return headers
}

function assignAsset(out: DownloadLinks, asset: DownloadAsset, name: string): void {
  for (const rid of CLI_RIDS) {
    if (name === `Titanium.Cli-${rid}.zip` && !out.cli[rid]) out.cli[rid] = asset
    if (name === `TitaniumInspector-${rid}.zip` && !out.inspector[rid]) out.inspector[rid] = asset
  }
  if (name === 'TitaniumInspector-win-x64.msi' && !out.inspector.msi) out.inspector.msi = asset
  if (name === 'TitaniumInspector-win-x64.zip' && !out.inspector.zip) out.inspector.zip = asset
}

function mapReleases(releases: GhRelease[]): DownloadLinks {
  const out: DownloadLinks = {
    cli: {},
    inspector: {},
    releasesUrl: RELEASES,
    latestTag: releases.find((r) => !r.prerelease)?.tag_name ?? null,
  }

  for (const r of releases) {
    for (const a of r.assets ?? []) {
      assignAsset(out, { name: a.name, url: a.browser_download_url, tag: r.tag_name }, a.name)
    }
  }
  return out
}

export default defineLoader({
  async load(): Promise<DownloadLinks> {
    const url =
      'https://api.github.com/repos/justcoding121/titanium-web-proxy/releases?per_page=40'
    try {
      const res = await fetch(url, { headers: githubHeaders() })
      if (!res.ok) {
        throw new Error(`GitHub releases API ${res.status} ${res.statusText}`)
      }
      const links = mapReleases((await res.json()) as GhRelease[])
      const cliCount = Object.keys(links.cli).length
      if (cliCount === 0) {
        console.warn(
          '[download.data] GitHub releases returned no CLI zip assets — download buttons will be empty',
        )
      } else {
        console.log(
          `[download.data] resolved ${cliCount} CLI RIDs from tag ${links.cli['win-x64']?.tag ?? links.latestTag}`,
        )
      }
      return links
    } catch (e) {
      console.error('[download.data] failed to load GitHub releases:', e)
      // Fail the Pages build rather than silently shipping empty download buttons.
      throw e
    }
  },
})
