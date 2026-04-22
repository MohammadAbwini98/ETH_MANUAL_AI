// ─── Amman/Jordan timezone ─────────────────────────────
const AMMAN_TZ = 'Asia/Amman';
function toAmman(isoStr) {
    if (!isoStr) return '--';
    return new Date(isoStr).toLocaleString('en-US', {
        timeZone: AMMAN_TZ, year: 'numeric', month: '2-digit', day: '2-digit',
        hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: true
    });
}
function toAmmanShort(isoStr) {
    if (!isoStr) return '--';
    return new Date(isoStr).toLocaleString('en-US', {
        timeZone: AMMAN_TZ, month: '2-digit', day: '2-digit',
        hour: '2-digit', minute: '2-digit', hour12: true
    });
}

const $ = id => document.getElementById(id);
function fmt(v, d = 2) { return v != null ? Number(v).toFixed(d) : '--'; }
function fmtPct(v) { return v != null ? Number(v).toFixed(1) + '%' : '--'; }

// Current ML Mode — updated by refreshActiveParameterSet(), used by loadMlModelRegistry()
let _currentMlMode = null; // 'DISABLED' | 'SHADOW' | 'ACTIVE' | null (unknown)
function escapeHtml(value) {
    return String(value ?? '')
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#39;');
}

function formatDurationSeconds(totalSeconds) {
    const n = Number(totalSeconds);
    if (!Number.isFinite(n) || n <= 0) return '--';

    const days = Math.floor(n / 86400);
    const hours = Math.floor((n % 86400) / 3600);
    const minutes = Math.floor((n % 3600) / 60);

    if (days > 0) return `${days}d ${hours}h`;
    if (hours > 0) return `${hours}h ${minutes}m`;
    return `${minutes}m`;
}

function syncBadgeClass(status) {
    switch ((status || '').toUpperCase()) {
        case 'READY': return 'badge-win';
        case 'FAILED': return 'badge-loss';
        case 'RUNNING': return 'badge-pending';
        default: return 'badge-neutral';
    }
}

function diagnosticBadgeClass(status) {
    switch ((status || '').toUpperCase()) {
        case 'HEALTHY': return 'badge-win';
        case 'WARNING': return 'badge-expired';
        case 'CRITICAL': return 'badge-loss';
        default: return 'badge-neutral';
    }
}

function diagnosticTextColor(status) {
    switch ((status || '').toUpperCase()) {
        case 'HEALTHY': return 'var(--green)';
        case 'WARNING': return 'var(--yellow)';
        case 'CRITICAL': return 'var(--red)';
        case 'INSUFFICIENT_DATA': return 'var(--blue)';
        default: return 'var(--text-sub)';
    }
}

function diagnosticPanelColor(status) {
    switch ((status || '').toUpperCase()) {
        case 'HEALTHY': return 'rgba(63, 185, 80, .10)';
        case 'WARNING': return 'rgba(227, 179, 65, .10)';
        case 'CRITICAL': return 'rgba(248, 81, 73, .11)';
        case 'INSUFFICIENT_DATA': return 'rgba(88, 166, 255, .10)';
        default: return 'rgba(88, 166, 255, .08)';
    }
}

function diagnosticBorderColor(status) {
    switch ((status || '').toUpperCase()) {
        case 'HEALTHY': return 'rgba(63, 185, 80, .28)';
        case 'WARNING': return 'rgba(227, 179, 65, .30)';
        case 'CRITICAL': return 'rgba(248, 81, 73, .32)';
        case 'INSUFFICIENT_DATA': return 'rgba(88, 166, 255, .28)';
        default: return 'rgba(88, 166, 255, .20)';
    }
}

function setDiagnosticText(id, text, color = 'var(--text-main)') {
    const el = $(id);
    if (!el) return;
    el.textContent = text;
    el.style.color = color;
}

function setDiagnosticTitle(id, status) {
    const el = $(id);
    if (!el) return;
    el.style.color = diagnosticTextColor(status) || 'var(--text-main)';
}

function issueCountColor(count, warningThreshold = 1, criticalThreshold = warningThreshold) {
    const value = Number(count);
    if (!Number.isFinite(value) || value <= 0) return 'var(--green)';
    if (value >= criticalThreshold) return 'var(--red)';
    if (value >= warningThreshold) return 'var(--yellow)';
    return 'var(--text-main)';
}

function coverageColor(pct) {
    const value = Number(pct);
    if (!Number.isFinite(value)) return 'var(--text-main)';
    if (value >= 95) return 'var(--green)';
    if (value >= 80) return 'var(--yellow)';
    return 'var(--red)';
}

function sampleCountColor(count, healthyThreshold = 100, warningThreshold = 20) {
    const value = Number(count);
    if (!Number.isFinite(value)) return 'var(--text-main)';
    if (value >= healthyThreshold) return 'var(--green)';
    if (value >= warningThreshold) return 'var(--yellow)';
    return 'var(--red)';
}

function calibrationGapColor(gap) {
    const value = Math.abs(Number(gap));
    if (!Number.isFinite(value)) return 'var(--text-main)';
    if (value >= 0.15) return 'var(--red)';
    if (value >= 0.08) return 'var(--yellow)';
    if (value >= 0.05) return 'var(--blue)';
    return 'var(--green)';
}

function liftColor(lift) {
    const value = Number(lift);
    if (!Number.isFinite(value)) return 'var(--text-main)';
    if (value <= 0) return 'var(--red)';
    if (value < 0.05) return 'var(--yellow)';
    return 'var(--green)';
}

function psiColor(psi) {
    const value = Number(psi);
    if (!Number.isFinite(value)) return 'var(--text-main)';
    if (value >= 0.25) return 'var(--red)';
    if (value >= 0.10) return 'var(--yellow)';
    if (value >= 0.05) return 'var(--blue)';
    return 'var(--green)';
}

function buildMlPracticalNote(data) {
    const overall = String(data?.overallStatus || '').toUpperCase();
    const labelStatus = String(data?.labelQuality?.status || '').toUpperCase();
    const calStatus = String(data?.calibration?.status || '').toUpperCase();
    const driftStatus = String(data?.featureDrift?.status || '').toUpperCase();
    const featureVersion = data?.featureVersion || 'unknown';
    const gate = data?.calibration?.gateThreshold != null ? fmtPct(data.calibration.gateThreshold * 100) : null;
    const sampleCount = data?.calibration?.sampleCount ?? 0;
    const activeSampleCount = data?.calibration?.activeModelSampleCount ?? sampleCount;
    const liveCount = data?.featureDrift?.liveSampleCount ?? 0;
    const trainCount = data?.featureDrift?.trainingSampleCount ?? 0;
    const auc = data?.model?.aucRoc != null ? fmt(data.model.aucRoc, 3) : null;
    const brier = data?.model?.brierScore != null ? fmt(data.model.brierScore, 3) : null;
    const modelFeatureCount = data?.model?.featureCount ?? 0;
    const currentFeatureCount = data?.model?.currentFeatureCount ?? 0;
    const pendingLinks = data?.labelQuality?.pendingLinkSnapshots ?? 0;
    const noSignal = data?.labelQuality?.expectedNoSignalSnapshots ?? 0;
    const blocked = data?.labelQuality?.operationallyBlockedSnapshots ?? 0;
    const mlFiltered = data?.labelQuality?.mlFilteredSnapshots ?? 0;
    const notes = [];

    if (data?.isFeatureVersionFallback === true) {
        notes.push(`The data-quality panel is currently reading ${featureVersion} rows because the current runtime feature contract has not accumulated enough labeled snapshots yet.`);
    }

    if (modelFeatureCount > 0 && currentFeatureCount > 0 && modelFeatureCount < currentFeatureCount) {
        notes.push(`The active model still uses a legacy ${modelFeatureCount}-feature contract while runtime extraction now emits ${currentFeatureCount} features, so the new market-context fields will not affect predictions until the model is retrained.`);
    }

    if (data?.calibration?.usesActiveModelOnly === false) {
        notes.push(`Active-model calibration is still thin (${activeSampleCount} resolved active samples), so the panel is temporarily leaning on all recent models for context rather than treating the current model as fully judged.`);
    }

    if (calStatus === 'CRITICAL') {
        if (auc != null && brier != null) {
            notes.push(`Offline model validation still looks usable (AUC ${auc}, Brier ${brier}), so the main live problem is calibration rather than complete model collapse.`);
        }
        notes.push(`Recent prediction separation is weak around the ${gate || 'active'} gate, based on ${sampleCount} resolved recent samples.`);
    } else if (calStatus === 'WARNING') {
        notes.push(`Calibration is only moderately aligned right now, so treat live win probabilities as directional rather than precise.`);
    }

    if (labelStatus === 'CRITICAL') {
        notes.push(`Label integrity needs cleanup before trusting retraining outputs.`);
    } else if (labelStatus === 'WARNING') {
        notes.push(`Label coverage still has some cleanup debt, which can blur retraining quality.`);
    }

    if (pendingLinks > 0) {
        notes.push(`There are ${pendingLinks} ML evaluations still waiting on a final signal-link status; that is the part to watch for true pipeline leakage.`);
    }

    if (noSignal > 0 || blocked > 0 || mlFiltered > 0) {
        notes.push(`Many recent ML rows are now classified as intentional no-signal, ML-filtered, or operationally blocked evaluations (${noSignal + mlFiltered + blocked} total), so they should not be read as broken labeling.`);
    }

    if (driftStatus === 'INSUFFICIENT_DATA') {
        notes.push(`Drift is not judged yet because recent live feature coverage is still thin (${liveCount} live vs ${trainCount} labeled training snapshots).`);
    } else if (driftStatus === 'CRITICAL' || driftStatus === 'WARNING') {
        notes.push(`Feature distributions are moving away from training history, so live behavior may differ from offline validation.`);
    }

    if (notes.length === 0) {
        if (overall === 'HEALTHY') {
            notes.push('The current data-quality view looks aligned enough to trust the ML dashboard at face value.');
        } else {
            notes.push('The diagnostics are mixed, but no single failure mode dominates yet.');
        }
    }

    return notes.join(' ');
}

function formatMarketFeatureValue(key, value) {
    if (value == null || Number.isNaN(Number(value))) return '--';
    const numeric = Number(value);

    if (key === 'btcRegimeLabel') {
        return numeric === 1 ? 'BULLISH' : numeric === 2 ? 'BEARISH' : 'NEUTRAL';
    }

    if (key.endsWith('Flag')) {
        return numeric >= 0.5 ? 'ON' : 'OFF';
    }

    if (key.includes('Signals') || key.includes('Count')) {
        return String(Math.round(numeric));
    }

    if (key.includes('Pct') || key.includes('Rate') || key.includes('Strength') || key.includes('Return') || key.includes('Percentile')) {
        return fmtPct(numeric * 100);
    }

    return fmt(numeric, 4);
}

function renderMarketContextGroup(containerId, config, values) {
    const el = $(containerId);
    if (!el) return;

    el.innerHTML = config.map(({ key, label }) => `
        <div class="diag-row">
            <span class="diag-label">${escapeHtml(label)}</span>
            <span class="diag-value">${escapeHtml(formatMarketFeatureValue(key, values?.[key]))}</span>
        </div>`).join('');
}

const MARKET_STRUCTURE_FIELDS = [
    { key: 'sessionRangePositionPct', label: 'Session Range Position' },
    { key: 'distanceToPriorDayHighPct', label: 'Distance To Prior Day High' },
    { key: 'distanceToPriorDayLowPct', label: 'Distance To Prior Day Low' },
    { key: 'distanceToSessionVwapPct', label: 'Distance To Session VWAP' },
    { key: 'rangePositionPct', label: '20-Bar Range Position' },
    { key: 'distanceTo20BarHighPct', label: 'Distance To 20-Bar High' },
    { key: 'distanceTo20BarLowPct', label: 'Distance To 20-Bar Low' }
];

const VOLATILITY_REGIME_FIELDS = [
    { key: 'realizedVol15m', label: 'Realized Vol 15m' },
    { key: 'realizedVol1h', label: 'Realized Vol 1h' },
    { key: 'realizedVol4h', label: 'Realized Vol 4h' },
    { key: 'volatilityCompressionFlag', label: 'Compression Flag' },
    { key: 'volatilityExpansionFlag', label: 'Expansion Flag' },
    { key: 'atrPercentileRank', label: 'ATR Percentile Rank' }
];

const SIGNAL_SATURATION_FIELDS = [
    { key: 'signalsLast10Bars', label: 'Signals Last 10 Bars' },
    { key: 'sameDirectionSignalsLast10', label: 'Same Direction Last 10' },
    { key: 'oppositeDirectionSignalsLast10', label: 'Opposite Direction Last 10' },
    { key: 'recentStopOutCount', label: 'Recent Stop-Out Count' },
    { key: 'recentFalseBreakoutRate', label: 'Recent False-Breakout Rate' }
];

const BTC_CONTEXT_FIELDS = [
    { key: 'btcRecentReturn', label: 'BTC Recent Return' },
    { key: 'btcRegimeLabel', label: 'BTC Regime' },
    { key: 'ethBtcRelativeStrength', label: 'ETH/BTC Relative Strength' }
];

function animateNumber(el, target, decimals = 2, durationMs = 140) {
    if (!el || !Number.isFinite(target)) return;

    const existing = Number.isFinite(el._value)
        ? el._value
        : Number.parseFloat((el.textContent || '').replace(/,/g, ''));
    const from = Number.isFinite(existing) ? existing : target;
    const start = performance.now();

    if (el._raf) cancelAnimationFrame(el._raf);

    const step = now => {
        const t = Math.min(1, (now - start) / durationMs);
        const eased = 1 - Math.pow(1 - t, 3);
        const v = from + (target - from) * eased;
        el._value = v;
        el.textContent = v.toFixed(decimals);
        if (t < 1) el._raf = requestAnimationFrame(step);
    };

    el._raf = requestAnimationFrame(step);
}

function setAnimatedCellValue(id, value, decimals = 2, durationMs = 180) {
    const el = $(id);
    if (!el) return;

    const n = Number(value);
    if (!Number.isFinite(n)) {
        el.textContent = '--';
        el._value = NaN;
        return;
    }

    animateNumber(el, n, decimals, durationMs);
}

function pulsePrice(el, delta) {
    if (!el || !delta) return;
    const color = delta > 0 ? 'var(--green)' : 'var(--red)';
    el.animate(
        [
            { color, transform: 'translateY(-1px) scale(1.02)' },
            { color: 'var(--blue)', transform: 'translateY(0) scale(1)' }
        ],
        { duration: 180, easing: 'ease-out' }
    );
}

async function fetchJson(url) {
    try {
        const r = await fetch(url, {
            cache: 'no-store',
            headers: {
                'Cache-Control': 'no-cache'
            }
        });
        return r.ok ? await r.json() : null;
    }
    catch { return null; }
}

async function refreshHistorySync() {
    const data = await fetchJson('/api/admin/candle-sync/status');
    if (!data) return;

    const status = (data.status || 'PENDING').toUpperCase();
    const badge = $('hsync-status-badge');
    if (badge) {
        badge.textContent = status;
        badge.className = `badge ${syncBadgeClass(status)} ms-2`;
    }

    if ($('hsync-ready')) $('hsync-ready').textContent = data.readyTimeframes ?? '--';
    if ($('hsync-running')) $('hsync-running').textContent = data.runningTimeframes ?? '--';
    if ($('hsync-failed')) $('hsync-failed').textContent = data.failedTimeframes ?? '--';

    const lastRun = data.finishedAtUtc || data.startedAtUtc;
    if ($('hsync-last-run')) $('hsync-last-run').textContent = lastRun ? toAmmanShort(lastRun) : '--';

    const tbody = $('hsync-rows');
    if (!tbody) return;

    const rows = Array.isArray(data.timeframes) ? [...data.timeframes] : [];
    const order = ['4h', '1h', '30m', '15m', '5m', '1m'];
    rows.sort((a, b) => order.indexOf(a.timeframe) - order.indexOf(b.timeframe));

    if (rows.length === 0) {
        tbody.innerHTML = '<tr><td colspan="7" class="text-center" style="color:var(--text)">No startup sync state yet</td></tr>';
        return;
    }

    tbody.innerHTML = rows.map(row => {
        const rowStatus = (row.status || 'PENDING').toUpperCase();
        const rowMode = row.syncMode || '--';
        const lastSynced = row.lastSyncedCandleUtc ? toAmmanShort(row.lastSyncedCandleUtc) : '--';
        const offline = formatDurationSeconds(row.offlineDurationSec);
        const chunks = Number(row.chunksTotal) > 0
            ? `${row.chunksCompleted ?? 0} / ${row.chunksTotal}`
            : (rowMode === 'NOOP' ? 'NOOP' : '--');
        const errorText = row.lastError
            ? `<span title="${escapeHtml(row.lastError)}" style="color:var(--red)">${escapeHtml(row.lastError)}</span>`
            : '--';

        return `<tr>
            <td><span class="badge badge-neutral" style="font-size:.7rem">${escapeHtml(row.timeframe)}</span></td>
            <td><span class="badge ${syncBadgeClass(rowStatus)}" style="font-size:.68rem">${escapeHtml(rowStatus)}</span></td>
            <td>${escapeHtml(rowMode)}</td>
            <td>${escapeHtml(lastSynced)}</td>
            <td>${escapeHtml(offline)}</td>
            <td>${escapeHtml(chunks)}</td>
            <td style="max-width:320px;word-break:break-word">${errorText}</td>
        </tr>`;
    }).join('');
}

function dirClass(dir) {
    if (!dir) return 'neutral';
    const d = dir.toUpperCase();
    return d === 'BUY' ? 'buy' : d === 'SELL' ? 'sell' : 'neutral';
}

// ─── Clock ─────────────────────────────────────────────
function updateClock() {
    const el = $('clock');
    if (el) el.textContent = new Date().toLocaleString('en-US', {
        timeZone: AMMAN_TZ, hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: true
    }) + ' (Amman)';
}
setInterval(updateClock, 1000);
updateClock();

// ─── TF durations in ms (for expiry timer) ────────────
const TF_MS = { '1m': 60e3, '5m': 5 * 60e3, '15m': 15 * 60e3, '30m': 30 * 60e3, '1h': 60 * 60e3, '4h': 4 * 60 * 60e3 };
let TIMEOUT_BARS = 60; // default OutcomeTimeoutBars, updated from server
let MAX_SPREAD_PCT = 0.004; // default MaxSpreadPct, updated from server
let UI_PRICE_ONLY = false;

function updateIndicatorSyntheticBadge() {
    const headers = Array.from(document.querySelectorAll('.card-header'));
    const indicatorsHeader = headers.find(h => (h.textContent || '').trim() === 'Indicators');
    if (!indicatorsHeader) return;

    let badge = document.getElementById('ind-synthetic-badge');
    if (UI_PRICE_ONLY) {
        if (!badge) {
            badge = document.createElement('span');
            badge.id = 'ind-synthetic-badge';
            badge.style.marginLeft = '8px';
            badge.style.fontSize = '.68rem';
            badge.style.padding = '2px 6px';
            badge.style.borderRadius = '999px';
            badge.style.background = 'var(--yellow)';
            badge.style.color = '#000';
            badge.style.fontWeight = '700';
            badge.textContent = 'Synthetic Vol';
            indicatorsHeader.appendChild(badge);
        }
    } else if (badge) {
        badge.remove();
    }
}

async function loadConfigThresholds() {
    const cfg = await fetchJson('/api/config/thresholds');
    if (!cfg) return;
    if (cfg.outcomeTimeoutBars) TIMEOUT_BARS = cfg.outcomeTimeoutBars;
    if (cfg.maxSpreadPct) MAX_SPREAD_PCT = cfg.maxSpreadPct;
    UI_PRICE_ONLY = !!cfg.uiPriceOnly;
    updateIndicatorSyntheticBadge();
}

// ─── Refresh functions ─────────────────────────────────
let quoteSse = null;

function applyQuotePayload(q) {
    if (!q || Number(q.mid) <= 0 || Number(q.bid) <= 0 || Number(q.ask) <= 0) return;

    const midEl = $('mid-price');
    const prevMid = Number.isFinite(midEl?._value) ? midEl._value : Number(q.mid);

    animateNumber(midEl, Number(q.mid), 2);
    animateNumber($('bid-price'), Number(q.bid), 2);
    animateNumber($('ask-price'), Number(q.ask), 2);

    if (Number(q.mid) !== prevMid)
        pulsePrice(midEl, Number(q.mid) - prevMid);

    const spread = (q.ask - q.bid).toFixed(2);
    const spreadPct = ((q.ask - q.bid) / q.mid * 100).toFixed(3);
    const el = $('spread-val');
    el.textContent = `${spread} (${spreadPct}%)`;
    const spreadWarnPct = MAX_SPREAD_PCT * 100 * 0.5;
    const spreadBadPct = MAX_SPREAD_PCT * 100;
    el.className = spreadPct < spreadWarnPct ? 'spread-ok' : spreadPct < spreadBadPct ? 'spread-warn' : 'spread-bad';
    $('quote-time').textContent = toAmman(q.timestamp || q.ts);
    document.title = `ETHUSD ${Number(q.mid).toFixed(2)} — Signal Dashboard`;
}

function connectQuoteSse() {
    if (quoteSse) {
        try { quoteSse.close(); } catch { }
    }

    quoteSse = new EventSource('/api/ticks/stream');
    quoteSse.onmessage = e => {
        try {
            const d = JSON.parse(e.data);
            applyQuotePayload(d);
        } catch { }
    };
    quoteSse.onerror = () => {
        try { quoteSse.close(); } catch { }
        setTimeout(connectQuoteSse, 1500);
    };
}

async function refreshQuote() {
    try {
        const q = await fetchJson('/api/quote/current');
        applyQuotePayload(q);
    } catch { }
}

const INDICATOR_TFS = ['1m', '5m', '15m', '30m', '1h', '4h'];
async function refreshIndicators() {
    const data = await fetchJson('/api/indicators/current');
    if (!data) return;
    const map = { '1m': data.oneMin, '5m': data.fiveMin, '15m': data.fifteenMin, '30m': data.thirtyMin, '1h': data.oneHour, '4h': data.fourHour };
    for (const tf of INDICATOR_TFS) {
        const s = map[tf];
        if (s) {
            setAnimatedCellValue(`ema20-${tf}`, s.ema20, 2);
            setAnimatedCellValue(`ema50-${tf}`, s.ema50, 2);
            setAnimatedCellValue(`rsi-${tf}`, s.rsi14, 1);
            setAnimatedCellValue(`macd-${tf}`, s.macdHist, 4);
            setAnimatedCellValue(`atr-${tf}`, s.atr14, 2);
            setAnimatedCellValue(`adx-${tf}`, s.adx14, 1);
            setAnimatedCellValue(`vwap-${tf}`, s.vwap, 2);
        }
    }
}

async function refreshRegime() {
    const r = await fetchJson('/api/regime/current');
    if (!r) return;

    const primary = r.primary || r;
    const primaryRegime = primary.regime || r.regime || 'NEUTRAL';
    const primaryScore = primary.regimeScore ?? r.regimeScore ?? 0;

    const el = $('regime-label');
    if (el) {
        el.textContent = primaryRegime;
        el.className = 'stat-val ' + dirClass(primaryRegime === 'BULLISH' ? 'BUY' : primaryRegime === 'BEARISH' ? 'SELL' : '');
    }
    const scoreEl = $('regime-score');
    if (scoreEl) scoreEl.textContent = primaryScore;

    const perTf = r.perTimeframe || {};
    const summaryOrder = ['1m', '5m', '15m', '30m', '1h', '4h'];
    const summary = summaryOrder
        .filter(tf => perTf[tf])
        .map(tf => `${tf}:${perTf[tf].regime}(${perTf[tf].regimeScore})`)
        .join(' | ');

    const visibleTf = ['1m', '5m', '30m', '1h'];
    for (const tf of visibleTf) {
        const reg = perTf[tf];
        const tfLabel = $(`regime-${tf}-label`);
        const tfScore = $(`regime-${tf}-score`);
        if (!tfLabel || !tfScore) continue;

        const regimeText = reg?.regime || '--';
        const regimeClass = regimeText === 'BULLISH' ? 'buy' : regimeText === 'BEARISH' ? 'sell' : 'neutral';

        tfLabel.textContent = regimeText;
        tfLabel.className = `stat-val ${regimeClass}`;
        tfLabel.style.fontSize = '.95rem';
        tfScore.textContent = reg?.regimeScore ?? '--';
    }

    let details = $('regime-details');
    if (!details) {
        const scoreHost = $('regime-score')?.parentElement;
        if (scoreHost) {
            details = document.createElement('div');
            details.id = 'regime-details';
            details.style.fontSize = '.68rem';
            details.style.color = 'var(--text-sub)';
            details.style.marginTop = '4px';
            scoreHost.insertAdjacentElement('afterend', details);
        }
    }
    if (details) details.textContent = summary || '--';
}

async function refreshSignal() {
    const payload = await fetchJson('/api/dashboard/latest');
    if (!payload) return;

    const s = payload.signal;
    const linkedDecision = payload.decision;
    const latestDecision = payload.latestDecision;
    const d = linkedDecision || (!s ? latestDecision : null);
    const mlPred = payload.mlPrediction;
    const hasDirectionalSignal = !!s && s.direction !== 'NO_DATA' && s.direction !== 'NO_TRADE';

    // Show decision outcome and reason on the main card
    if ($('sig-outcome')) $('sig-outcome').textContent = d?.outcomeCategory || '--';
    if ($('sig-reason-code')) $('sig-reason-code').textContent = d?.reasonCodes?.[0] || '--';
    if ($('sig-reason-detail')) $('sig-reason-detail').textContent = d?.reasonDetails?.[0] || '--';
    if ($('sig-effective-threshold')) $('sig-effective-threshold').textContent = d?.effectiveThreshold ?? '--';
    if ($('sig-blended-score')) $('sig-blended-score').textContent = d?.blendedConfidence ?? '--';

    if (!hasDirectionalSignal) {
        // If latest decision shows a specific outcome, prefer that label
        let label = 'NO DATA';
        if (d?.outcomeCategory === 'STRATEGY_NO_TRADE') label = 'NO TRADE';
        else if (d?.outcomeCategory === 'OPERATIONAL_BLOCKED') label = 'BLOCKED';
        else if (d?.outcomeCategory === 'CONTEXT_NOT_READY') label = 'NOT READY';
        else if (s?.direction === 'NO_TRADE') label = 'NO TRADE';
        $('sig-direction').textContent = label;
        $('sig-direction').className = 'stat-val neutral';
        $('sig-badge').textContent = label;
        $('sig-badge').className = 'badge badge-neutral';
        $('sig-tf').textContent = d?.timeframe || s?.timeframe || '';
        for (const id of ['sig-entry', 'sig-tp', 'sig-sl', 'sig-risk', 'sig-score']) $(id).textContent = '--';
        $('sig-time').textContent = d?.decisionTime ? toAmmanShort(d.decisionTime) : (s?.signalTimeUtc ? toAmmanShort(s.signalTimeUtc) : '--');
    } else {
        const cls = dirClass(s.direction);
        $('sig-direction').textContent = s.direction;
        $('sig-direction').className = 'stat-val ' + cls;
        $('sig-badge').textContent = s.direction;
        $('sig-badge').className = 'badge badge-' + cls;
        $('sig-tf').textContent = s.timeframe || '';
        $('sig-entry').textContent = fmt(s.entryPrice);
        $('sig-tp').textContent = fmt(s.tpPrice);
        $('sig-sl').textContent = fmt(s.slPrice);
        $('sig-risk').textContent = `${fmtPct(s.riskPercent)} ($${fmt(s.riskUsd)})`;
        $('sig-score').textContent = s.confidenceScore;
        $('sig-time').textContent = toAmmanShort(s.signalTimeUtc);
    }

    // ML prediction overlay — only show if tied to same decision
    const predictionIsSignalLinked = !s?.signalId || !mlPred?.signalId || mlPred.signalId === s.signalId;
    const calibratedWinProb = mlPred?.calibratedWinProbability ?? mlPred?.predictedWinProbability;
    const rawWinProb = mlPred?.rawWinProbability ?? calibratedWinProb;
    if (mlPred && calibratedWinProb != null && predictionIsSignalLinked) {
        const winPct = (calibratedWinProb * 100).toFixed(1) + '%';
        const conf = fmt(mlPred.predictionConfidence ?? mlPred.calibratedConfidence, 0);
        const probColor = calibratedWinProb >= 0.6 ? 'var(--green)'
            : calibratedWinProb >= 0.5 ? 'var(--yellow)'
                : 'var(--red)';
        const probEl = $('sig-ml-prob');
        probEl.textContent = winPct;
        probEl.title = `Raw ${((rawWinProb ?? calibratedWinProb) * 100).toFixed(1)}% | Calibrated ${winPct}`;
        probEl.style.color = probColor;
        probEl.style.fontWeight = '600';
        const confEl = $('sig-ml-conf');
        confEl.textContent = conf;
        confEl.title = 'Prediction confidence';
        confEl.style.color = probColor;
        const modeEl = $('sig-ml-mode');
        modeEl.textContent = mlPred.mode || 'SHADOW';
        modeEl.style.color = mlPred.isActive ? 'var(--green)' : 'var(--yellow)';
    } else {
        $('sig-ml-prob').textContent = '--';
        $('sig-ml-conf').textContent = '--';
        $('sig-ml-mode').textContent = 'OFF';
        $('sig-ml-mode').style.color = 'var(--text-sub)';
    }
}

async function refreshPerformance() {
    const p = await fetchJson('/api/performance/summary');
    if (!p) return;
    $('perf-wr').textContent = fmtPct(p.winRate);
    $('perf-avgr').textContent = fmt(p.averageR, 2) + 'R';
    $('perf-pf').textContent = fmt(p.profitFactor, 1);
    $('perf-total').textContent = p.totalSignals;
    $('perf-wins').textContent = p.wins;
    $('perf-losses').textContent = p.losses;
}

// ─── Signal History with pagination, sorting, filtering ──
let historyData = [];
let currentPage = 1;
const PAGE_SIZE = 30;
let sortCol = 'time';
let sortDir = 'desc';
let executedTradesData = [];
let executedTradesCurrentPage = 1;
const EXECUTED_TRADES_PAGE_SIZE = 25;
let executedTradesTotal = 0;
let tradeQueueData = null;
let historyTotal = 0;

async function loadHistory() {
    const params = new URLSearchParams({
        limit: String(PAGE_SIZE),
        page: String(currentPage),
        sort: sortCol,
        sortDir
    });

    const source = $('filter-source')?.value;
    const timeframe = $('filter-tf')?.value;
    const direction = $('filter-dir')?.value;
    const outcome = $('filter-outcome')?.value;
    const dateFrom = $('filter-date-from')?.value;
    const dateTo = $('filter-date-to')?.value;

    if (source) params.set('source', source);
    if (timeframe) params.set('timeframe', timeframe);
    if (direction) params.set('direction', direction);
    if (outcome) params.set('outcome', outcome);
    if (dateFrom) params.set('from', dateFrom);
    if (dateTo) params.set('to', dateTo);

    const data = await fetchJson(`/api/signals/history?${params.toString()}`);
    if (!data) return;
    historyData = data.signals || [];
    historyTotal = data.total || 0;
    renderHistory();
}

function onFilterChange() {
    currentPage = 1;
    loadHistory().catch(() => { });
}

function getExpiryMs(tf) { return TF_MS[tf] || TF_MS['5m']; }

function computeExpiryTime(signalTimeUtc, tf) {
    const signalMs = new Date(signalTimeUtc).getTime();
    const barMs = getExpiryMs(tf);
    return signalMs + TIMEOUT_BARS * barMs;
}

function formatCountdown(ms) {
    if (ms <= 0) return 'Expired';
    const h = Math.floor(ms / 3600000);
    const m = Math.floor((ms % 3600000) / 60000);
    const s = Math.floor((ms % 60000) / 1000);
    return `${h}h ${m}m ${s}s`;
}

function outcomeClass(label) {
    if (!label) return 'badge-pending';
    switch (label.toUpperCase()) {
        case 'WIN': return 'badge-win';
        case 'LOSS': return 'badge-loss';
        case 'FAILED': return 'badge-loss';
        case 'CLOSED': return 'badge-neutral';
        case 'OPEN': return 'badge-pending';
        case 'CLOSE REQUESTED': return 'badge-pending';
        case 'EXPIRED': return 'badge-expired';
        case 'AMBIGUOUS': return 'badge-ambiguous';
        default: return 'badge-pending';
    }
}

function historyExecutionLabel(status, pendingLabel) {
    switch ((status || '').toUpperCase()) {
        case 'WIN': return 'WIN';
        case 'LOSS': return 'LOSS';
        case 'CLOSED': return 'CLOSED';
        case 'FAILED':
        case 'REJECTED':
        case 'VALIDATIONFAILED':
        case 'CLOSEFAILED': return 'FAILED';
        case 'OPEN': return 'OPEN';
        case 'CLOSEREQUESTED': return 'CLOSE REQUESTED';
        case 'QUEUED':
        case 'PENDING':
        case 'SUBMITTED':
            return pendingLabel;
        default: return null;
    }
}

function getOutcomeLabel(item) {
    const pendingLabel = item.signal?.sourceType === 'Recommended' ? 'OPEN' : 'PENDING';
    const executionLabel = historyExecutionLabel(item.execution?.status, pendingLabel);
    if (executionLabel) return executionLabel;
    if (item.outcome?.outcomeLabel) return item.outcome.outcomeLabel;
    if (item.signal?.sourceType === 'Recommended') {
        if (item.signal?.status === 'OPEN') return 'OPEN';
        return item.signal?.status || 'OPEN';
    }
    return 'PENDING';
}

async function executeCandidate(path, label) {
    if (!confirm(`Execute this ${label} on Capital.com DEMO?`)) return;
    try {
        const response = await fetch(path, { method: 'POST' });
        const payload = await response.json().catch(() => null);
        if (!response.ok) {
            alert(payload?.message || payload?.failureReason || payload?.error || `Execution failed (${response.status})`);
            return;
        }
        await Promise.all([loadExecutedTrades(), refreshTradingSummary(), loadTradeQueue(), loadHistory()]);
        alert(payload?.message || 'Execution request submitted.');
    } catch (e) {
        alert(`Execution failed: ${e.message}`);
    }
}

function sortHistory(col) {
    if (sortCol === col) sortDir = sortDir === 'asc' ? 'desc' : 'asc';
    else { sortCol = col; sortDir = col === 'time' ? 'desc' : 'asc'; }
    loadHistory().catch(() => { });
}

function renderHistory() {
    const totalPages = Math.max(1, Math.ceil(historyTotal / PAGE_SIZE));
    if (currentPage > totalPages) {
        currentPage = totalPages;
        loadHistory().catch(() => { });
        return;
    }
    document.querySelectorAll('#history-table thead th').forEach(th => {
        const icon = th.querySelector('.sort-icon');
        if (icon) icon.textContent = th.dataset.col === sortCol ? (sortDir === 'asc' ? '▲' : '▼') : '';
    });

    const pageItems = historyData;
    const tbody = $('signal-history');
    tbody.innerHTML = '';
    const now = Date.now();

    for (const item of pageItems) {
        const s = item.signal;
        const o = item.outcome;
        const cls = dirClass(s.direction);
        const lbl = getOutcomeLabel(item);
        const oCls = outcomeClass(lbl);
        const pnl = o?.pnlR != null ? (o.pnlR >= 0 ? '+' : '') + Number(o.pnlR).toFixed(2) + 'R' : '--';
        const pnlCls = o?.pnlR > 0 ? 'buy' : o?.pnlR < 0 ? 'sell' : '';

        let expiryHtml;
        const expiryAt = s.expiryTimeUtc ? new Date(s.expiryTimeUtc).getTime() : computeExpiryTime(s.signalTimeUtc, s.timeframe);
        if (lbl === 'OPEN' || lbl === 'PENDING') {
            const expiryMs = expiryAt - now;
            expiryHtml = `<span class="expiry-timer" data-expiry="${expiryAt}">${formatCountdown(expiryMs)}</span>`;
        } else if (o?.closedAtUtc) {
            expiryHtml = `<span class="expiry-timer expired">${toAmmanShort(o.closedAtUtc)}</span>`;
        } else {
            expiryHtml = '<span class="expiry-timer expired">--</span>';
        }

        const tr = document.createElement('tr');
        tr.innerHTML = `
            <td style="font-size:.7rem;font-family:monospace;color:var(--text)">${s.signalId ?? '--'}</td>
            <td>${toAmmanShort(s.signalTimeUtc)}</td>
            <td><span class="badge badge-neutral" style="font-size:.7rem">${s.source || s.sourceType || '--'}</span></td>
            <td><span class="badge badge-neutral" style="font-size:.7rem">${s.timeframe}</span></td>
            <td class="${cls}">${s.direction}</td>
            <td>${fmt(s.entryPrice)}</td>
            <td class="buy">${fmt(s.tpPrice)}</td>
            <td class="sell">${fmt(s.slPrice)}</td>
            <td>${s.confidenceScore}</td>
            <td><span class="badge ${oCls}" style="font-size:.72rem">${lbl}</span></td>
            <td class="${pnlCls}">${pnl}</td>
            <td>${expiryHtml}</td>
            <td><button class="btn btn-sm btn-outline-primary" onclick="executeCandidate('/api/executed-trades/execute-history/${encodeURIComponent(s.sourceType || s.source)}/${s.signalId}', '${String(s.source || s.sourceType || 'signal').toLowerCase()} signal')">Execute</button></td>`;
        tbody.appendChild(tr);
    }

    $('page-info').textContent = `Page ${currentPage} of ${totalPages} (${historyTotal} signals total)`;
    $('prev-page').disabled = currentPage <= 1;
    $('next-page').disabled = currentPage >= totalPages;
}

function changePage(delta) {
    currentPage = Math.max(1, currentPage + delta);
    loadHistory().catch(() => { });
}

function executionStatusBadgeClass(status) {
    switch ((status || '').toUpperCase()) {
        case 'OPEN': return 'badge-win';
        case 'WIN': return 'badge-win';
        case 'LOSS': return 'badge-loss';
        case 'CLOSED': return 'badge-neutral';
        case 'PROCESSING': return 'badge-pending';
        case 'COMPLETED': return 'badge-neutral';
        case 'QUEUED':
        case 'SUBMITTED':
        case 'PENDING':
        case 'CLOSEREQUESTED': return 'badge-pending';
        case 'FAILED':
        case 'REJECTED':
        case 'VALIDATIONFAILED':
        case 'CLOSEFAILED': return 'badge-loss';
        default: return 'badge-expired';
    }
}

function tradingSummaryText(id, value) {
    const el = $(id);
    if (!el) return;
    el.textContent = value ?? '--';
}

function tradeStatusAllowsForceClose(status) {
    const normalized = String(status || '').toUpperCase();
    return normalized === 'OPEN' || normalized === 'SUBMITTED' || normalized === 'CLOSEREQUESTED';
}

function buildExecutedTradesQuery() {
    const qs = new URLSearchParams();
    const fromDate = $('exec-filter-date-from')?.value;
    const toDate = $('exec-filter-date-to')?.value;
    const instrument = $('exec-filter-instrument')?.value?.trim();
    const direction = $('exec-filter-dir')?.value;
    const timeframe = $('exec-filter-tf')?.value;
    const sourceType = $('exec-filter-source')?.value;
    const status = $('exec-filter-status')?.value;

    if (fromDate) qs.set('from', new Date(`${fromDate}T00:00:00`).toISOString());
    if (toDate) qs.set('to', new Date(`${toDate}T23:59:59.999`).toISOString());
    if (instrument) qs.set('instrument', instrument);
    if (direction) qs.set('direction', direction);
    if (timeframe) qs.set('timeframe', timeframe);
    if (sourceType) qs.set('sourceType', sourceType);
    if (status) qs.set('status', status);
    qs.set('limit', String(EXECUTED_TRADES_PAGE_SIZE));
    qs.set('page', String(executedTradesCurrentPage));
    return qs;
}

async function refreshTradingSummary() {
    const [account, stats, health] = await Promise.all([
        fetchJson('/api/trading/account-summary'),
        fetchJson('/api/trading/execution-stats'),
        fetchJson('/api/trading/health')
    ]);

    tradingSummaryText('trade-available', account?.available != null ? fmt(account.available) : '--');
    tradingSummaryText('trade-equity', account?.equity != null ? fmt(account.equity) : '--');
    tradingSummaryText('trade-funds', account?.funds != null ? fmt(account.funds) : '--');
    tradingSummaryText('trade-margin', account?.margin != null ? fmt(account.margin) : '--');
    tradingSummaryText('trade-currency', account?.currency || stats?.currency || '--');
    tradingSummaryText('trade-open', stats?.openTrades ?? account?.openPositions ?? '--');
    tradingSummaryText('trade-total', stats?.totalExecuted ?? '--');
    tradingSummaryText('trade-wins', stats?.wins ?? '--');
    tradingSummaryText('trade-losses', stats?.losses ?? '--');
    tradingSummaryText('trade-pnl', stats?.totalPnl != null ? `${fmt(stats.totalPnl)} ${stats.currency || account?.currency || ''}`.trim() : '--');
    tradingSummaryText('trade-winrate', stats?.winRate != null ? fmtPct(stats.winRate) : '--');
    tradingSummaryText('trade-failed', stats?.failedExecutions ?? '--');

    const demoEl = $('trade-demo-indicator');
    if (demoEl) {
        const accountName = account?.accountName || health?.accountName || '--';
        const accountId = account?.accountId || health?.accountId || '--';
        const isDemo = account?.isDemo === true
            && health?.demoOnly === true
            && health?.activeAccountIsDemo === true
            && health?.activeAccountMatchesRequiredDemo !== false;
        demoEl.textContent = isDemo ? `${accountName} (${accountId}) · DEMO` : `${accountName} (${accountId}) · BLOCKED`;
        demoEl.style.color = isDemo ? 'var(--green)' : 'var(--red)';
        demoEl.style.fontWeight = '700';
    }

    tradingSummaryText('trade-account-id', account?.accountId || health?.accountId || '--');
    tradingSummaryText('trade-selection-source', health?.accountSelectionSource || '--');
    tradingSummaryText('trade-last-exec-account', health?.latestExecutionAccountName || '--');

    const sessionEl = $('trade-session-status');
    if (sessionEl) {
        const ready = health?.sessionReady === true;
        sessionEl.textContent = ready ? 'READY' : 'NOT READY';
        sessionEl.style.color = ready ? 'var(--green)' : 'var(--yellow)';
    }

    tradingSummaryText('trade-last-sync', health?.lastSyncUtc ? toAmmanShort(health.lastSyncUtc) : '--');
    tradingSummaryText('trade-order-note', health?.latestOrderNote || '--');

    const errorEl = $('trade-broker-error');
    if (errorEl) {
        errorEl.textContent = health?.latestBrokerError || '--';
        errorEl.style.color = health?.latestBrokerError ? 'var(--red)' : 'var(--text-sub)';
    }
}

function formatElapsedSeconds(seconds) {
    const totalSeconds = Math.max(0, Math.round(Number(seconds || 0)));
    if (totalSeconds < 60) return `${totalSeconds}s`;
    const totalMinutes = Math.floor(totalSeconds / 60);
    const remainingSeconds = totalSeconds % 60;
    if (totalMinutes < 60) return `${totalMinutes}m ${remainingSeconds}s`;
    const hours = Math.floor(totalMinutes / 60);
    const remainingMinutes = totalMinutes % 60;
    return `${hours}h ${remainingMinutes}m`;
}

function renderTradeQueue() {
    const snapshot = tradeQueueData || {};
    tradingSummaryText('trade-queue-server-time', snapshot.serverTimeUtc ? toAmmanShort(snapshot.serverTimeUtc) : '--');
    tradingSummaryText('trade-queue-active', snapshot.activeTradeCount != null && snapshot.maxConcurrentOpenTrades != null
        ? `${snapshot.activeTradeCount}/${snapshot.maxConcurrentOpenTrades}`
        : '--');
    tradingSummaryText('trade-queue-broker-open', snapshot.brokerOpenTradeCount ?? '--');
    tradingSummaryText('trade-queue-pending', snapshot.pendingSubmissionCount ?? '--');
    tradingSummaryText('trade-queue-burst', snapshot.queueConcurrentRequestLimit ?? '--');
    tradingSummaryText('trade-queue-available', snapshot.availableDispatchSlots ?? '--');
    tradingSummaryText('trade-queue-queued', snapshot.queuedCount ?? '--');
    tradingSummaryText('trade-queue-processing', snapshot.processingCount ?? '--');

    const tbody = $('trade-queue-body');
    if (!tbody) return;

    const entries = snapshot.entries || [];
    if (entries.length === 0) {
        tbody.innerHTML = '<tr><td colspan="10" class="text-center" style="color:var(--text)">No active queue entries</td></tr>';
        return;
    }

    tbody.innerHTML = entries.map(entry => `
        <tr>
            <td style="font-family:monospace;font-size:.72rem">#${entry.queueEntryId}</td>
            <td style="font-family:monospace;font-size:.7rem;color:var(--text)">${escapeHtml(entry.signalId || '--')}</td>
            <td><span class="badge badge-neutral" style="font-size:.68rem">${escapeHtml(entry.sourceType || '--')}</span></td>
            <td><span class="badge ${executionStatusBadgeClass(entry.status)}" style="font-size:.68rem">${escapeHtml(entry.status || '--')}</span></td>
            <td>${escapeHtml(entry.requestedBy || '--')}</td>
            <td>${entry.createdAtUtc ? toAmmanShort(entry.createdAtUtc) : '--'}</td>
            <td>${entry.updatedAtUtc ? toAmmanShort(entry.updatedAtUtc) : '--'}</td>
            <td>${entry.processedAtUtc ? toAmmanShort(entry.processedAtUtc) : '--'}</td>
            <td>${formatElapsedSeconds(entry.ageSeconds)}</td>
            <td>${formatElapsedSeconds(entry.waitSeconds)}</td>
        </tr>
    `).join('');
}

async function loadTradeQueue() {
    const data = await fetchJson('/api/trading/queue?limit=50');
    if (!data) return;
    tradeQueueData = data;
    renderTradeQueue();
}

function renderExecutedTrades() {
    const tbody = $('executed-trades-body');
    if (!tbody) return;

    tbody.innerHTML = '';
    for (const trade of executedTradesData) {
        const directionCls = dirClass(trade.direction);
        const statusCls = executionStatusBadgeClass(trade.status);
        const pnl = trade.pnl != null ? `${trade.pnl >= 0 ? '+' : ''}${fmt(trade.pnl, 2)}` : '--';
        const pnlCls = trade.pnl > 0 ? 'buy' : trade.pnl < 0 ? 'sell' : '';
        const canForceClose = tradeStatusAllowsForceClose(trade.status) && !!trade.executedTradeId;

        const tr = document.createElement('tr');
        tr.innerHTML = `
            <td style="font-size:.7rem;font-family:monospace;color:var(--text)">${escapeHtml(trade.signalId || '--')}</td>
            <td><span class="badge badge-neutral" style="font-size:.68rem">${escapeHtml(trade.sourceType || '--')}</span></td>
            <td>${escapeHtml(trade.instrument || trade.symbol || '--')}</td>
            <td class="${directionCls}">${escapeHtml(trade.direction || '--')}</td>
            <td><span class="badge badge-neutral" style="font-size:.68rem">${escapeHtml(trade.timeframe || '--')}</span></td>
            <td>${trade.recommendedEntryPrice != null ? fmt(trade.recommendedEntryPrice) : '--'}</td>
            <td>${trade.actualEntryPrice != null ? fmt(trade.actualEntryPrice) : '--'}</td>
            <td class="buy">${trade.tpPrice != null ? fmt(trade.tpPrice) : '--'}</td>
            <td class="sell">${trade.slPrice != null ? fmt(trade.slPrice) : '--'}</td>
            <td>${trade.executedSize != null && Number(trade.executedSize) > 0 ? fmt(trade.executedSize, 4) : (trade.requestedSize != null ? fmt(trade.requestedSize, 4) : '--')}</td>
            <td><span class="badge ${statusCls}" style="font-size:.72rem">${escapeHtml(trade.status || '--')}</span></td>
            <td>${trade.openedAtUtc ? toAmmanShort(trade.openedAtUtc) : '--'}</td>
            <td>${trade.closedAtUtc ? toAmmanShort(trade.closedAtUtc) : '--'}</td>
            <td class="${pnlCls}">${pnl}</td>
            <td title="${escapeHtml(trade.accountId || '--')}">${escapeHtml(trade.accountName || '--')}</td>
            <td><span class="badge ${trade.isDemo ? 'badge-win' : 'badge-loss'}" style="font-size:.68rem">${trade.isDemo ? 'DEMO' : 'LIVE'}</span></td>
            <td>${escapeHtml(trade.accountCurrency || '--')}</td>
            <td style="font-size:.7rem;font-family:monospace;color:var(--text)">${escapeHtml(trade.dealReference || '--')}</td>
            <td title="${escapeHtml(trade.errorDetails || trade.failureReason || '--')}" style="max-width:220px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis">${escapeHtml(trade.failureReason || '--')}</td>
            <td>${canForceClose
                ? `<button class="btn btn-sm btn-outline-danger" onclick="forceCloseExecutedTrade(${trade.executedTradeId})">Force Close</button>`
                : '<span style="color:var(--text-sub)">--</span>'}</td>`;
        tbody.appendChild(tr);
    }

    if (executedTradesData.length === 0) {
        tbody.innerHTML = '<tr><td colspan="20" class="text-center" style="color:var(--text)">No executed trades found</td></tr>';
    }

    const totalPages = Math.max(1, Math.ceil(executedTradesTotal / EXECUTED_TRADES_PAGE_SIZE));
    $('exec-page-info').textContent = `Page ${executedTradesCurrentPage} of ${totalPages} (${executedTradesTotal} trades)`;
    $('exec-prev-page').disabled = executedTradesCurrentPage <= 1;
    $('exec-next-page').disabled = executedTradesCurrentPage >= totalPages;
}

async function loadExecutedTrades() {
    const data = await fetchJson(`/api/executed-trades?${buildExecutedTradesQuery().toString()}`);
    if (!data) return;
    executedTradesData = data.trades || [];
    executedTradesTotal = data.total || 0;
    renderExecutedTrades();
}

function onExecutedTradeFilterChange() {
    executedTradesCurrentPage = 1;
    loadExecutedTrades().catch(() => { });
}

function changeExecutedTradePage(delta) {
    executedTradesCurrentPage = Math.max(1, executedTradesCurrentPage + delta);
    loadExecutedTrades().catch(() => { });
}

async function forceCloseExecutedTrade(id) {
    if (!confirm(`Force close executed trade #${id}?`)) return;
    try {
        const response = await fetch(`/api/executed-trades/${id}/force-close`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ requestedBy: 'dashboard', reason: 'Manual force-close from dashboard' })
        });
        const payload = await response.json().catch(() => null);
        if (!response.ok) {
            alert(payload?.message || payload?.error || `Force close failed (${response.status})`);
            return;
        }
        await Promise.all([loadExecutedTrades(), refreshTradingSummary(), loadTradeQueue()]);
    } catch (e) {
        alert(`Force close failed: ${e.message}`);
    }
}

async function resetExecutedTradesSection() {
    if (!confirm('Reset Executed Signals / Trades? This clears executed trades, queue entries, attempts, events, account snapshots, and close actions only.')) {
        return;
    }

    try {
        const response = await fetch('/api/executed-trades/reset', {
            method: 'POST'
        });
        const payload = await response.json().catch(() => null);
        if (!response.ok) {
            alert(payload?.message || payload?.error || `Reset failed (${response.status})`);
            return;
        }

        executedTradesData = [];
        executedTradesTotal = 0;
        tradeQueueData = null;
        renderExecutedTrades();
        renderTradeQueue();
        await Promise.all([loadExecutedTrades(), refreshTradingSummary(), loadTradeQueue()]);
    } catch (e) {
        alert(`Reset failed: ${e.message}`);
    }
}

// Update expiry timers every second
setInterval(() => {
    const now = Date.now();
    document.querySelectorAll('.expiry-timer[data-expiry]').forEach(el => {
        const expiry = parseInt(el.dataset.expiry);
        const remaining = expiry - now;
        el.textContent = formatCountdown(remaining);
        if (remaining <= 0) {
            el.classList.add('expired');
            el.removeAttribute('data-expiry');
            el.textContent = 'Expired';
        }
    });
}, 1000);

// ─── Charts ────────────────────────────────────────────
function toUnix(isoStr) { return Math.floor(new Date(isoStr).getTime() / 1000); }

function createChartInstance(containerId) {
    const container = $(containerId);
    const chart = LightweightCharts.createChart(container, {
        width: container.clientWidth, height: 310,
        layout: { background: { color: '#161b22' }, textColor: '#c9d1d9' },
        grid: { vertLines: { color: '#21262d' }, horzLines: { color: '#21262d' } },
        crosshair: { mode: LightweightCharts.CrosshairMode.Normal },
        timeScale: { timeVisible: true, secondsVisible: false }
    });
    const candleSeries = chart.addCandlestickSeries({
        upColor: '#3fb950', downColor: '#f85149',
        borderUpColor: '#3fb950', borderDownColor: '#f85149',
        wickUpColor: '#3fb950', wickDownColor: '#f85149'
    });
    const ema20Series = chart.addLineSeries({ color: '#58a6ff', lineWidth: 1, title: 'EMA20' });
    const ema50Series = chart.addLineSeries({ color: '#d29922', lineWidth: 1, title: 'EMA50' });
    const vwapSeries = chart.addLineSeries({ color: '#bc8cff', lineWidth: 1, lineStyle: 2, title: 'VWAP' });
    new ResizeObserver(() => chart.applyOptions({ width: container.clientWidth })).observe(container);
    return { chart, candleSeries, ema20Series, ema50Series, vwapSeries };
}

let chart1m = null;
let chart5m = null;
let chart15m = null;

function ensureCharts() {
    try {
        if (!chart1m && $('chart-1m')) chart1m = createChartInstance('chart-1m');
        if (!chart5m && $('chart-5m')) chart5m = createChartInstance('chart-5m');
        if (!chart15m && $('chart-15m')) chart15m = createChartInstance('chart-15m');
    } catch (e) { console.warn('Chart init error:', e); }
}

async function refreshChart(tf, chartObj) {
    if (!chartObj) return;
    const [candles, indicators] = await Promise.all([
        fetchJson(`/api/candles?timeframe=${tf}&limit=200`),
        fetchJson(`/api/indicators/history?timeframe=${tf}&limit=200`)
    ]);
    if (!candles || candles.length === 0) return;

    const dedup = new Map();
    for (const c of candles) {
        if (!c || Number(c.midClose) <= 0) continue;
        dedup.set(toUnix(c.openTime), {
            time: toUnix(c.openTime),
            open: c.midOpen,
            high: c.midHigh,
            low: c.midLow,
            close: c.midClose
        });
    }
    const bars = Array.from(dedup.values()).sort((a, b) => a.time - b.time);
    if (bars.length === 0) return;

    const lastBar = bars[bars.length - 1];
    if (!chartObj._initialized) {
        chartObj.candleSeries.setData(bars);
        chartObj._initialized = true;
    } else if (chartObj._lastBarTime === lastBar.time) {
        chartObj.candleSeries.update(lastBar);
    } else {
        chartObj.candleSeries.setData(bars);
    }
    chartObj._lastBarTime = lastBar.time;

    if (indicators?.length > 0) {
        chartObj.ema20Series.setData(indicators.map(i => ({ time: toUnix(i.candleOpenTimeUtc), value: i.ema20 })));
        chartObj.ema50Series.setData(indicators.map(i => ({ time: toUnix(i.candleOpenTimeUtc), value: i.ema50 })));
        chartObj.vwapSeries.setData(indicators.map(i => ({ time: toUnix(i.candleOpenTimeUtc), value: i.vwap })));
    }
}

async function refreshCharts() {
    ensureCharts();
    await Promise.all([
        refreshChart('1m', chart1m),
        refreshChart('5m', chart5m),
        refreshChart('15m', chart15m)
    ]);
}

async function refreshDecisionSummary() {
    const d = await fetchJson('/api/decisions/summary');
    if (!d) return;
    if ($('dec-total')) $('dec-total').textContent = d.totalDecisions ?? '--';
    if ($('dec-notrade')) $('dec-notrade').textContent = d.noTradeCount ?? '--';
    if ($('dec-generated')) $('dec-generated').textContent = (d.longCount ?? 0) + (d.shortCount ?? 0);
    if ($('dec-blocked')) $('dec-blocked').textContent = d.operationalBlockedCount ?? '--';
    if ($('dec-context')) {
        const parts = [];
        if (d.strategyNoTradeCount) parts.push(`Strategy: ${d.strategyNoTradeCount}`);
        if (d.contextNotReadyCount) parts.push(`Ctx: ${d.contextNotReadyCount}`);
        if (d.lastEvaluationTime) parts.push(`Last: ${toAmmanShort(d.lastEvaluationTime)}`);
        $('dec-context').textContent = parts.join(' · ') || '--';
    }
    const tbody = $('dec-reject-reasons');
    if (tbody && d.topRejectReasons && d.topRejectReasons.length > 0) {
        tbody.innerHTML = d.topRejectReasons.map(r =>
            `<tr><td>${r.reason}</td><td class="text-end">${r.count}</td></tr>`
        ).join('');
    } else if (tbody) {
        tbody.innerHTML = '<tr><td colspan="2" class="text-center" style="color:var(--text)">No data</td></tr>';
    }
}

// ─── ML Trainer Status ─────────────────────────────────
async function refreshTrainerStatus() {
    const d = await fetchJson('/api/admin/ml/training/status');
    if (!d) return;

    // Progress bar
    const pct = Math.min(100, Math.round((d.labeledSamples / d.minSamplesRequired) * 100));
    const bar = $('trainer-progress-bar');
    if (bar) bar.style.width = pct + '%';
    const sc = $('trainer-sample-count');
    if (sc) sc.textContent = `${d.labeledSamples} / ${d.minSamplesRequired}`;

    // WIN / LOSS counts
    const wEl = $('trainer-wins');
    if (wEl) wEl.textContent = d.wins;
    const lEl = $('trainer-losses');
    if (lEl) lEl.textContent = d.losses;
    const warnEl = $('trainer-balance-warn');
    if (warnEl) warnEl.style.display =
        (d.wins < d.minSamplesPerClass || d.losses < d.minSamplesPerClass) ? '' : 'none';

    // Status badge
    const badge = $('trainer-status-badge');
    if (badge) {
        if (d.isRunning) {
            badge.textContent = `Running (${d.currentTrigger})`;
            badge.className = 'badge badge-pending';
        } else if (d.lastStatus === 'success') {
            badge.textContent = 'Last: Success';
            badge.className = 'badge badge-win';
        } else if (d.lastStatus === 'failed') {
            badge.textContent = 'Last: Failed';
            badge.className = 'badge badge-loss';
        } else if (d.lastStatus === 'skipped') {
            badge.textContent = 'Last: Skipped';
            badge.className = 'badge badge-expired';
        } else {
            badge.textContent = 'Idle';
            badge.className = 'badge badge-neutral';
        }
    }

    // Trigger button
    const btn = $('trainer-trigger-btn');
    if (btn) btn.disabled = d.isRunning;

    // Last run
    const lastEl = $('trainer-last-run');
    if (lastEl) {
        if (d.lastSuccessAt)
            lastEl.textContent = toAmmanShort(d.lastSuccessAt) + (d.lastDuration ? ` (${Math.round(d.lastDuration)}s)` : '');
        else
            lastEl.textContent = 'Never';
    }

    // Next scheduled
    const nextEl = $('trainer-next-run');
    if (nextEl)
        nextEl.textContent = d.nextScheduledAt ? toAmmanShort(d.nextScheduledAt) : 'Once ready';

    // Threshold info
    const thrEl = $('trainer-threshold-info');
    if (thrEl) thrEl.textContent = `${d.retrainThreshold} new outcomes OR ${d.retrainMaxDays}d elapsed`;

    // Recent runs table
    const tbody = $('trainer-runs-tbody');
    if (!tbody) return;
    if (!d.recentRuns || d.recentRuns.length === 0) {
        tbody.innerHTML = '<tr><td colspan="7" style="color:var(--text);text-align:center">No training runs yet</td></tr>';
        return;
    }
    tbody.innerHTML = d.recentRuns.map(r => {
        const statusCls = r.status === 'success' ? 'badge-win' : r.status === 'failed' ? 'badge-loss'
            : r.status === 'running' ? 'badge-pending' : 'badge-expired';
        const dur = r.durationSeconds != null ? r.durationSeconds + 's' : '--';
        const err = r.errorText ? `<span title="${r.errorText}" style="cursor:help;color:var(--red)">⚠ ${r.errorText.substring(0, 40)}${r.errorText.length > 40 ? '…' : ''}</span>` : '--';
        return `<tr>
            <td style="font-family:monospace">#${r.id}</td>
            <td>${r.trigger}</td>
            <td><span class="badge ${statusCls}" style="font-size:.68rem">${r.status}</span></td>
            <td>${r.sampleCount ?? '--'}</td>
            <td>${toAmmanShort(r.startedAtUtc)}</td>
            <td>${dur}</td>
            <td>${err}</td>
        </tr>`;
    }).join('');
}

async function triggerTraining() {
    const btn = $('trainer-trigger-btn');
    if (btn) btn.disabled = true;
    try {
        const resp = await fetch('/api/admin/ml/training/trigger', { method: 'POST' });
        if (resp.status === 409) {
            alert('Training already in progress.');
        } else if (resp.status === 202) {
            await refreshTrainerStatus();
        } else {
            alert('Failed to trigger training: ' + resp.status);
        }
    } catch (e) {
        alert('Error: ' + e.message);
    }
}

// ─── Refresh orchestration ─────────────────────────────
async function refreshAll() {
    try {
        await Promise.all([
            refreshQuote(), refreshIndicators(), refreshRegime(),
            refreshSignal(), refreshPerformance(), loadHistory(),
            loadExecutedTrades(), refreshTradingSummary(), loadTradeQueue(),
            refreshMlHealth(), refreshMlPerformance(), refreshMlPredictions(),
            refreshMlDiagnostics(), refreshMlFeatureSnapshot(), refreshDecisionSummary(), refreshTrainerStatus(), refreshHistorySync(),
            refreshAdaptiveStatus(), refreshActiveParameterSet(),
            loadMlModelRegistry(),
            updateRegimeSpecialistStatus()
        ]);
    } catch (e) { console.error('Refresh error:', e); }
    try { await refreshCharts(); } catch (e) { console.warn('Chart refresh error:', e); }
}

// Staggered intervals for different data
connectQuoteSse();
setInterval(refreshQuote, 200);
setInterval(() => { refreshIndicators(); refreshRegime(); refreshSignal(); }, 1000);
setInterval(() => { refreshCharts().catch(() => { }); }, 2000);
setInterval(() => { refreshHistorySync().catch(() => { }); }, 5000);
setInterval(() => { refreshPerformance(); loadHistory(); loadExecutedTrades(); refreshTradingSummary(); loadTradeQueue(); refreshDecisionSummary(); }, 15000);
setInterval(() => { refreshMlHealth(); refreshMlPerformance(); refreshMlPredictions(); refreshMlDiagnostics(); refreshMlFeatureSnapshot(); refreshTrainerStatus(); }, 30000);
setInterval(() => { refreshAdaptiveStatus(); refreshActiveParameterSet(); }, 15000);
setInterval(() => { loadMlModelRegistry().catch(() => {}); updateRegimeSpecialistStatus().catch(() => {}); }, 60000);

// Initialize after DOM is ready
async function initDashboard() {
    await loadConfigThresholds();
    await refreshAll();
}
if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', () => initDashboard());
} else {
    initDashboard();
}

// ─── Truncate All ──────────────────────────────────────
async function truncateAll() {
    if (!confirm('WARNING: This will permanently delete ALL data.\n\nAre you sure?')) return;
    if (!confirm('FINAL CONFIRMATION: All data will be erased. Continue?')) return;
    try {
        const r = await fetch('/api/db/truncate-all', { method: 'POST', headers: { 'X-Confirm-Truncate': 'YES' } });
        const data = await r.json();
        if (r.ok) { alert(`Done. ${data.tables} tables truncated.`); location.reload(); }
        else alert('Error: ' + (data.error || 'Unknown'));
    } catch (e) { alert('Failed: ' + e.message); }
}

async function restartService() {
    if (!confirm('This will stop the service, rebuild, and restart with the latest code changes.\n\nContinue?')) return;
    const btn = document.getElementById('btn-restart');
    btn.disabled = true;
    btn.textContent = '⟳ Restarting…';
    try {
        // Call portal restart endpoint (portal handles Kill + Run)
        await fetch('http://localhost:5233/api/eth/restart', { method: 'POST' });
        btn.textContent = '⟳ Stopped — rebuilding…';
        // Wait a moment then redirect to portal waiting page
        setTimeout(() => {
            window.location.href = 'http://localhost:5233/await/eth';
        }, 2000);
    } catch (e) {
        alert('Restart failed: ' + e.message);
        btn.disabled = false;
        btn.textContent = '⟳ Update';
    }
}

// ─── ML System ─────────────────────────────────────────
let mlPredChart = null;

function mlGateBadge(status) {
    switch ((status || '').toUpperCase()) {
        case 'SIGNAL_LINKED':
            return '<span class="badge badge-buy" style="font-size:.7rem">PASS</span>';
        case 'ML_FILTERED':
            return '<span class="badge badge-sell" style="font-size:.7rem">FAIL</span>';
        case 'OPERATIONALLY_BLOCKED':
            return '<span class="badge badge-expired" style="font-size:.7rem">BLOCKED</span>';
        case 'NO_SIGNAL_EXPECTED':
            return '<span class="badge badge-neutral" style="font-size:.7rem">NO SIGNAL</span>';
        default:
            return '<span class="badge badge-neutral" style="font-size:.7rem">PENDING</span>';
    }
}

function formatMlLinkStatus(status) {
    if (!status) return 'pending-link';
    return status.toLowerCase().replaceAll('_', ' ');
}

async function refreshMlHealth() {
    const health = await fetchJson('/api/admin/ml/health');
    if (!health) return;

    // Mode label
    const modeEl = $('ml-current-mode');
    modeEl.textContent = health.mlMode || 'DISABLED';
    modeEl.style.color = health.mlMode === 'ACTIVE' ? 'var(--green)' :
        health.mlMode === 'SHADOW' ? 'var(--yellow)' : 'var(--text-sub)';

    // Highlight active mode button
    document.querySelectorAll('.ml-mode-btn').forEach(btn => {
        btn.classList.toggle('active-mode', btn.dataset.mode === health.mlMode);
    });

    // Model info
    $('ml-model-version').textContent = health.modelVersion || 'none';
    const loadedEl = $('ml-model-loaded');
    loadedEl.textContent = health.modelLoaded ? 'Loaded' : 'Not loaded';
    loadedEl.style.color = health.modelLoaded ? 'var(--green)' : 'var(--red)';

    // Drift metrics
    $('ml-auc').textContent = health.rollingAuc != null ? fmt(health.rollingAuc, 3) : '--';
    $('ml-brier').textContent = health.rollingBrier != null ? fmt(health.rollingBrier, 3) : '--';
    $('ml-window-size').textContent = health.windowSize ?? '--';

    const driftBadge = $('ml-drift-badge');
    const overallStatus = health.overallStatus || (health.driftDetected ? 'WARNING' : 'HEALTHY');
    if (overallStatus === 'CRITICAL') {
        driftBadge.textContent = `CRITICAL · ${health.healthReason || 'ML issue'}`;
        driftBadge.className = 'badge badge-sell';
    } else if (overallStatus === 'WARNING') {
        driftBadge.textContent = `WARNING · ${health.healthReason || 'Needs attention'}`;
        driftBadge.className = 'badge badge-expired';
    } else if (overallStatus === 'INSUFFICIENT_DATA') {
        driftBadge.textContent = 'INSUFFICIENT DATA';
        driftBadge.className = 'badge badge-neutral';
    } else if (health.driftDetected) {
        driftBadge.textContent = 'DRIFT DETECTED';
        driftBadge.className = 'badge badge-sell';
    } else {
        driftBadge.textContent = 'HEALTHY';
        driftBadge.className = 'badge badge-buy';
    }

    await refreshLatestCandidateHint();
}

async function refreshMlPerformance() {
    const perf = await fetchJson('/api/ml/performance');
    if (!perf) return;
    $('ml-actual-wr').textContent = perf.drift?.actualWinRate != null ? fmtPct(perf.drift.actualWinRate * 100) : '--';
    $('ml-pred-wr').textContent = perf.drift?.predictedMeanWin != null ? fmtPct(perf.drift.predictedMeanWin * 100) : '--';
}

async function refreshMlDiagnostics() {
    const data = await fetchJson('/api/admin/ml/diagnostics');
    if (!data) return;

    const overallStatus = data.overallStatus || 'INSUFFICIENT_DATA';
    const labelStatus = data.labelQuality?.status || 'INSUFFICIENT_DATA';
    const classBalanceStatus = data.classBalance?.status || 'INSUFFICIENT_DATA';
    const calibrationStatus = data.calibration?.status || 'INSUFFICIENT_DATA';
    const driftStatus = data.featureDrift?.status || 'INSUFFICIENT_DATA';
    const overallBadge = $('ml-diag-overall-badge');
    if (overallBadge) {
        overallBadge.textContent = overallStatus;
        overallBadge.className = `badge ${diagnosticBadgeClass(overallStatus)}`;
    }

    setDiagnosticTitle('ml-diag-label-title', labelStatus);
    setDiagnosticTitle('ml-diag-cal-title', calibrationStatus);
    setDiagnosticTitle('ml-diag-drift-title', driftStatus);

    setDiagnosticText('ml-diag-timeframe', data.timeframe || '--', diagnosticTextColor(overallStatus));
    setDiagnosticText(
        'ml-diag-labeled',
        data.classBalance?.labeledSamples ?? '--',
        sampleCountColor(data.classBalance?.labeledSamples, 200, 100)
    );
    setDiagnosticText(
        'ml-diag-winrate',
        data.classBalance?.winRate != null ? fmtPct(data.classBalance.winRate * 100) : '--',
        diagnosticTextColor(classBalanceStatus)
    );

    const gapEl = $('ml-diag-cal-gap');
    if (gapEl) {
        const gap = data.calibration?.calibrationGap;
        gapEl.textContent = gap != null ? `${gap >= 0 ? '+' : ''}${fmt(gap * 100, 1)}%` : '--';
        gapEl.style.color = calibrationGapColor(gap);
    }

    const psiEl = $('ml-diag-max-psi');
    if (psiEl) {
        psiEl.textContent = data.featureDrift?.maxPsi != null ? fmt(data.featureDrift.maxPsi, 3) : '--';
        psiEl.style.color = psiColor(data.featureDrift?.maxPsi);
    }

    setDiagnosticText('ml-diag-label-status', labelStatus || '--', diagnosticTextColor(labelStatus));
    setDiagnosticText(
        'ml-diag-outcome-mix',
        `${data.labelQuality?.pendingOutcomes ?? 0} / ${data.labelQuality?.expiredOutcomes ?? 0} / ${data.labelQuality?.ambiguousOutcomes ?? 0}`,
        diagnosticTextColor(labelStatus)
    );
    setDiagnosticText(
        'ml-diag-conflicts',
        data.labelQuality?.conflictingTpSlHits ?? '--',
        issueCountColor(data.labelQuality?.conflictingTpSlHits, 1, 1)
    );
    setDiagnosticText(
        'ml-diag-pnl-mismatch',
        data.labelQuality?.inconsistentPnlLabels ?? '--',
        issueCountColor(data.labelQuality?.inconsistentPnlLabels, 1, 1)
    );
    setDiagnosticText(
        'ml-diag-link-coverage',
        data.labelQuality?.linkCoveragePct != null ? `${fmt(data.labelQuality.linkCoveragePct, 1)}%` : '--',
        coverageColor(data.labelQuality?.linkCoveragePct)
    );
    {
        const stale = data.labelQuality?.stalePendingLinkSnapshots ?? 0;
        const total = data.labelQuality?.pendingLinkSnapshots ?? 0;
        const pendingColor = stale > 0 ? 'var(--red)' : total > 0 ? 'var(--yellow)' : 'var(--green)';
        setDiagnosticText('ml-diag-pending-links', `${total} (${stale} stale)`, pendingColor);
    }
    {
        const noSignal = data.labelQuality?.expectedNoSignalSnapshots ?? 0;
        const mlFiltered = data.labelQuality?.mlFilteredSnapshots ?? 0;
        const blocked = data.labelQuality?.operationallyBlockedSnapshots ?? 0;
        const terminalColor = mlFiltered > 0 ? 'var(--yellow)' : (noSignal + blocked) > 0 ? 'var(--blue)' : 'var(--green)';
        setDiagnosticText('ml-diag-terminal-evals', `${noSignal} / ${mlFiltered + blocked}`, terminalColor);
    }

    setDiagnosticText('ml-diag-cal-status', calibrationStatus || '--', diagnosticTextColor(calibrationStatus));
    {
        const sampleCount = data.calibration?.sampleCount ?? 0;
        const activeCount = data.calibration?.activeModelSampleCount ?? sampleCount;
        const modelTag = data.calibration?.usesActiveModelOnly === false
            ? `${activeCount} active / all recent`
            : (data.calibration?.modelVersion || 'active');
        const modelColor = data.calibration?.usesActiveModelOnly === false
            ? 'var(--yellow)'
            : sampleCountColor(sampleCount, 80, 20);
        setDiagnosticText('ml-diag-cal-model', `${sampleCount} / ${modelTag}`, modelColor);
    }
    {
        const pred = data.calibration?.predictedMeanWin;
        const actual = data.calibration?.actualWinRate;
        setDiagnosticText(
            'ml-diag-cal-rates',
            pred != null && actual != null ? `${fmtPct(pred * 100)} vs ${fmtPct(actual * 100)}` : '--',
            calibrationGapColor(data.calibration?.calibrationGap)
        );
    }
    {
        const brier = data.calibration?.brierScore != null ? fmt(data.calibration.brierScore, 3) : '--';
        const ece = data.model?.expectedCalibrationError != null ? fmt(data.model.expectedCalibrationError, 3) : '--';
        const ll = data.model?.logLoss != null ? fmt(data.model.logLoss, 3) : '--';
        setDiagnosticText('ml-diag-cal-metrics', `${brier} / ${ece} / ${ll}`, diagnosticTextColor(calibrationStatus));
    }
    {
        const passWr = data.calibration?.passWinRate;
        const failWr = data.calibration?.failWinRate;
        setDiagnosticText(
            'ml-diag-pass-fail',
            passWr != null && failWr != null ? `${fmtPct(passWr * 100)} vs ${fmtPct(failWr * 100)}` : '--',
            liftColor(data.calibration?.thresholdLift)
        );
    }
    {
        const lift = data.calibration?.thresholdLift;
        const passCount = data.calibration?.passCount ?? 0;
        const failCount = data.calibration?.failCount ?? 0;
        setDiagnosticText(
            'ml-diag-threshold-lift',
            lift != null ? `${lift >= 0 ? '+' : ''}${fmt(lift * 100, 1)} pts (${passCount}/${failCount})` : '--',
            liftColor(lift)
        );
    }

    setDiagnosticText('ml-diag-drift-status', driftStatus || '--', diagnosticTextColor(driftStatus));
    setDiagnosticText(
        'ml-diag-drift-counts',
        `${data.featureDrift?.trainingSampleCount ?? 0} / ${data.featureDrift?.liveSampleCount ?? 0}`,
        diagnosticTextColor(driftStatus)
    );
    {
        const avgPsi = data.featureDrift?.averagePsi != null ? fmt(data.featureDrift.averagePsi, 3) : '--';
        const avgShift = data.featureDrift?.averageMeanShiftSigma != null ? fmt(data.featureDrift.averageMeanShiftSigma, 2) : '--';
        setDiagnosticText('ml-diag-drift-summary', `${avgPsi} / ${avgShift}`, diagnosticTextColor(driftStatus));
    }

    const noteEl = $('ml-diag-practical-note');
    if (noteEl) {
        noteEl.textContent = buildMlPracticalNote(data);
        noteEl.style.color = diagnosticTextColor(overallStatus) === 'var(--text-sub)' ? 'var(--text-main)' : diagnosticTextColor(overallStatus);
        noteEl.style.background = diagnosticPanelColor(overallStatus);
        noteEl.style.borderColor = diagnosticBorderColor(overallStatus);
    }

    const driftTbody = $('ml-diag-drift-tbody');
    if (!driftTbody) return;
    const topFeatures = Array.isArray(data.featureDrift?.topFeatures) ? data.featureDrift.topFeatures : [];
    if (topFeatures.length === 0) {
        driftTbody.innerHTML = '<tr><td colspan="4" style="color:var(--text-dim);text-align:center">No drift samples yet</td></tr>';
        return;
    }

    driftTbody.innerHTML = topFeatures.map(item => `
        <tr>
            <td style="color:${psiColor(item.psi)};font-weight:700">${escapeHtml(item.feature)}</td>
            <td style="color:var(--text-main)">${fmt(item.trainingMean, 2)}</td>
            <td style="color:var(--blue)">${fmt(item.liveMean, 2)}</td>
            <td style="color:${psiColor(item.psi)};font-weight:700">${fmt(item.psi, 3)}</td>
        </tr>`).join('');
}

async function refreshMlFeatureSnapshot() {
    const data = await fetchJson('/api/ml/features/latest');

    const versionBadge = $('ml-feature-version-badge');
    const linkStatusEl = $('ml-feature-link-status');
    const timestampEl = $('ml-feature-timestamp');
    const noteEl = $('ml-feature-note');

    if (!data) {
        if (versionBadge) {
            versionBadge.textContent = '--';
            versionBadge.className = 'badge badge-neutral';
        }
        if (linkStatusEl) linkStatusEl.textContent = '--';
        if (timestampEl) timestampEl.textContent = '--';
        if (noteEl) {
            noteEl.textContent = 'No ML feature snapshot is available yet for the current dashboard timeframe.';
            noteEl.style.color = 'var(--text-main)';
            noteEl.style.background = diagnosticPanelColor('INSUFFICIENT_DATA');
            noteEl.style.borderColor = diagnosticBorderColor('INSUFFICIENT_DATA');
        }

        renderMarketContextGroup('ml-market-structure-rows', MARKET_STRUCTURE_FIELDS, {});
        renderMarketContextGroup('ml-volatility-regime-rows', VOLATILITY_REGIME_FIELDS, {});
        renderMarketContextGroup('ml-signal-saturation-rows', SIGNAL_SATURATION_FIELDS, {});
        renderMarketContextGroup('ml-btc-context-rows', BTC_CONTEXT_FIELDS, {});
        return;
    }

    const isCurrentContract = data.featureVersion === data.currentFeatureVersion
        && Number(data.availableFeatureCount) >= Number(data.currentFeatureCount);

    if (versionBadge) {
        versionBadge.textContent = `${data.featureVersion || '--'} · ${data.availableFeatureCount || 0}`;
        versionBadge.className = `badge ${isCurrentContract ? 'badge-win' : 'badge-expired'}`;
    }
    if (linkStatusEl) linkStatusEl.textContent = formatMlLinkStatus(data.linkStatus);
    if (timestampEl) timestampEl.textContent = toAmmanShort(data.timestampUtc);
    if (noteEl) {
        noteEl.textContent = isCurrentContract
            ? 'This is the latest persisted runtime feature snapshot, including the expanded market-context groups.'
            : 'The latest snapshot is not on the full current feature contract yet, or the stored row is missing part of the expanded market-context set.';
        noteEl.style.color = isCurrentContract ? 'var(--green)' : 'var(--yellow)';
        noteEl.style.background = isCurrentContract ? diagnosticPanelColor('HEALTHY') : diagnosticPanelColor('WARNING');
        noteEl.style.borderColor = isCurrentContract ? diagnosticBorderColor('HEALTHY') : diagnosticBorderColor('WARNING');
    }

    renderMarketContextGroup('ml-market-structure-rows', MARKET_STRUCTURE_FIELDS, data.marketStructure || {});
    renderMarketContextGroup('ml-volatility-regime-rows', VOLATILITY_REGIME_FIELDS, data.volatilityRegime || {});
    renderMarketContextGroup('ml-signal-saturation-rows', SIGNAL_SATURATION_FIELDS, data.signalSaturation || {});
    renderMarketContextGroup('ml-btc-context-rows', BTC_CONTEXT_FIELDS, data.btcContext || {});
}

async function refreshMlPredictions() {
    const selectedHours = Number($('ml-pred-hours')?.value || 24);
    const showAll = $('ml-pred-show-all')?.checked === true;
    const requestedScope = showAll ? 'all' : 'linked';

    async function fetchPredictions(hours, scope) {
        return await fetchJson(`/api/ml/predictions/history?hours=${hours}&limit=200&scope=${scope}`);
    }

    let effectiveHours = selectedHours;
    let effectiveScope = requestedScope;
    let preds = await fetchPredictions(effectiveHours, effectiveScope);

    // If the selected window is too narrow, auto-expand so recent table is not empty.
    if (Array.isArray(preds) && preds.length === 0 && effectiveHours < 72) {
        effectiveHours = 72;
        preds = await fetchPredictions(effectiveHours, effectiveScope);
    }
    if (Array.isArray(preds) && preds.length === 0 && effectiveHours < 720) {
        effectiveHours = 720;
        preds = await fetchPredictions(effectiveHours, effectiveScope);
    }

    // Shadow-mode evaluations often end as NO_SIGNAL_EXPECTED, so the linked-only
    // view can look empty even while inference is healthy. Fall back to all rows
    // when the linked query is empty and the user did not explicitly opt into "all".
    if (!showAll && Array.isArray(preds) && preds.length === 0) {
        effectiveHours = selectedHours;
        effectiveScope = 'all';
        preds = await fetchPredictions(effectiveHours, effectiveScope);

        if (Array.isArray(preds) && preds.length === 0 && effectiveHours < 72) {
            effectiveHours = 72;
            preds = await fetchPredictions(effectiveHours, effectiveScope);
        }
        if (Array.isArray(preds) && preds.length === 0 && effectiveHours < 720) {
            effectiveHours = 720;
            preds = await fetchPredictions(effectiveHours, effectiveScope);
        }
    }

    const hoursSelect = $('ml-pred-hours');
    if (hoursSelect && String(effectiveHours) !== String(selectedHours)) {
        const hasOption = Array.from(hoursSelect.options).some(o => Number(o.value) === effectiveHours);
        if (hasOption) hoursSelect.value = String(effectiveHours);
    }

    if (!preds || !Array.isArray(preds)) {
        $('ml-pred-count').textContent = '0 predictions';
        $('ml-pred-tbody').innerHTML = '<tr><td colspan="7" class="text-center" style="color:var(--text)">No ML predictions yet</td></tr>';
        updateMlPredChart([]);
        return;
    }

    const scopeLabel = effectiveScope === 'all' ? 'evaluations' : 'linked predictions';
    const fallbackLabel = !showAll && effectiveScope === 'all' ? ' · showing all evaluations' : '';
    $('ml-pred-count').textContent = `${preds.length} ${scopeLabel} (${effectiveHours}h)${fallbackLabel}`;

    if (preds.length === 0) {
        $('ml-pred-tbody').innerHTML = '<tr><td colspan="7" class="text-center" style="color:var(--text)">No ML predictions in the selected window</td></tr>';
        updateMlPredChart([]);
        return;
    }

    // Table
    const tbody = $('ml-pred-tbody');
    tbody.innerHTML = '';
    const sorted = [...preds].sort((a, b) => new Date(b.createdAtUtc || b.predictionTimeUtc || b.createdUtc) - new Date(a.createdAtUtc || a.predictionTimeUtc || a.createdUtc));

    for (const p of sorted.slice(0, 50)) {
        const rawProb = p.rawWinProbability ?? p.predictedWinProbability ?? p.winProbability ?? 0;
        const calProb = p.calibratedWinProbability ?? p.predictedWinProbability ?? p.winProbability ?? rawProb;
        const conf = p.predictionConfidence ?? p.calibratedConfidence ?? p.confidence ?? 0;
        const threshold = p.recommendedThreshold ?? '--';
        const pctWidth = Math.min(calProb * 100, 100);
        const barColor = calProb >= 0.6 ? 'var(--green)' : calProb >= 0.5 ? 'var(--yellow)' : 'var(--red)';
        const linkStatus = p.linkStatus || (p.signalId ? 'SIGNAL_LINKED' : 'PENDING');
        const signalId = p.signalId || null;
        const signalCell = signalId
            ? `<span title="${signalId}" style="font-family:monospace">${signalId.substring(0, 8)}...</span>`
            : `<span style="color:var(--text-sub)">${formatMlLinkStatus(linkStatus)}</span>`;

        const tr = document.createElement('tr');
        tr.innerHTML = `
            <td>${toAmmanShort(p.createdAtUtc || p.predictionTimeUtc || p.createdUtc)}</td>
            <td>${signalCell}</td>
            <td style="color:${barColor};font-weight:600" title="Raw / calibrated win probability">${(rawProb * 100).toFixed(1)}% / ${(calProb * 100).toFixed(1)}%</td>
            <td>${fmt(conf, 0)}</td>
            <td>${threshold}</td>
            <td>${mlGateBadge(linkStatus)}</td>
            <td><div class="prob-bar-bg"><div class="prob-bar" style="width:${pctWidth}%;background:${barColor}"></div></div></td>`;
        tbody.appendChild(tr);
    }

    updateMlPredChart(sorted);
}

function updateMlPredChart(predictions) {
    const canvas = $('ml-pred-chart');
    if (!canvas) return;

    const labels = predictions.map(p => {
        const t = new Date(p.createdAtUtc || p.predictionTimeUtc || p.createdUtc);
        return t.toLocaleString('en-US', { timeZone: AMMAN_TZ, hour: '2-digit', minute: '2-digit', hour12: false });
    }).reverse();

    const winProbs = predictions.map(p => ((p.calibratedWinProbability ?? p.predictedWinProbability ?? p.winProbability ?? 0) * 100)).reverse();

    if (mlPredChart) mlPredChart.destroy();

    mlPredChart = new Chart(canvas, {
        type: 'line',
        data: {
            labels,
            datasets: [{
                label: 'Win Probability %',
                data: winProbs,
                borderColor: '#58a6ff',
                backgroundColor: 'rgba(88,166,255,0.1)',
                fill: true,
                tension: 0.3,
                pointRadius: 2,
                borderWidth: 1.5
            }, {
                label: '50% Threshold',
                data: labels.map(() => 50),
                borderColor: 'rgba(248,81,73,0.5)',
                borderDash: [4, 4],
                borderWidth: 1,
                pointRadius: 0,
                fill: false
            }]
        },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            plugins: {
                legend: { display: true, labels: { color: '#9eaab6', font: { size: 10 } } }
            },
            scales: {
                x: {
                    ticks: { color: '#9eaab6', font: { size: 9 }, maxTicksLimit: 10, maxRotation: 0 },
                    grid: { color: '#21262d' }
                },
                y: {
                    min: 0, max: 100,
                    ticks: { color: '#9eaab6', font: { size: 9 }, callback: v => v + '%' },
                    grid: { color: '#21262d' }
                }
            }
        }
    });
}

async function switchMlMode(mode) {
    if (!confirm(`Switch ML mode to ${mode}?`)) return;
    try {
        const r = await fetch('/api/admin/ml/mode', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ mode })
        });
        const data = await r.json();
        if (r.ok) {
            refreshMlHealth();
        } else {
            alert('Error: ' + (data.error || 'Unknown'));
        }
    } catch (e) {
        alert('Failed: ' + e.message);
    }
}

// ─── Regime Specialist Training ────────────────────────

const REGIME_SCOPES = ['BEARISH', 'BULLISH', 'NEUTRAL'];

/**
 * Called by the "Train & Activate Regime Models" button.
 * Kicks off the full training pipeline (which includes regime steps 7-9),
 * then polls until it finishes — models are auto-promoted by the server.
 */
async function trainAndActivateRegimeModels() {
    const btn = $('ml-regime-train-btn');
    const progress = $('ml-regime-train-progress');

    if (btn) { btn.disabled = true; btn.textContent = 'Starting…'; }
    if (progress) { progress.style.display = 'block'; progress.textContent = 'Triggering training pipeline…'; }

    try {
        const resp = await fetch('/api/admin/ml/training/trigger', { method: 'POST' });
        const body = await resp.json().catch(() => null);

        if (resp.status === 409) {
            if (progress) progress.textContent = 'Training already in progress — polling for completion…';
        } else if (resp.status === 202) {
            if (progress) progress.textContent = 'Pipeline started — training global + regime models…';
        } else if (resp.status === 403) {
            alert('This action is loopback-only. Open the dashboard on localhost.');
            return;
        } else {
            alert('Failed to trigger training: ' + (body?.error || resp.status));
            return;
        }

        // Poll training status until it finishes
        await pollTrainingUntilDone(progress);

        // Reload everything once done
        await Promise.all([loadMlModelRegistry(), refreshMlHealth(), refreshLatestCandidateHint()]);
        await updateRegimeSpecialistStatus();

    } catch (e) {
        alert('Error: ' + e.message);
    } finally {
        if (btn) { btn.disabled = false; btn.textContent = 'Train & Activate Regime Models'; }
        // Keep progress visible for 10s then hide
        setTimeout(() => { if (progress) progress.style.display = 'none'; }, 10000);
    }
}

async function pollTrainingUntilDone(progressEl) {
    const MAX_POLLS = 120;  // 10 minutes max (5s intervals)
    for (let i = 0; i < MAX_POLLS; i++) {
        await new Promise(r => setTimeout(r, 5000));
        try {
            const r = await fetch('/api/admin/ml/training/status');
            if (!r.ok) continue;
            const s = await r.json();

            if (s.isRunning) {
                const elapsed = s.runStartedAt
                    ? Math.round((Date.now() - new Date(s.runStartedAt).getTime()) / 1000)
                    : '?';
                if (progressEl) progressEl.textContent = `Training in progress… ${elapsed}s elapsed`;
            } else {
                const status = String(s.lastStatus || '').toLowerCase();
                if (progressEl) {
                    if (status === 'success') {
                        progressEl.style.color = 'var(--green)';
                        progressEl.textContent = '✓ Training complete — regime specialists activated automatically.';
                    } else if (status === 'skipped') {
                        progressEl.style.color = 'var(--yellow)';
                        progressEl.textContent = '⚠ Skipped — insufficient labeled data (need 200+ global, 50+ per regime).';
                    } else {
                        progressEl.style.color = 'var(--red)';
                        progressEl.textContent = `✗ Training ended: ${status || 'unknown'}`;
                    }
                }
                return;
            }
        } catch { /* keep polling */ }
    }
    if (progressEl) progressEl.textContent = 'Timed out polling — check trainer status below.';
}

/**
 * Reads the model registry and updates the regime status mini-panel
 * in the ML Mode card (● ACTIVE / ○ none per scope).
 */
async function updateRegimeSpecialistStatus() {
    const el = $('ml-regime-status');
    if (!el) return;

    try {
        const resp = await fetch('/api/admin/ml/models');
        if (!resp.ok) { el.textContent = 'unavailable'; return; }
        const models = await resp.json();
        if (!Array.isArray(models)) { el.textContent = 'unavailable'; return; }

        const activeByScope = {};
        for (const m of models) {
            const scope = String(m.regimeScope || 'ALL').toUpperCase();
            const status = String(m.status || '').toUpperCase();
            if (status === 'ACTIVE' && scope !== 'ALL') {
                activeByScope[scope] = m.modelVersion || `#${m.id}`;
            }
        }

        el.innerHTML = REGIME_SCOPES.map(scope => {
            const ver = activeByScope[scope];
            const dot = ver ? '●' : '○';
            const color = ver ? 'var(--green)' : 'var(--text-sub)';
            const label = ver ? `<span style="color:var(--text-sub)">${escapeHtml(ver.slice(-8))}</span>` : 'not trained';
            return `<span style="color:${color}">${dot} ${scope}</span> ${label}<br>`;
        }).join('');
    } catch {
        el.textContent = 'unavailable';
    }
}

// ─── ML Models Registry ────────────────────────────────

const SCOPE_COLORS = { ALL: 'var(--blue)', BULLISH: 'var(--green)', BEARISH: 'var(--red)', NEUTRAL: 'var(--yellow)' };
const STATUS_BADGE = {
    ACTIVE:    '<span class="badge badge-win">ACTIVE</span>',
    CANDIDATE: '<span class="badge badge-neutral">CANDIDATE</span>',
    SHADOW:    '<span class="badge" style="background:var(--bg-header);color:var(--yellow);border:1px solid var(--yellow)">SHADOW</span>',
    RETIRED:   '<span class="badge badge-loss">RETIRED</span>',
    TRAINING:  '<span class="badge badge-neutral">TRAINING</span>',
};

async function loadMlModelRegistry() {
    const tbody = $('ml-registry-tbody');
    const countEl = $('ml-registry-count');
    if (!tbody) return;

    // Ensure ML mode is known before rendering — fetch if not yet set
    if (_currentMlMode === null) {
        try {
            const pr = await fetch('/api/admin/parameter-sets/active');
            if (pr.ok) {
                const pd = await pr.json();
                const mlModeMap = { 0: 'DISABLED', 1: 'SHADOW', 2: 'ACTIVE' };
                _currentMlMode = mlModeMap[(pd.parameters || pd)?.mlMode] || null;
            }
        } catch { /* non-fatal — banner will just be hidden */ }
    }

    try {
        const resp = await fetch('/api/admin/ml/models');
        if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
        const models = await resp.json();

        if (!Array.isArray(models) || models.length === 0) {
            tbody.innerHTML = '<tr><td colspan="10" style="color:var(--text-dim);text-align:center">No models registered</td></tr>';
            if (countEl) countEl.textContent = '0 models';
            return;
        }

        if (countEl) countEl.textContent = `${models.length} model${models.length !== 1 ? 's' : ''}`;

        // Mode banner — shows current ML Mode so "active" status in the table is not confused with trading mode
        const modeBar = $('ml-registry-mode-bar');
        if (modeBar) {
            if (_currentMlMode === 'ACTIVE') {
                modeBar.style.display = 'block';
                modeBar.style.background = 'rgba(63,185,80,.1)';
                modeBar.innerHTML = '<span style="color:var(--green)">● ML Mode: ACTIVE</span> — loaded models are gating live trades';
            } else if (_currentMlMode === 'SHADOW') {
                modeBar.style.display = 'block';
                modeBar.style.background = 'rgba(227,179,65,.07)';
                modeBar.innerHTML = '<span style="color:var(--yellow)">● ML Mode: SHADOW</span> — loaded models run inference only, not gating trades. "Active" status = loaded for inference.';
            } else if (_currentMlMode === 'DISABLED') {
                modeBar.style.display = 'block';
                modeBar.style.background = 'rgba(248,81,73,.07)';
                modeBar.innerHTML = '<span style="color:var(--text-dim)">○ ML Mode: DISABLED</span> — inference is off. Models remain loaded but are not called.';
            } else {
                modeBar.style.display = 'none';
            }
        }

        // Sort: ALL scope first, then by regime, then by ID desc within scope
        const scopeOrder = { ALL: 0, BULLISH: 1, BEARISH: 2, NEUTRAL: 3 };
        models.sort((a, b) => {
            const sa = scopeOrder[String(a.regimeScope || 'ALL').toUpperCase()] ?? 9;
            const sb = scopeOrder[String(b.regimeScope || 'ALL').toUpperCase()] ?? 9;
            if (sa !== sb) return sa - sb;
            return (Number(b.id) || 0) - (Number(a.id) || 0);
        });

        tbody.innerHTML = models.map(m => {
            const scope = String(m.regimeScope || 'ALL').toUpperCase();
            const status = String(m.status || '').toUpperCase();
            const scopeColor = SCOPE_COLORS[scope] || 'var(--text)';
            const statusBadge = STATUS_BADGE[status] || `<span class="badge badge-neutral">${status}</span>`;
            const auc = m.aucRoc != null ? Number(m.aucRoc).toFixed(4) : '—';
            const brier = m.brierScore != null ? Number(m.brierScore).toFixed(4) : '—';
            const ece = m.expectedCalibrationError != null && Number(m.expectedCalibrationError) > 0
                ? Number(m.expectedCalibrationError).toFixed(4) : '—';
            const samples = m.trainingSampleCount ?? '—';
            const activated = m.activatedAtUtc ? toAmmanShort(m.activatedAtUtc) : '—';
            const version = m.modelVersion || `#${m.id}`;

            let actionCell = '—';
            if (status === 'CANDIDATE' || status === 'SHADOW') {
                actionCell = `<button class="btn btn-sm btn-outline-success" style="font-size:.7rem;padding:1px 8px"
                    onclick="activateModelById(${m.id}, '${escapeHtml(version)}')" id="ml-reg-activate-${m.id}">
                    Activate
                </button>`;
            } else if (status === 'ACTIVE') {
                if (_currentMlMode === 'ACTIVE') {
                    actionCell = '<span style="color:var(--green);font-size:.72rem">● Running</span>';
                } else if (_currentMlMode === 'SHADOW') {
                    actionCell = '<span style="color:var(--yellow);font-size:.72rem">● Shadow</span>';
                } else if (_currentMlMode === 'DISABLED') {
                    actionCell = '<span style="color:var(--text-dim);font-size:.72rem">○ Loaded</span>';
                } else {
                    actionCell = '<span style="color:var(--text-dim);font-size:.72rem">● Loaded</span>';
                }
            }

            const rowStyle = status === 'ACTIVE' ? 'background:rgba(63,185,80,.08)' :
                             status === 'RETIRED' ? 'opacity:.5' : '';

            return `<tr style="${rowStyle}">
                <td style="font-family:monospace">${m.id}</td>
                <td style="color:${scopeColor};font-weight:600">${scope}</td>
                <td style="font-family:monospace;font-size:.72rem">${escapeHtml(version)}</td>
                <td>${statusBadge}</td>
                <td style="color:${Number(m.aucRoc)>=0.62?'var(--green)':Number(m.aucRoc)>=0.58?'var(--yellow)':'var(--red)'}">${auc}</td>
                <td style="color:${Number(m.brierScore)<=0.26?'var(--green)':Number(m.brierScore)<=0.30?'var(--yellow)':'var(--red)'}">${brier}</td>
                <td>${ece}</td>
                <td>${samples}</td>
                <td style="font-size:.7rem;color:var(--text-sub)">${activated}</td>
                <td>${actionCell}</td>
            </tr>`;
        }).join('');

    } catch (e) {
        tbody.innerHTML = `<tr><td colspan="10" style="color:var(--red)">Failed to load models: ${escapeHtml(e.message)}</td></tr>`;
    }
}

async function activateModelById(id, version) {
    if (!confirm(`Activate model ${version} (id=${id})?`)) return;

    const btn = document.getElementById(`ml-reg-activate-${id}`);
    if (btn) { btn.disabled = true; btn.textContent = 'Activating…'; }

    try {
        const r = await fetch(`/api/admin/ml/models/${id}/activate`, { method: 'POST' });
        const body = await r.json().catch(() => null);

        if (r.ok) {
            const msg = body?.message || `Model ${version} activated.`;
            // Show inline success instead of an alert to keep UX smooth
            if (btn) {
                btn.className = 'btn btn-sm btn-success';
                btn.style.cssText = 'font-size:.7rem;padding:1px 8px';
                btn.textContent = '✓ Done';
                setTimeout(() => { btn.disabled = false; }, 2000);
            }
            // Refresh affected panels
            await Promise.all([
                loadMlModelRegistry(),
                refreshMlHealth(),
                refreshMlPerformance(),
                refreshLatestCandidateHint(),
            ]);
            return;
        }

        if (r.status === 403) {
            alert('Activation is loopback-only. Open this dashboard on localhost.');
            return;
        }

        const errorMsg = body?.error || body?.message || `HTTP ${r.status}`;
        alert('Activation failed: ' + errorMsg);
    } catch (e) {
        alert('Activation error: ' + e.message);
    } finally {
        if (btn && btn.textContent === 'Activating…') {
            btn.disabled = false;
            btn.textContent = 'Activate';
        }
    }
}

async function activateLatestCandidate() {
    if (!confirm('Activate latest candidate model now?')) return;

    const btn = $('ml-activate-latest-btn');
    const prevText = btn?.textContent;
    if (btn) {
        btn.disabled = true;
        btn.textContent = 'Resolving...';
    }

    try {
        const latest = await getLatestCandidateModel();
        if (!latest?.id) {
            alert('No candidate/shadow model available to activate.');
            return;
        }

        if (btn) btn.textContent = 'Activating...';

        const r = await fetch(`/api/admin/ml/models/${latest.id}/activate`, { method: 'POST' });
        const body = await r.json().catch(() => null);

        if (r.ok) {
            const msg = body?.message || `Model ${latest.modelVersion || latest.id} activated.`;
            alert(msg);
            await refreshMlHealth();
            await refreshMlPerformance();
            await refreshMlPredictions();
            await refreshLatestCandidateHint();
            return;
        }

        if (r.status === 403) {
            alert('Activation API is loopback-only. Open this dashboard on localhost.');
            return;
        }

        const errorMsg = body?.error || body?.message || `HTTP ${r.status}`;
        alert('Activation failed: ' + errorMsg);
    } catch (e) {
        alert('Activation error: ' + e.message);
    } finally {
        if (btn) {
            btn.disabled = false;
            btn.textContent = prevText || 'Activate Latest Candidate';
        }
    }
}

async function getLatestCandidateModel() {
    const modelsResp = await fetch('/api/admin/ml/models');
    const models = await modelsResp.json().catch(() => null);
    if (!modelsResp.ok || !Array.isArray(models)) return null;

    const candidates = models
        .filter(m => {
            const status = String(m?.status || '').toUpperCase();
            return status === 'CANDIDATE' || status === 'SHADOW';
        })
        .sort((a, b) => (Number(b?.id) || 0) - (Number(a?.id) || 0));

    return candidates[0] || null;
}

async function refreshLatestCandidateHint() {
    const hint = $('ml-latest-candidate-hint');
    const btn = $('ml-activate-latest-btn');
    if (!hint) return;

    try {
        const latest = await getLatestCandidateModel();
        if (!latest?.id) {
            hint.textContent = 'Latest: none';
            hint.style.color = 'var(--text-sub)';
            if (btn) btn.disabled = true;
            return;
        }

        const version = latest.modelVersion || 'unknown';
        hint.textContent = 'Latest: ';
        const link = document.createElement('a');
        link.href = `/api/admin/ml/models/${latest.id}`;
        link.target = '_blank';
        link.rel = 'noopener noreferrer';
        link.style.color = 'var(--blue)';
        link.textContent = `#${latest.id} (${version})`;
        hint.appendChild(link);
        hint.style.color = 'var(--text-sub)';
        if (btn) btn.disabled = false;
    } catch {
        hint.textContent = 'Latest: unavailable';
        hint.style.color = 'var(--yellow)';
        if (btn) btn.disabled = false;
    }
}

// ─── Adaptive Strategy Status ──────────────────────────
async function refreshAdaptiveStatus() {
    try {
        const r = await fetch('/api/admin/adaptive/status');
        if (!r.ok) throw new Error(r.status);
        const d = await r.json();

        const badge = $('adaptive-status-badge');
        if (badge) {
            badge.textContent = d.enabled ? 'ENABLED' : 'DISABLED';
            badge.className = 'badge ' + (d.enabled ? 'badge-win' : 'badge-loss');
        }

        const setText = (id, val) => { const el = $(id); if (el) el.textContent = val; };
        setText('adaptive-intensity', fmt(d.intensity, 2));
        setText('adaptive-intensity-override', d.intensityOverride != null ? fmt(d.intensityOverride, 2) : 'none');
        setText('adaptive-retro-enabled', d.retrospectiveEnabled ? 'ON' : 'OFF');
        setText('adaptive-current-condition', d.currentCondition || 'none');
        setText('adaptive-tracked-count', d.trackedConditionCount);
        setText('adaptive-retro-count', d.retrospectiveOverlayCount);

        const timeframeBody = $('adaptive-timeframes-body');
        const profiles = d.timeframeProfiles || [];
        if (timeframeBody) {
            if (profiles.length === 0) {
                timeframeBody.innerHTML = '<tr><td colspan="5" style="color:var(--text-dim);text-align:center">No timeframe adaptive setups have been persisted yet</td></tr>';
            } else {
                timeframeBody.innerHTML = profiles
                    .sort((a, b) => timeframeSort(a.timeframe) - timeframeSort(b.timeframe))
                    .map(p => {
                        const condition = p.currentConditionClass || 'none';
                        const strategy = `${escapeHtml(p.strategyVersion)} · ${escapeHtml(p.profileBucket)}`;
                        const parameters = `C ${p.confidenceBuyThreshold}/${p.confidenceSellThreshold} · ATR ${fmt(p.minAtrThreshold, 2)} · SL ${fmt(p.stopAtrMultiplier, 2)}x · TP ${fmt(p.targetRMultiple, 2)}R · ML ${(Number(p.mlMinWinProbability || 0) * 100).toFixed(0)}%`;
                        const hash = escapeHtml((p.effectiveParameterHash || '').substring(0, 8));
                        const retro = p.hasRetrospectiveOverlay ? ' · retro' : '';
                        return `<tr>
                            <td style="color:var(--text);font-weight:600">${escapeHtml(p.timeframe)}</td>
                            <td style="color:var(--text)">${strategy}</td>
                            <td style="color:var(--text-sub)">
                                <div>${escapeHtml(parameters)}</div>
                                <div style="font-size:.65rem;color:var(--text-dim)">hash ${hash}${retro} · intensity ${fmt(p.effectiveIntensity, 2)}</div>
                            </td>
                            <td style="font-family:monospace;color:var(--text)">${escapeHtml(condition)}</td>
                            <td style="color:var(--text-dim)">${toAmmanShort(p.lastChangedUtc)}</td>
                        </tr>`;
                    }).join('');
            }
        }

        const tbody = $('adaptive-conditions-body');
        const conditions = d.conditionDetails || [];
        if (tbody) {
            if (conditions.length === 0) {
                tbody.innerHTML = '<tr><td colspan="6" style="color:var(--text-dim);text-align:center">No conditions tracked yet — outcomes accumulate as live signals resolve</td></tr>';
            } else {
                const totalOutcomes = conditions.reduce((s, c) => s + c.outcomeCount, 0);
                tbody.innerHTML = conditions
                    .sort((a, b) => {
                        const tf = timeframeSort(a.timeframe) - timeframeSort(b.timeframe);
                        return tf !== 0 ? tf : b.outcomeCount - a.outcomeCount;
                    })
                    .map(c => {
                        const lowData = c.outcomeCount < 5;
                        const wrColor = lowData ? 'var(--text-dim)' : c.winRate >= 0.55 ? 'var(--green)' : c.winRate >= 0.45 ? 'var(--yellow)' : 'var(--red)';
                        const expColor = lowData ? 'var(--text-dim)' : c.expectancy >= 0.15 ? 'var(--green)' : c.expectancy >= 0 ? 'var(--yellow)' : 'var(--red)';
                        const lowBadge = lowData ? ' <span style="font-size:.6rem;color:var(--yellow);opacity:.7">(low data)</span>' : '';
                        return `<tr>
                            <td style="color:var(--text);font-weight:600">${escapeHtml(c.timeframe)}</td>
                            <td style="font-family:monospace;font-size:.7rem;color:var(--text)">${escapeHtml(c.conditionKey)}${lowBadge}</td>
                            <td style="color:var(--text)">${c.outcomeCount}</td>
                            <td style="color:${wrColor}">${(c.winRate * 100).toFixed(1)}%</td>
                            <td style="color:${expColor}">${Number(c.expectancy).toFixed(3)}R</td>
                            <td>${c.hasRetrospectiveOverlay ? '<span style="color:var(--purple)">●</span>' : '<span style="color:var(--text-dim)">–</span>'}</td>
                        </tr>`;
                    }).join('')
                    + `<tr><td colspan="6" style="font-size:.65rem;color:var(--text-dim);text-align:right;padding-top:.3rem">${totalOutcomes} live outcomes tracked across ${conditions.length} timeframe conditions</td></tr>`;
            }
        }

        const changesBody = $('adaptive-changes-body');
        if (changesBody) {
            const changes = d.recentChanges || [];
            if (changes.length === 0) {
                changesBody.innerHTML = '<tr><td colspan="5" style="color:var(--text-dim);text-align:center">No adaptive setup changes recorded yet</td></tr>';
            } else {
                changesBody.innerHTML = changes
                    .map(c => {
                        const setup = `C ${c.confidenceBuyThreshold}/${c.confidenceSellThreshold} · SL ${fmt(c.stopAtrMultiplier, 2)}x · TP ${fmt(c.targetRMultiple, 2)}R`;
                        const hash = escapeHtml((c.currentParameterHash || '').substring(0, 8));
                        return `<tr>
                            <td style="color:var(--text-dim)">${toAmmanShort(c.changedAtUtc)}</td>
                            <td style="color:var(--text);font-weight:600">${escapeHtml(c.timeframe)}</td>
                            <td style="color:var(--text)">${escapeHtml(c.changeReason)}</td>
                            <td style="font-family:monospace;color:var(--text)">${escapeHtml(c.currentConditionClass || 'none')}</td>
                            <td style="color:var(--text-sub)">
                                <div>${escapeHtml(c.strategyVersion)} · ${escapeHtml(c.profileBucket)} · ${setup}</div>
                                <div style="font-size:.65rem;color:var(--text-dim)">hash ${hash}${c.hasRetrospectiveOverlay ? ' · retro' : ''}</div>
                            </td>
                        </tr>`;
                    }).join('');
            }
        }
    } catch (e) {
        console.warn('Adaptive status refresh error:', e);
    }
}

function timeframeSort(tf) {
    switch ((tf || '').toLowerCase()) {
        case '1m': return 1;
        case '5m': return 5;
        case '15m': return 15;
        case '30m': return 30;
        case '1h': return 60;
        case '4h': return 240;
        default: return 9999;
    }
}

// ─── Active Parameter Set ──────────────────────────────
async function refreshActiveParameterSet() {
    try {
        const r = await fetch('/api/admin/parameter-sets/overview');
        if (!r.ok) throw new Error(r.status);
        const d = await r.json();
        const base = d.baseSet || d;

        const setText = (id, val) => { const el = $(id); if (el) el.textContent = val; };
        const setHtml = (id, val) => { const el = $(id); if (el) el.innerHTML = val; };

        setText('ps-id', '#' + base.id);
        setText('ps-version', base.strategyVersion);
        setText('ps-hash', base.parameterHash);
        setText('ps-created-by', base.createdBy || '--');
        setText('ps-activated', toAmmanShort(d.baseActivatedUtc || base.activatedUtc));
        setText('ps-runtime-changed', d.latestAdaptiveChangeUtc ? `${toAmmanShort(d.latestAdaptiveChangeUtc)}${d.latestAdaptiveChangeTimeframe ? ` · ${d.latestAdaptiveChangeTimeframe}` : ''}` : '—');
        setText('ps-timeframe-count', d.timeframeSetupCount ?? 0);
        setText('param-set-meta', `Base #${base.id} · ${base.strategyVersion} · ${base.parameterHash?.substring(0, 8)}`);

        const notesEl = $('ps-notes');
        if (notesEl) {
            const noteParts = [];
            if (base.notes) noteParts.push(base.notes);
            if (d.primaryAdaptiveCondition) noteParts.push(`Primary adaptive condition: ${d.primaryAdaptiveCondition}`);
            noteParts.push(`Adaptive runtime: ${d.adaptiveEnabled ? 'enabled' : 'disabled'}`);
            notesEl.textContent = noteParts.join(' · ');
        }

        const p = base.parameters || {};

        // Signal thresholds
        setText('ps-conf-buy', p.confidenceBuyThreshold ?? '--');
        setText('ps-conf-sell', p.confidenceSellThreshold ?? '--');
        setText('ps-pullback', p.pullbackZonePct != null ? (Number(p.pullbackZonePct) * 100).toFixed(2) + '%' : '--');
        setText('ps-min-atr', fmt(p.minAtrThreshold, 2));
        setText('ps-adx', p.adxTrendThreshold ?? '--');

        // ML config
        const mlModeMap = { 0: 'DISABLED', 1: 'SHADOW', 2: 'ACTIVE' };
        _currentMlMode = mlModeMap[p.mlMode] || null;
        setText('ps-ml-mode', _currentMlMode || p.mlMode || '--');
        setText('ps-acc-first', p.mlAccuracyFirstMode ? 'ON' : 'OFF');
        setText('ps-min-win-prob', p.mlAccuracyFirstMinWinProbability != null ? (Number(p.mlAccuracyFirstMinWinProbability) * 100).toFixed(0) + '%' : '--');

        const policyMap = { 0: 'BlockAll', 1: 'AllowHigh', 2: 'AllowAll' };
        setText('ps-neutral-policy', policyMap[p.neutralRegimePolicy] || p.neutralRegimePolicy || '--');

        // Adaptive settings
        setText('ps-adaptive-enabled', p.adaptiveParametersEnabled ? 'ON' : 'OFF');
        setText('ps-adaptive-retro', p.adaptiveRetrospectiveEnabled ? 'ON' : 'OFF');
        setText('ps-adaptive-intensity', fmt(p.adaptiveOverlayIntensity, 2));
        setText('ps-adaptive-min-outcomes', p.adaptiveRetrospectiveMinOutcomes ?? '--');
        setText('ps-adaptive-window', p.adaptiveRetrospectiveWindowSize ?? '--');

        const tfBody = $('ps-timeframes-body');
        const setups = d.timeframeSetups || [];
        if (tfBody) {
            if (setups.length === 0) {
                tfBody.innerHTML = '<tr><td colspan="4" style="color:var(--text-dim);text-align:center">No timeframe runtime setups have been persisted yet</td></tr>';
            } else {
                tfBody.innerHTML = setups
                    .sort((a, b) => timeframeSort(a.timeframe) - timeframeSort(b.timeframe))
                    .map(setup => {
                        const strategy = `${escapeHtml(setup.strategyVersion)} · ${escapeHtml(setup.profileBucket)}`;
                        const runtime = `C ${setup.confidenceBuyThreshold}/${setup.confidenceSellThreshold} · SL ${fmt(setup.stopAtrMultiplier, 2)}x · TP ${fmt(setup.targetRMultiple, 2)}R · ${escapeHtml((setup.effectiveParameterHash || '').substring(0, 8))}`;
                        return `<tr>
                            <td style="color:var(--text);font-weight:600">${escapeHtml(setup.timeframe)}</td>
                            <td style="color:var(--text)">${strategy}</td>
                            <td style="color:var(--text-sub)">${runtime}</td>
                            <td style="color:var(--text-dim)">${toAmmanShort(setup.lastChangedUtc)}</td>
                        </tr>`;
                    }).join('');
            }
        }
    } catch (e) {
        console.warn('Active parameter set refresh error:', e);
    }
}
