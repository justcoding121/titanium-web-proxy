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

export interface ChannelDownloads {
  /** GitHub release tag, or null when no product zip/MSI release exists for the channel. */
  tag: string | null
  cli: Partial<Record<CliRid, DownloadAsset>>
  inspector: {
    msi?: DownloadAsset
    zip?: DownloadAsset
  } & Partial<Record<CliRid, DownloadAsset>>
}

export interface DownloadLinks {
  stable: ChannelDownloads
  beta: ChannelDownloads
  releasesUrl: string
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

function emptyChannel(): ChannelDownloads {
  return { tag: null, cli: {}, inspector: {} }
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

function hasProductAssets(r: GhRelease): boolean {
  return (r.assets ?? []).some(
    (a) =>
      a.name.startsWith('Titanium.Cli-') ||
      a.name.startsWith('TitaniumInspector-') ||
      a.name.startsWith('Titanium.Plus-'),
  )
}

function isStableRelease(r: GhRelease): boolean {
  return !r.prerelease && !r.tag_name.includes('-')
}

function isBetaRelease(r: GhRelease): boolean {
  return (
    r.prerelease ||
    r.tag_name.includes('beta') ||
    /-/.test(r.tag_name.replace(/^v/, ''))
  )
}

function assignAsset(out: ChannelDownloads, asset: DownloadAsset, name: string): void {
  for (const rid of CLI_RIDS) {
    if (name === `Titanium.Cli-${rid}.zip` && !out.cli[rid]) out.cli[rid] = asset
    if (name === `TitaniumInspector-${rid}.zip` && !out.inspector[rid]) out.inspector[rid] = asset
  }
  if (name === 'TitaniumInspector-win-x64.msi' && !out.inspector.msi) out.inspector.msi = asset
  if (name === 'TitaniumInspector-win-x64.zip' && !out.inspector.zip) out.inspector.zip = asset
}

function channelFromRelease(r: GhRelease): ChannelDownloads {
  const out = emptyChannel()
  out.tag = r.tag_name
  for (const a of r.assets ?? []) {
    assignAsset(
      out,
      { name: a.name, url: a.browser_download_url, tag: r.tag_name },
      a.name,
    )
  }
  return out
}

function pickChannel(
  releases: GhRelease[],
  pred: (r: GhRelease) => boolean,
): ChannelDownloads {
  const hit = releases.find((r) => pred(r) && hasProductAssets(r))
  return hit ? channelFromRelease(hit) : emptyChannel()
}

function mapReleases(releases: GhRelease[]): DownloadLinks {
  return {
    stable: pickChannel(releases, isStableRelease),
    beta: pickChannel(releases, isBetaRelease),
    releasesUrl: RELEASES,
  }
}

export default defineLoader({
  async load(): Promise<DownloadLinks> {
    const url =
      'https://api.github.com/repos/justcoding121/titanium-web-proxy/releases?per_page=40'
    const res = await fetch(url, { headers: githubHeaders() })
    if (!res.ok) {
      throw new Error(`GitHub releases API ${res.status} ${res.statusText}`)
    }
    const links = mapReleases((await res.json()) as GhRelease[])
    const stableCli = Object.keys(links.stable.cli).length
    const betaCli = Object.keys(links.beta.cli).length
    console.log(
      `[download.data] stable=${links.stable.tag ?? 'none'} (${stableCli} CLI) ` +
        `beta=${links.beta.tag ?? 'none'} (${betaCli} CLI)`,
    )
    if (stableCli === 0 && betaCli === 0) {
      throw new Error(
        '[download.data] no CLI/Inspector zip assets found on any GitHub Release',
      )
    }
    return links
  },
})
