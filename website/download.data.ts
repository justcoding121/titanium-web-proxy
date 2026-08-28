/// <reference types="vitepress/client" />
import { defineLoader } from 'vitepress'

export interface DownloadAsset {
  name: string
  url: string
  tag: string
}

export interface DownloadLinks {
  cli: {
    'win-x64'?: DownloadAsset
    'linux-x64'?: DownloadAsset
    'osx-x64'?: DownloadAsset
  }
  inspector: {
    msi?: DownloadAsset
    zip?: DownloadAsset
  }
  releasesUrl: string
  latestTag: string | null
}

declare const data: DownloadLinks
export { data }

const RELEASES = 'https://github.com/justcoding121/titanium-web-proxy/releases'

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
          if (a.name === 'Titanium.Cli-win-x64.zip' && !out.cli['win-x64']) out.cli['win-x64'] = asset
          if (a.name === 'Titanium.Cli-linux-x64.zip' && !out.cli['linux-x64']) out.cli['linux-x64'] = asset
          if (a.name === 'Titanium.Cli-osx-x64.zip' && !out.cli['osx-x64']) out.cli['osx-x64'] = asset
          if (a.name === 'TitaniumInspector-win-x64.msi' && !out.inspector.msi) out.inspector.msi = asset
          if (a.name === 'TitaniumInspector-win-x64.zip' && !out.inspector.zip) out.inspector.zip = asset
        }
        if (
          out.cli['win-x64'] &&
          out.cli['linux-x64'] &&
          out.cli['osx-x64'] &&
          out.inspector.msi &&
          out.inspector.zip
        ) {
          break
        }
      }
      return out
    } catch {
      return empty
    }
  },
})
