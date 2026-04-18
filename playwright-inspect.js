const fs = require('fs');
const path = require('path');
const { spawnSync } = require('child_process');

const workspaceRoot = '/Users/mohammadabwini/Desktop/ETH_MANUAL';
const { chromium } = require(path.join(workspaceRoot, 'src/EthSignal.Infrastructure/bin/Debug/net9.0/.playwright/package'));

const userDataDir = path.join(workspaceRoot, 'logs', 'capital-chrome-profile');
const storageStatePath = path.join(userDataDir, 'capital-storage-state.json');
const screenshotPath = path.join(workspaceRoot, 'logs', 'playwright-inspect.png');
const targetUrl = 'https://capital.com/trading/platform/trade';
const browserArgs = [
  '--disable-blink-features=AutomationControlled',
  '--disable-infobars',
  '--no-first-run',
  '--no-default-browser-check'
];

function getChromeVersion() {
  const cmd = '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome';
  const result = spawnSync(cmd, ['--version'], { encoding: 'utf8' });
  if (result.error || result.status !== 0) return null;
  return result.stdout.trim();
}

function isChromeVersionSupported(versionText) {
  if (!versionText) return false;
  const match = versionText.match(/(\d+)\./);
  if (!match) return false;
  const major = Number(match[1]);
  return Number.isFinite(major) && major >= 120;
}

async function isLoggedIn(page) {
  const overlayExists = await page.evaluate(
    "() => document.querySelector('.cdk-overlay-backdrop') !== null"
  );
  if (overlayExists) return false;

  const balanceCount = await page.locator("[class*='account-balance'], [class*='equity'], [class*='availableFunds']").count();
  if (balanceCount > 0) return true;

  const loginLocator = page.locator("button:has-text('Log in'), button:has-text('Login'), a:has-text('Log in'), a:has-text('Login'), [class*='login-btn']");
  const loginCount = await loginLocator.count();
  const loginVisible = loginCount > 0 ? await loginLocator.first().isVisible() : false;
  const onPlatform = page.url().includes('/trading/platform');

  return onPlatform && !loginVisible;
}

async function waitForManualLogin(page, timeoutMs) {
  const start = Date.now();
  while ((Date.now() - start) < timeoutMs) {
    if (await isLoggedIn(page)) return true;
    await page.waitForTimeout(5000);
  }
  return false;
}

async function launchPersistentContext() {
  try {
    return await chromium.launchPersistentContext(userDataDir, {
      headless: false,
      channel: 'chrome',
      viewport: { width: 1400, height: 900 },
      slowMo: 0,
      ignoreDefaultArgs: ['--enable-automation'],
      args: browserArgs
    });
  } catch (err) {
    const msg = String(err && err.message ? err.message : err);
    if (/existing browser session|already in use|singleton/i.test(msg)) {
      throw new Error(
        'Chrome profile is already open. Close Chrome windows launched with logs/capital-chrome-profile and rerun.'
      );
    }

    throw new Error(
      `Failed to launch real Chrome channel. Google login requires Chrome. Original error: ${msg}`
    );
  }
}

async function applyStealth(page) {
  await page.addInitScript(() => {
    Object.defineProperty(navigator, 'webdriver', {
      get: () => undefined,
      configurable: true
    });
  });
}

(async () => {
  fs.mkdirSync(userDataDir, { recursive: true });

  const chromeVersion = getChromeVersion();
  console.log('Installed Chrome:', chromeVersion || 'not found');
  console.log('Chrome supported for Google login:', isChromeVersionSupported(chromeVersion));
  console.log('Persistent profile dir:', userDataDir);

  const context = await launchPersistentContext();
  const pages = context.pages();
  const page = pages.length > 0 ? pages[0] : await context.newPage();
  await applyStealth(page);

  console.log('Navigating to Capital.com...');
  await page.goto(targetUrl, { waitUntil: 'load', timeout: 60000 });

  console.log('Waiting 8s for SPA render...');
  await page.waitForTimeout(8000);
  console.log('Current URL:', page.url());

  if (!(await isLoggedIn(page))) {
    console.log('Not logged in yet. Please complete Google + Capital 2FA in the opened browser window (max 3 minutes).');
    const ok = await waitForManualLogin(page, 180000);
    if (!ok) {
      throw new Error('Login was not detected within 3 minutes.');
    }
  }

  await context.storageState({ path: storageStatePath });
  console.log('Session state saved to:', storageStatePath);

  await page.screenshot({ path: screenshotPath, fullPage: true });
  console.log('Screenshot saved to:', screenshotPath);

  await context.close();
  console.log('Done. Your login session is now persisted for reuse.');
})().catch(err => {
  console.error(err);
  process.exit(1);
});
