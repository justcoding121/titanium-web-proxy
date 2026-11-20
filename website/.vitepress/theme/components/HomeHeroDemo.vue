<script setup>
import inspectorScreenshot from '../../../../wiki/images/inspector-screenshot.jpg'
</script>

<template>
  <section class="home-hero-demo">
    <pre class="home-quick-start__pre"><code>using System.Net;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Models;

using var proxyServer = new ProxyServer();
proxyServer.BeforeRequest += async (s, e) =>
{
    Console.WriteLine(e.HttpClient.Request.Url);
};

var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 8000, decryptSsl: true);
proxyServer.AddEndPoint(endPoint);
// The first run installs a root certificate in the current user's trust store.
proxyServer.CertificateManager.EnsureRootCertificate(
    userTrustRootCertificate: true,
    machineTrustRootCertificate: false);
proxyServer.Start();</code></pre>
    <p class="home-hero-demo__note">
      The first run installs a root certificate for the current user.
      <a href="/docs/security">Trust</a>.
      <strong>26,023</strong> requests/second on Linux for a tiny TLS GET.
      <a href="#performance">Chart</a>.
    </p>
    <figure class="inspector-preview">
      <img
        :src="inspectorScreenshot"
        alt="Titanium Inspector session grid with HTTPS decrypt and headers pane"
        width="1400"
        height="888"
      />
      <figcaption>Session grid, HTTPS decrypt, and inspectors.</figcaption>
    </figure>
    <p class="home-quick-start__links">
      <a class="home-quick-start__cta" href="/download/#stable-inspector">Download Inspector →</a>
    </p>
  </section>
</template>
