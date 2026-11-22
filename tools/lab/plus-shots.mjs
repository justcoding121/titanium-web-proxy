import { chromium } from "playwright-core";
import { mkdir } from "node:fs/promises";

const out = process.env.SHOTS_OUT;
const base = process.env.SHOTS_BASE || "http://127.0.0.1:19181";
const chrome = process.env.CHROME_BIN;
if (!out || !chrome) {
  throw new Error("SHOTS_OUT and CHROME_BIN are required");
}
await mkdir(out, { recursive: true });
const views = [
  ["overview", "/"],
  ["routes", "/routes"],
  ["health", "/health"],
  ["config", "/config"],
  ["certificates", "/certificates"],
  ["audit", "/audit"],
];
const browser = await chromium.launch({ executablePath: chrome, headless: true });
try {
  for (const scheme of ["light", "dark"]) {
    const context = await browser.newContext({
      viewport: { width: 1280, height: 900 },
      colorScheme: scheme,
    });
    const page = await context.newPage();
    await page.goto(base + "/login");
    await page.fill("#secret", "smoke-operator-secret-806");
    await page.click("button[type=submit]");
    await page.waitForURL(base + "/");
    for (const [name, path] of views) {
      await page.goto(base + path, { waitUntil: "domcontentloaded" });
      await page.waitForTimeout(600);
      await page.screenshot({ path: `${out}/${name}-${scheme}.png`, fullPage: true });
    }
    if (scheme === "light") {
      const overview = await page.evaluate(async (b) => {
        const r = await fetch(b + "/api/overview");
        return await r.text();
      }, base);
      console.log("OVERVIEW " + overview);
    }
    await context.close();
  }
} finally {
  await browser.close();
}
console.log("PLUS SHOTS PASS");
