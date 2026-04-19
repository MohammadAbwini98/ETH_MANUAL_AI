'use strict';

const express = require('express');
const { spawn, execSync } = require('child_process');
const path = require('path');
const fs = require('fs');
const { Readable } = require('stream');
const { Pool } = require('pg');

const app = express();
const PORT = 5233;
const ROOT = path.resolve(__dirname, '..');
const LOGS = path.join(ROOT, 'logs');
const APP_SETTINGS = path.join(ROOT, 'src/EthSignal.Web/appsettings.json');
const ETH_SERVICE_BASE = 'http://127.0.0.1:5234';

// ─── DB setup (reads connection string from appsettings.json) ─────────────────

function loadRootEnv() {
    const envPath = path.join(ROOT, '.env');
    if (!fs.existsSync(envPath)) return;

    for (const line of fs.readFileSync(envPath, 'utf8').split(/\r?\n/)) {
        const trimmed = line.trim();
        if (!trimmed || trimmed.startsWith('#')) continue;

        const idx = trimmed.indexOf('=');
        if (idx <= 0) continue;

        const key = trimmed.slice(0, idx).trim();
        let value = trimmed.slice(idx + 1).trim();
        if ((value.startsWith('"') && value.endsWith('"')) || (value.startsWith("'") && value.endsWith("'")))
            value = value.slice(1, -1);

        if (!(key in process.env))
            process.env[key] = value;
    }
}

function readAppSettings() {
    try { return JSON.parse(fs.readFileSync(APP_SETTINGS, 'utf8')); }
    catch { return {}; }
}

function parsePgConnectionString(raw) {
    if (!raw || typeof raw !== 'string') return null;

    const trimmed = raw.trim();
    if (!trimmed) return null;

    if (/^postgres(?:ql)?:\/\//i.test(trimmed))
        return { connectionString: trimmed };

    const config = {};
    for (const part of trimmed.split(';')) {
        const segment = part.trim();
        if (!segment) continue;

        const idx = segment.indexOf('=');
        if (idx <= 0) continue;

        const key = segment.slice(0, idx).trim().toLowerCase();
        const value = segment.slice(idx + 1).trim();
        switch (key) {
            case 'host':
            case 'server':
            case 'hostname':
                config.host = value;
                break;
            case 'port':
                config.port = Number.parseInt(value, 10) || 5432;
                break;
            case 'database':
            case 'initial catalog':
                config.database = value;
                break;
            case 'username':
            case 'user':
            case 'user id':
            case 'userid':
            case 'uid':
                config.user = value;
                break;
            case 'password':
            case 'pwd':
                config.password = value;
                break;
            case 'ssl mode':
            case 'sslmode':
                if (!/^disable$/i.test(value))
                    config.ssl = { rejectUnauthorized: false };
                break;
            default:
                break;
        }
    }

    return Object.keys(config).length ? config : { connectionString: trimmed };
}

loadRootEnv();
const appSettings = readAppSettings();
const DB_CONN = process.env.PG_CONNECTION || appSettings?.ConnectionStrings?.PostgreSQL || '';

let pool = null;
if (DB_CONN) {
    pool = new Pool(parsePgConnectionString(DB_CONN));
    // Ensure portal_overrides table exists (fire-and-forget on startup)
    pool.query(`
        CREATE TABLE IF NOT EXISTS "ETH".portal_overrides (
            id INT NOT NULL DEFAULT 1 PRIMARY KEY,
            CONSTRAINT portal_overrides_singleton CHECK (id = 1),
            settings JSONB NOT NULL DEFAULT '{}',
            updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            updated_by TEXT
        );
        INSERT INTO "ETH".portal_overrides (id, settings)
        VALUES (1, '{}') ON CONFLICT (id) DO NOTHING;
    `).catch(err => console.error('[portal-db] Failed to ensure portal_overrides table:', err.message));
} else {
    console.warn('[portal-db] No DB connection string found in appsettings.json — signal blocker persistence disabled');
}

// ─── Helpers ──────────────────────────────────────────────────────────────────

function isRunning(port) {
    try { execSync(`lsof -ti:${port} -sTCP:LISTEN`, { stdio: 'pipe' }); return true; }
    catch { return false; }
}

function startScript(script) {
    const child = spawn('bash', [path.join(ROOT, script)], {
        cwd: ROOT, detached: true, stdio: 'ignore', env: { ...process.env }
    });
    child.unref();
}

function stopScript(script) {
    try { execSync(`bash "${path.join(ROOT, script)}"`, { cwd: ROOT, stdio: 'pipe' }); }
    catch { /* already stopped */ }
}

/** Find the most recent log file matching a prefix, e.g. "ethsignal-" */
function latestLogFile(prefix) {
    try {
        const files = fs.readdirSync(LOGS)
            .filter(f => f.startsWith(prefix) && f.endsWith('.log'))
            .sort()
            .reverse();
        return files.length ? path.join(LOGS, files[0]) : null;
    } catch { return null; }
}

/** Return last N lines of a file as a string array */
function tailLines(filePath, n = 80) {
    if (!filePath || !fs.existsSync(filePath)) return [];
    try {
        const result = execSync(`tail -n ${n} "${filePath}"`, { stdio: 'pipe' });
        return result.toString().split('\n').filter(l => l.trim());
    } catch { return []; }
}

async function postEthJson(pathname, body) {
    const response = await fetch(`${ETH_SERVICE_BASE}${pathname}`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body)
    });

    const text = await response.text();
    const json = text ? JSON.parse(text) : {};
    if (!response.ok)
        throw new Error(json?.error || json?.title || `ETH service call failed: ${response.status}`);

    return json;
}

async function getEthJson(pathname) {
    const response = await fetch(`${ETH_SERVICE_BASE}${pathname}`);
    const text = await response.text();
    const json = text ? JSON.parse(text) : {};
    if (!response.ok)
        throw new Error(json?.error || json?.title || `ETH service call failed: ${response.status}`);

    return json;
}

async function loadPortalSettings() {
    if (!pool) throw new Error('DB not configured');

    const r = await pool.query(
        'SELECT settings FROM "ETH".portal_overrides WHERE id = 1');

    return r.rows[0]?.settings || {};
}

async function persistPortalSettingsPatch(patch) {
    if (!pool) throw new Error('DB not configured');

    const settings = { ...(await loadPortalSettings()), ...patch };

    await pool.query(`
        INSERT INTO "ETH".portal_overrides (id, settings, updated_at, updated_by)
        VALUES (1, $1::jsonb, NOW(), 'portal')
        ON CONFLICT (id) DO UPDATE
            SET settings = EXCLUDED.settings,
                updated_at = EXCLUDED.updated_at,
                updated_by = EXCLUDED.updated_by
    `, [JSON.stringify(settings)]);

    console.log('[portal] Updated portal settings:', Object.keys(patch).join(', '));
    return settings;
}

// ─── Middleware ───────────────────────────────────────────────────────────────

app.use(express.json());
app.use(express.static(path.join(__dirname, 'public')));

// ─── Status & Process control ─────────────────────────────────────────────────

app.get('/api/status', (_req, res) => res.json({
    eth: isRunning(5234) ? 'running' : 'stopped'
}));

app.post('/api/eth/start', (_req, res) => { startScript('run.sh'); res.json({ ok: true }); });
app.post('/api/eth/stop', (_req, res) => { stopScript('Kill.sh'); res.json({ ok: true }); });

app.post('/api/eth/restart', (_req, res) => {
    try {
        // Kill existing process
        try { execSync(`bash "${path.join(ROOT, 'Kill.sh')}"`, { cwd: ROOT, stdio: 'pipe' }); }
        catch { /* already stopped */ }
        // Start fresh (run.sh handles build + startup)
        const child = spawn('bash', [path.join(ROOT, 'run.sh')], {
            cwd: ROOT, detached: true, stdio: 'ignore', env: { ...process.env }
        });
        child.unref();
        res.json({ ok: true, message: 'ETH service restarting — applying code changes' });
    } catch (err) {
        res.status(500).json({ ok: false, error: err.message });
    }
});

// ─── Portal settings (direct DB + appsettings.json — no ETH service required) ─

// Playwright headless — persists to appsettings and applies live when ETH is running
app.get('/api/portal/playwright/headless', async (_req, res) => {
    try {
        if (isRunning(5234)) {
            const live = await getEthJson('/api/admin/playwright/headless');
            return res.json(live);
        }

        const s = readAppSettings();
        res.json({ headless: s?.HighFreqTicks?.Headless ?? true, appliedLive: false, persisted: true });
    } catch (err) {
        res.status(500).json({ error: err.message });
    }
});

app.post('/api/portal/playwright/headless', async (req, res) => {
    try {
        if (typeof req.body?.headless !== 'boolean')
            return res.status(400).json({ error: "Missing 'headless' bool in body" });

        const s = readAppSettings();
        if (!s.HighFreqTicks) s.HighFreqTicks = {};
        s.HighFreqTicks.Headless = req.body.headless;
        fs.writeFileSync(APP_SETTINGS, JSON.stringify(s, null, 2));

        let appliedLive = false;
        if (isRunning(5234)) {
            try {
                const live = await postEthJson('/api/admin/playwright/headless', {
                    headless: req.body.headless
                });
                appliedLive = live.appliedLive !== false;
            } catch (err) {
                console.error('[portal] Live Playwright mode update failed:', err.message);
            }
        }

        res.json({ headless: req.body.headless, appliedLive, persisted: true });
    } catch (err) {
        res.status(500).json({ error: err.message });
    }
});

// Signal blockers — direct PostgreSQL access (no ETH service required)
app.get('/api/portal/signal-blockers', async (_req, res) => {
    if (!pool) return res.status(503).json({ error: 'DB not configured' });
    try {
        const r = await pool.query(
            'SELECT settings, updated_at, updated_by FROM "ETH".portal_overrides WHERE id = 1');
        if (r.rows.length === 0) return res.json({ settings: {} });
        const { settings, updated_at, updated_by } = r.rows[0];
        res.json({ ...settings, updatedAt: updated_at, updatedBy: updated_by });
    } catch (err) {
        res.status(500).json({ error: err.message });
    }
});

app.patch('/api/portal/signal-blockers', async (req, res) => {
    if (!pool) return res.status(503).json({ error: 'DB not configured' });
    try {
        const body = req.body || {};
        const allowed = ['maxOpenPositions', 'maxOpenPerTimeframe', 'maxOpenPerDirection',
            'dailyLossCapPercent', 'maxConsecutiveLossesPerDay', 'scalpMaxConsecutiveLossesPerDay'];
        const settings = {};
        for (const key of allowed) {
            if (key in body && (typeof body[key] === 'number' || body[key] === null)) {
                if (body[key] !== null) settings[key] = body[key];
            }
        }
        await persistPortalSettingsPatch(settings);

        let appliedLive = false;
        if (isRunning(5234)) {
            try {
                await postEthJson('/api/admin/signal-blockers/refresh', {});
                appliedLive = true;
            } catch (err) {
                console.error('[portal] Live parameter refresh failed:', err.message);
            }
        }

        res.json({ ok: true, settings, appliedLive });
    } catch (err) {
        res.status(500).json({ error: err.message });
    }
});

app.get('/api/portal/global-config', async (_req, res) => {
    if (!pool) return res.status(503).json({ error: 'DB not configured' });
    try {
        const r = await pool.query(
            'SELECT settings, updated_at, updated_by FROM "ETH".portal_overrides WHERE id = 1');
        if (r.rows.length === 0) {
            return res.json({ recommendedSignalExecutionEnabled: true, updatedAt: null, updatedBy: null });
        }

        const { settings, updated_at, updated_by } = r.rows[0];
        res.json({
            recommendedSignalExecutionEnabled:
                typeof settings?.recommendedSignalExecutionEnabled === 'boolean'
                    ? settings.recommendedSignalExecutionEnabled
                    : true,
            updatedAt: updated_at,
            updatedBy: updated_by
        });
    } catch (err) {
        res.status(500).json({ error: err.message });
    }
});

app.patch('/api/portal/global-config', async (req, res) => {
    if (!pool) return res.status(503).json({ error: 'DB not configured' });
    try {
        if (typeof req.body?.recommendedSignalExecutionEnabled !== 'boolean') {
            return res.status(400).json({ error: "Missing 'recommendedSignalExecutionEnabled' bool in body" });
        }

        const settings = await persistPortalSettingsPatch({
            recommendedSignalExecutionEnabled: req.body.recommendedSignalExecutionEnabled
        });

        let appliedLive = false;
        if (isRunning(5234)) {
            try {
                await postEthJson('/api/admin/global-config', {
                    recommendedSignalExecutionEnabled: req.body.recommendedSignalExecutionEnabled
                });
                appliedLive = true;
            } catch (err) {
                console.error('[portal] Live global-config apply failed:', err.message);
            }
        }

        res.json({
            ok: true,
            recommendedSignalExecutionEnabled: settings.recommendedSignalExecutionEnabled,
            appliedLive
        });
    } catch (err) {
        res.status(500).json({ error: err.message });
    }
});

// ─── ETH passthrough APIs for portal tick strip (same-origin) ──────────────

app.get('/api/eth/quote/current', async (_req, res) => {
    try {
        const upstream = await fetch('http://127.0.0.1:5234/api/quote/current');
        const text = await upstream.text();
        res.status(upstream.status).type('application/json').send(text);
    } catch {
        res.status(502).json({ error: 'ETH service unavailable' });
    }
});

app.get('/api/eth/health/ticks', async (_req, res) => {
    try {
        const upstream = await fetch('http://127.0.0.1:5234/api/health/ticks');
        const text = await upstream.text();
        res.status(upstream.status).type('application/json').send(text);
    } catch {
        res.status(502).json({ error: 'ETH service unavailable' });
    }
});

app.get('/api/eth/ticks/stream', async (req, res) => {
    try {
        const upstream = await fetch('http://127.0.0.1:5234/api/ticks/stream', {
            headers: { Accept: 'text/event-stream' }
        });

        if (!upstream.ok || !upstream.body) {
            return res.status(502).json({ error: 'ETH tick stream unavailable' });
        }

        res.status(upstream.status);
        res.setHeader('Content-Type', upstream.headers.get('content-type') || 'text/event-stream');
        res.setHeader('Cache-Control', 'no-cache');
        res.setHeader('Connection', 'keep-alive');

        const nodeReadable = Readable.fromWeb(upstream.body);
        req.on('close', () => nodeReadable.destroy());
        nodeReadable.pipe(res);
    } catch {
        res.status(502).json({ error: 'ETH tick stream unavailable' });
    }
});

// ─── Service-ready redirect page ─────────────────────────────────────────────

const DASH_PORTS = { eth: 5234 };

app.get('/await/:inst', (req, res) => {
    const inst = req.params.inst;
    const port = DASH_PORTS[inst];
    if (!port) return res.status(404).send('Unknown instrument');
    const label = 'ETH/USD';
    res.send(`<!DOCTYPE html>
<html><head><meta charset="UTF-8"><title>Starting ${label}\u2026</title>
<style>
  body{margin:0;background:#0d1117;color:#f0f6fc;font-family:system-ui;
       display:flex;align-items:center;justify-content:center;
       height:100vh;flex-direction:column;gap:12px}
  .dot{width:10px;height:10px;border-radius:50%;background:#e3b341;
       animation:bl 1s infinite}
  @keyframes bl{0%,100%{opacity:1}50%{opacity:.25}}
</style></head>
<body><div class="dot"></div>
<p>Starting <strong>${label}</strong> service\u2026</p>
<script>
  (function poll(){
    fetch('http://localhost:${port}/',{mode:'no-cors'})
      .then(function(){ window.location.href='http://localhost:${port}/'; })
      .catch(function(){ setTimeout(poll,1500); });
  })();
</script></body></html>`);
});

// ─── Log streaming ────────────────────────────────────────────────────────────

const LOG_PREFIX = { eth: 'ethsignal-' };

app.get('/api/logs/:inst', (req, res) => {
    const prefix = LOG_PREFIX[req.params.inst];
    if (!prefix) return res.status(404).json({ error: 'unknown instrument' });
    const file = latestLogFile(prefix);
    const lines = tailLines(file, 80);
    res.json({ lines, file: file ? path.basename(file) : null });
});

// ─── Graceful shutdown ────────────────────────────────────────────────────────

function killServices() {
    for (const port of [5234]) {
        try {
            const pids = execSync(`lsof -ti:${port} -sTCP:LISTEN`, { stdio: 'pipe' }).toString().trim();
            if (pids) {
                pids.split('\n').forEach(pid => { try { process.kill(Number(pid), 'SIGKILL'); } catch { } });
                console.log(`Killed service on port ${port}`);
            }
        } catch { /* not running */ }
    }
}

function shutdown() {
    console.log('\nShutting down portal — killing ETH service...');
    killServices();
    process.exit(0);
}

process.on('SIGINT', shutdown);
process.on('SIGTERM', shutdown);
process.on('SIGHUP', shutdown);
process.on('exit', killServices);

// ─── Start ────────────────────────────────────────────────────────────────────

app.listen(PORT, () => console.log(`Signal Portal → http://localhost:${PORT}`));
