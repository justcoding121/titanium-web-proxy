<script setup>
import consoleScreenshot from '../../../../wiki/images/basic-screenshot.jpg'
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

var endPoint = new ExplicitProxyEndPoint(
    IPAddress.Loopback, 8000, decryptSsl: true);
proxyServer.AddEndPoint(endPoint);
// The first run installs a root certificate in the current user's trust store.
proxyServer.CertificateManager.EnsureRootCertificate(
    userTrustRootCertificate: true,
    machineTrustRootCertificate: false);
proxyServer.Start();</code></pre>
    <p class="home-hero-demo__note">
      The first run installs a root certificate for the current user.
      <a href="/docs/security">Trust</a>.
      <strong>45,499</strong> requests/second on Linux for a tiny TLS GET.
      <a href="#performance">Chart</a>.
    </p>
    <figure class="inspector-preview">
      <img
        :src="consoleScreenshot"
        alt="Console log of each requested URL"
        width="1400"
        height="888"
      />
      <figcaption>Each requested URL logged in the console.</figcaption>
    </figure>
    <p class="home-quick-start__links">
      <a class="home-quick-start__cta" href="/docs/getting-started#embed-in-net-library">Getting started →</a>
    </p>
  </section>
</template>
