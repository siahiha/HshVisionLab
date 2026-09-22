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
    body { margin: 0; min-height: 100vh; background: radial-gradient(circle at 85% 0%, #17365c 0, #07111f 46rem); }
    main { width: min(1120px, calc(100% - 32px)); margin: 0 auto; padding: 36px 0 56px; }
    header { display: flex; gap: 20px; align-items: flex-start; justify-content: space-between; margin-bottom: 28px; }
    h1, h2, p { margin: 0; }
    h1 { font-size: clamp(24px, 4vw, 38px); letter-spacing: -.5px; }
    h2 { font-size: 17px; margin-bottom: 14px; }
    .muted { color: #91a4bb; line-height: 1.8; }
    .version { color: #9bb9d8; font-size: 13px; margin-top: 8px; direction: ltr; text-align: right; }
    button { border: 1px solid #345b82; background: #102b49; color: #e8f0fb; border-radius: 10px; padding: 10px 15px; cursor: pointer; font: inherit; }
    button:hover { background: #174064; }
    .overall { display: flex; align-items: center; gap: 10px; padding: 13px 16px; border: 1px solid #274463; border-radius: 13px; background: #0d2036; min-width: 220px; }
    .dot { width: 11px; height: 11px; border-radius: 50%; background: #91a4bb; box-shadow: 0 0 0 4px #91a4bb22; }
    .dot.ok, .badge.ok { background: #16b878; color: #d9fff0; }
    .dot.warn, .badge.warn { background: #d59d31; color: #fff0c4; }
    .dot.fail, .badge.fail { background: #d55363; color: #ffe1e5; }
    .panel { border: 1px solid #203a57; border-radius: 16px; background: #0a1a2cdd; padding: 20px; margin-top: 18px; box-shadow: 0 18px 60px #00000020; }
    .cards { display: grid; grid-template-columns: repeat(auto-fit, minmax(210px, 1fr)); gap: 12px; }
    .card { border: 1px solid #264563; border-radius: 13px; padding: 15px; background: #0d2138; min-height: 112px; }
    .card-top { display: flex; justify-content: space-between; gap: 8px; align-items: center; }
    .card strong { font-size: 15px; }
    .badge { border-radius: 999px; padding: 4px 8px; font-size: 11px; font-weight: 700; }
    .card-detail { color: #a9bad0; font-size: 12px; line-height: 1.8; margin-top: 12px; word-break: break-word; }
    .card-time { color: #718aa5; direction: ltr; font-size: 11px; margin-top: 5px; }
    table { width: 100%; border-collapse: collapse; font-size: 13px; }
    th, td { text-align: right; padding: 11px 8px; border-bottom: 1px solid #1b3550; }
    th { color: #88a7c6; font-weight: 600; }
    td { color: #d8e5f3; }
    .state { direction: ltr; display: inline-block; }
    .footer { color: #718aa5; font-size: 12px; margin-top: 18px; }
    @media (max-width: 650px) { header { display: block; } .overall { margin-top: 18px; } main { width: min(100% - 20px, 1120px); padding-top: 22px; } .panel { padding: 14px; } }
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

    <button id="refresh" type="button">بررسی مجدد</button>
    <div id="updated" class="footer"></div>
  </main>
  <script>
    const checks = new Map();
    const esc = (value) => String(value ?? '').replace(/[&<>'"]/g, (char) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' }[char]));
    const textStatus = (kind) => kind === 'ok' ? 'سالم' : kind === 'warn' ? 'هشدار' : kind === 'fail' ? 'قطع' : 'در حال بررسی';
    function setCheck(id, label, kind, detail, elapsed) { checks.set(id, { label, kind, detail, elapsed }); render(); }
    function render() {
      document.getElementById('checks').innerHTML = [...checks.values()].map((item) => `
        <article class="card">
          <div class="card-top"><strong>${esc(item.label)}</strong><span class="badge ${item.kind}">${textStatus(item.kind)}</span></div>
          <div class="card-detail">${esc(item.detail)}</div>
          ${item.elapsed == null ? '' : `<div class="card-time">${item.elapsed} ms</div>`}
        </article>`).join('');
      const values = [...checks.values()];
      const overall = values.some((item) => item.kind === 'fail') ? 'fail' : values.some((item) => item.kind === 'warn') ? 'warn' : values.length && values.every((item) => item.kind === 'ok') ? 'ok' : '';
      const dot = document.getElementById('overall-dot');
      dot.className = `dot ${overall}`;
      document.getElementById('overall-text').textContent = overall === 'ok' ? 'همه اجزا سالم هستند' : overall === 'warn' ? 'سرویس فعال است، اما نیاز به بررسی دارد' : overall === 'fail' ? 'یک یا چند جزء در دسترس نیست' : 'در حال بررسی...';
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
      checks.clear(); render();
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
