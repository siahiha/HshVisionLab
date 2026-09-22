using System.Net;
using System.Reflection;

namespace HshDetectionService;

internal static class ServiceHealthPage
{
    public static string Render()
    {
        string version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(4) ?? "unknown";
        return Template.Replace("__VERSION__", WebUtility.HtmlEncode(version), StringComparison.Ordinal);
    }

    private const string Template = """
<!doctype html>
<html lang="fa" dir="rtl">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>HSH Detection Service — API Health</title>
  <style>
    :root { color-scheme: dark; font-family: Tahoma, Segoe UI, sans-serif; background: #07111f; color: #e8f0fb; }
    * { box-sizing: border-box; }
    body { margin: 0; min-height: 100vh; background: radial-gradient(circle at 85% 0%, #17365c 0, #07111f 38rem); }
    main { width: min(1040px, calc(100% - 28px)); margin: 0 auto; padding: 24px 0 38px; }
    header { display: flex; gap: 16px; align-items: flex-start; justify-content: space-between; margin-bottom: 16px; }
    h1, h2, p { margin: 0; }
    h1 { font-size: clamp(23px, 3vw, 31px); letter-spacing: -.5px; }
    h2 { font-size: 15px; margin-bottom: 11px; }
    .muted { color: #91a4bb; line-height: 1.7; font-size: 13px; }
    .version { color: #9bb9d8; font-size: 12px; margin-top: 5px; direction: ltr; text-align: right; }
    button { border: 1px solid #345b82; background: #102b49; color: #e8f0fb; border-radius: 9px; padding: 8px 12px; cursor: pointer; font: inherit; font-size: 12px; }
    button:hover { background: #174064; }
    .overall { display: flex; align-items: center; gap: 9px; padding: 10px 12px; border: 1px solid #274463; border-radius: 11px; background: #0d2036; min-width: 205px; font-size: 12px; }
    .dot { width: 9px; height: 9px; border-radius: 50%; background: #91a4bb; box-shadow: 0 0 0 3px #91a4bb22; }
    .dot.ok, .badge.ok { background: #16b878; color: #d9fff0; }
    .dot.warn, .badge.warn { background: #d59d31; color: #fff0c4; }
    .dot.fail, .badge.fail { background: #d55363; color: #ffe1e5; }
    .panel { border: 1px solid #203a57; border-radius: 13px; background: #0a1a2cdd; padding: 14px; margin-top: 12px; box-shadow: 0 12px 38px #0000001c; }
    .cards { display: grid; grid-template-columns: repeat(auto-fit, minmax(190px, 1fr)); gap: 8px; }
    .card { border: 1px solid #264563; border-radius: 10px; padding: 11px; background: #0d2138; min-height: 91px; }
    .card-top { display: flex; justify-content: space-between; gap: 8px; align-items: center; }
    .card strong { font-size: 13px; }
    .badge { border-radius: 999px; padding: 3px 7px; font-size: 10px; font-weight: 700; }
    .card-detail { color: #a9bad0; font-size: 11px; line-height: 1.7; margin-top: 8px; word-break: break-word; }
    .card-time { color: #718aa5; direction: ltr; font-size: 10px; margin-top: 4px; }
    .engine-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(205px, 1fr)); gap: 8px; }
    .engine-card { border: 1px solid #264563; border-radius: 10px; padding: 10px 11px; background: #0d2138; }
    .engine-head { display: flex; align-items: center; gap: 8px; }
    .engine-dot { width: 8px; height: 8px; border-radius: 50%; background: #d55363; flex: 0 0 auto; }
    .engine-dot.ok { background: #16b878; }
    .engine-dot.warn { background: #d59d31; }
    .engine-name { font-size: 12px; font-weight: 700; }
    .engine-detail { color: #91a4bb; font-size: 10px; line-height: 1.7; margin-top: 6px; }
    table { width: 100%; border-collapse: collapse; font-size: 12px; }
    th, td { text-align: right; padding: 8px 6px; border-bottom: 1px solid #1b3550; }
    th { color: #88a7c6; font-weight: 600; }
    td { color: #d8e5f3; }
    .state { direction: ltr; display: inline-block; }
    .footer { color: #718aa5; font-size: 11px; margin-top: 12px; }
    @media (max-width: 650px) { header { display: block; } .overall { margin-top: 14px; } main { width: min(100% - 18px, 1040px); padding-top: 18px; } .panel { padding: 11px; } }
  </style>
</head>
<body>
  <main>
    <header>
      <div>
        <h1>وضعیت سرویس تشخیص</h1>
        <p class="muted">صفحه سلامت API، ارتباطات و pipeline پردازش</p>
        <div class="version">Version __VERSION__</div>
      </div>
      <div class="overall"><span id="overall-dot" class="dot"></span><strong id="overall-text">در حال بررسی...</strong></div>
    </header>

    <section class="panel">
      <h2>اجزای سرویس</h2>
      <div id="checks" class="cards"></div>
    </section>

    <section id="cameras-panel" class="panel" hidden>
      <h2>وضعیت دوربین‌ها و WebStream</h2>
      <div id="cameras"></div>
    </section>

    <section id="engines-panel" class="panel" hidden>
      <h2>موتورهای پردازش</h2>
      <div id="engines" class="engine-grid"></div>
    </section>

    <button id="refresh" type="button">بررسی مجدد</button>
    <div id="updated" class="footer"></div>
  </main>
  <script>
    const checkDefinitions = [
      ['api', 'API / Live'],
      ['ready', 'Readiness / License'],
      ['service', 'Service Runtime'],
      ['engines', 'Processing Engines'],
      ['stream', 'WebStream / Camera Pipeline'],
      ['signalr', 'SignalR / WebSocket']
    ];
    const checks = new Map();
    const checkElements = new Map();
    const engineElements = new Map();
    const esc = (value) => String(value ?? '').replace(/[&<>'"]/g, (char) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' }[char]));
    const textStatus = (kind) => kind === 'ok' ? 'سالم' : kind === 'warn' ? 'هشدار' : kind === 'fail' ? 'قطع' : 'در حال بررسی';
    function ensureCheckCard(id, label) {
      if (checkElements.has(id)) return checkElements.get(id);
      const card = document.createElement('article');
      card.className = 'card';
      card.innerHTML = `<div class="card-top"><strong>${esc(label)}</strong><span class="badge" data-role="status">در حال بررسی</span></div><div class="card-detail" data-role="detail">در حال بررسی...</div><div class="card-time" data-role="time"></div>`;
      document.getElementById('checks').appendChild(card);
      const elements = { card, status: card.querySelector('[data-role="status"]'), detail: card.querySelector('[data-role="detail"]'), time: card.querySelector('[data-role="time"]') };
      checkElements.set(id, elements);
      return elements;
    }
    function updateOverall() {
      const values = [...checks.values()];
      const overall = values.some((item) => item.kind === 'fail') ? 'fail' : values.some((item) => item.kind === 'warn') ? 'warn' : values.length && values.every((item) => item.kind === 'ok') ? 'ok' : '';
      const dot = document.getElementById('overall-dot');
      dot.className = `dot ${overall}`;
      document.getElementById('overall-text').textContent = overall === 'ok' ? 'همه اجزا سالم هستند' : overall === 'warn' ? 'سرویس فعال است، اما نیاز به بررسی دارد' : overall === 'fail' ? 'یک یا چند جزء در دسترس نیست' : 'در حال بررسی...';
    }
    function setCheck(id, label, kind, detail, elapsed) {
      checks.set(id, { label, kind, detail, elapsed });
      const elements = ensureCheckCard(id, label);
      elements.status.className = `badge ${kind}`;
      elements.status.textContent = textStatus(kind);
      elements.detail.textContent = detail;
      elements.time.textContent = elapsed == null ? '' : `${elapsed} ms`;
      updateOverall();
    }
    function resetChecks() {
      for (const [id, label] of checkDefinitions) setCheck(id, label, '', 'در حال بررسی...', null);
    }
    function renderEngines(modules) {
      const panel = document.getElementById('engines-panel');
      panel.hidden = !modules.length;
      const active = new Set();
      for (const module of modules) {
        const key = String(module.type || module.displayName || Math.random());
        active.add(key);
        let elements = engineElements.get(key);
        if (!elements) {
          const card = document.createElement('article');
          card.className = 'engine-card';
          card.innerHTML = '<div class="engine-head"><span class="engine-dot" data-role="dot"></span><span class="engine-name" data-role="name"></span></div><div class="engine-detail" data-role="detail"></div>';
          document.getElementById('engines').appendChild(card);
          elements = { card, dot: card.querySelector('[data-role="dot"]'), name: card.querySelector('[data-role="name"]'), detail: card.querySelector('[data-role="detail"]') };
          engineElements.set(key, elements);
        }
        const available = module.available !== false;
        elements.card.hidden = false;
        elements.dot.className = `engine-dot ${available ? 'ok' : 'warn'}`;
        elements.name.textContent = module.displayName || module.type || 'موتور بدون نام';
        elements.detail.textContent = available ? `${module.kind || 'engine'} — آماده` : (module.availabilityMessage || 'در دسترس نیست');
      }
      for (const [key, elements] of engineElements) if (!active.has(key)) elements.card.hidden = true;
    }
    async function jsonProbe(id, label, path, onSuccess) {
      const started = performance.now();
      try {
        const response = await fetch(`${path}${path.includes('?') ? '&' : '?'}ts=${Date.now()}`, { cache: 'no-store' });
        const payload = await response.json().catch(() => ({}));
        const elapsed = Math.round(performance.now() - started);
        if (!response.ok) { setCheck(id, label, 'fail', `${response.status} — ${payload.error || payload.status || 'پاسخ ناموفق'}`, elapsed); return null; }
        onSuccess?.(payload, elapsed);
        return payload;
      } catch (error) {
        setCheck(id, label, 'fail', error?.message || 'خطای اتصال', Math.round(performance.now() - started));
        return null;
      }
    }
    function probeSignalR() {
      return new Promise((resolve) => {
        const started = performance.now();
        const scheme = location.protocol === 'https:' ? 'wss' : 'ws';
        let settled = false;
        const finish = (kind, detail) => { if (settled) return; settled = true; clearTimeout(timer); try { socket.close(); } catch {} setCheck('signalr', 'SignalR / WebSocket', kind, detail, Math.round(performance.now() - started)); resolve(); };
        const timer = setTimeout(() => finish('fail', 'مهلت اتصال WebSocket تمام شد'), 3000);
        let socket;
        try {
          socket = new WebSocket(`${scheme}://${location.host}/hubs/detections`);
          socket.onopen = () => socket.send('{"protocol":"json","version":1}\u001e');
          socket.onmessage = (event) => { const message = String(event.data).replace(/\u001e/g, '').trim(); finish(message === '{}' || message.includes('"type":1') ? 'ok' : 'warn', message === '{}' ? 'اتصال و handshake موفق بود' : 'اتصال برقرار شد، پاسخ handshake غیرمنتظره بود'); };
          socket.onerror = () => finish('fail', 'اتصال WebSocket برقرار نشد');
          socket.onclose = () => finish('fail', 'اتصال WebSocket بسته شد');
        } catch (error) { finish('fail', error?.message || 'خطای WebSocket'); }
      });
    }
    function renderCameras(cameras) {
      const panel = document.getElementById('cameras-panel');
      panel.hidden = !cameras.length;
      document.getElementById('cameras').innerHTML = cameras.length ? `<table><thead><tr><th>دوربین</th><th>Capture</th><th>وضعیت منبع</th><th>FPS</th><th>Pipeline</th></tr></thead><tbody>${cameras.map((camera) => `<tr><td>${esc(camera.name || camera.id)}</td><td><span class="state">${esc(camera.captureBackend || '—')}</span></td><td><span class="state">${esc(camera.sourceState || (camera.running ? 'Running' : 'Stopped'))}</span></td><td><span class="state">${esc(camera.fps ?? 0)}</span></td><td>${camera.activePipelineCount > 0 ? 'فعال' : 'غیرفعال'}</td></tr>`).join('')}</tbody></table>` : '';
    }
    async function probe() {
      resetChecks();
      const live = await jsonProbe('api', 'API / Live', '/health/live', (payload, elapsed) => setCheck('api', 'API / Live', 'ok', `پاسخ معتبر — ${payload.status || 'live'}`, elapsed));
      await jsonProbe('ready', 'Readiness / License', '/health/ready', (payload, elapsed) => setCheck('ready', 'Readiness / License', 'ok', payload.license || payload.status || 'آماده', elapsed));
      const service = await jsonProbe('service', 'Service Runtime', '/api/v1/service/status', (payload, elapsed) => {
        const license = payload.license?.isValid ? 'license معتبر' : 'license نامعتبر';
        setCheck('service', 'Service Runtime', payload.ready && payload.license?.isValid ? 'ok' : 'warn', `${payload.ready ? 'runtime آماده' : 'runtime آماده نیست'} — ${license}`, elapsed);
        const cameras = Array.isArray(payload.cameras) ? payload.cameras : [];
        renderCameras(cameras);
        const running = cameras.filter((camera) => camera.running || String(camera.sourceState).toLowerCase() === 'running').length;
        setCheck('stream', 'WebStream / Camera Pipeline', cameras.length === 0 ? 'warn' : running === cameras.length ? 'ok' : 'warn', cameras.length === 0 ? 'دوربینی پیکربندی نشده' : `${running} از ${cameras.length} دوربین در حال اجرا`, elapsed);
      });
      await jsonProbe('engines', 'Processing Engines', '/api/v1/service/capabilities', (payload, elapsed) => {
        const modules = Array.isArray(payload) ? payload : [];
        const available = modules.filter((module) => module.available !== false).length;
        renderEngines(modules);
        setCheck('engines', 'Processing Engines', modules.length && available === modules.length ? 'ok' : modules.length ? 'warn' : 'fail', modules.length ? `${available} از ${modules.length} موتور آماده` : 'موتوری گزارش نشد', elapsed);
      });
      await probeSignalR();
      if (!live && !service) setCheck('stream', 'WebStream / Camera Pipeline', 'fail', 'به API وضعیت دسترسی نیست');
      document.getElementById('updated').textContent = `آخرین بررسی: ${new Date().toLocaleString()}`;
    }
    document.getElementById('refresh').addEventListener('click', probe);
    probe();
    setInterval(probe, 10000);
  </script>
</body>
</html>
""";
}
