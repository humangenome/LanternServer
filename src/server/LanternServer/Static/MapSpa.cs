namespace LanternServer.Static;

/// <summary>
/// Embedded static SPA served at GET /map/. Vanilla HTML+JS+canvas with no
/// external dependencies — no Leaflet, no tile fetcher, no CDN — so the
/// dedicated server can hand this to a browser even when offline. The page
/// polls <c>GET /api/v1/map/state</c> every <see cref="PollIntervalMs"/>
/// and renders player dots on a flat dark backdrop.
///
/// Basemap capture (top-down screenshot of Augusta_Main) is a follow-on; until
/// it lands the page draws a gradient + grid so the dots have spatial
/// context.
/// </summary>
internal static class MapSpa
{
    public const int PollIntervalMs = 2000;

    /// <summary>
    /// Hand-tuned to look reasonable at the Grounded 2 starting area's
    /// world-unit extents (player movement typically spans ±5000 UE units
    /// / ±50m). SPA auto-scales beyond this if a player travels out of frame.
    /// </summary>
    public const double InitialHalfExtent = 8000.0;

    public static string Render()
    {
        // Inline the tuning constants into the JS so the SPA has no extra
        // bootstrap step. Keep this method static + cached if it ever
        // becomes hot — for now it's called once per /map/ GET, which is
        // an idle browser refresh frequency.
        return HtmlTemplate
            .Replace("__POLL_INTERVAL_MS__", PollIntervalMs.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("__INITIAL_HALF_EXTENT__", InitialHalfExtent.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
    }

    private const string HtmlTemplate = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Lantern — Live Map</title>
<style>
  html, body { height: 100%; margin: 0; background: #0b1620; color: #cfd8e3;
    font-family: -apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,Helvetica,Arial,sans-serif; }
  #wrap { position: relative; width: 100%; height: 100%; overflow: hidden; }
  canvas { position: absolute; inset: 0; width: 100%; height: 100%; display: block; }
  #hud { position: absolute; top: 8px; left: 12px; font-size: 13px;
    background: rgba(11,22,32,0.7); padding: 6px 10px; border-radius: 4px;
    pointer-events: none; }
  #hud .row { line-height: 1.5; }
  #hud .label { color: #6e8190; margin-right: 4px; }
  #hud .ok { color: #6ee7b7; }
  #hud .warn { color: #fbbf24; }
  #hud .err { color: #fb7185; }
  #list { position: absolute; top: 8px; right: 12px; font-size: 12px;
    background: rgba(11,22,32,0.7); padding: 6px 10px; border-radius: 4px;
    max-width: 220px; max-height: 60%; overflow-y: auto;
    pointer-events: none; }
  #list .p { display: flex; align-items: center; gap: 6px; margin: 2px 0; }
  #list .dot { width: 8px; height: 8px; border-radius: 50%; background: #6ee7b7; flex: 0 0 auto; }
  #list .name { color: #cfd8e3; }
  #list .coord { color: #6e8190; font-size: 10px; }
  #footer { position: absolute; bottom: 8px; right: 12px; font-size: 10px;
    color: #6e8190; }
  #footer a { color: #6e8190; text-decoration: none; }
  #footer a:hover { color: #cfd8e3; }
</style>
</head>
<body>
<div id="wrap">
  <canvas id="map"></canvas>
  <div id="hud">
    <div class="row"><span class="label">server</span><span id="hud-server">connecting…</span></div>
    <div class="row"><span class="label">players</span><span id="hud-count">—</span></div>
    <div class="row"><span class="label">world</span><span id="hud-world">—</span></div>
    <div class="row"><span class="label">status</span><span id="hud-status">—</span></div>
  </div>
  <div id="list"></div>
  <div id="footer">Lantern · <span id="lantern-version">—</span></div>
</div>
<script>
(function () {
  var INITIAL_HALF_EXTENT = __INITIAL_HALF_EXTENT__;
  var POLL_INTERVAL_MS    = __POLL_INTERVAL_MS__;
  var canvas = document.getElementById('map');
  var ctx = canvas.getContext('2d');
  var hudServer = document.getElementById('hud-server');
  var hudCount  = document.getElementById('hud-count');
  var hudWorld  = document.getElementById('hud-world');
  var hudStatus = document.getElementById('hud-status');
  var listEl    = document.getElementById('list');
  var versionEl = document.getElementById('lantern-version');

  var state = { players: [], world: { name: '' }, unix_ms: 0, stale: false };
  var lastError = null;
  var halfExtent = INITIAL_HALF_EXTENT;

  function fit(canvas) {
    var dpr = window.devicePixelRatio || 1;
    var w = canvas.clientWidth, h = canvas.clientHeight;
    if (canvas.width !== w * dpr || canvas.height !== h * dpr) {
      canvas.width  = Math.floor(w * dpr);
      canvas.height = Math.floor(h * dpr);
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    }
    return { w: w, h: h };
  }

  function autoScale(players) {
    var maxExtent = INITIAL_HALF_EXTENT;
    for (var i = 0; i < players.length; i++) {
      var p = players[i];
      var d = Math.max(Math.abs(p.x), Math.abs(p.y));
      if (d > maxExtent) maxExtent = d;
    }
    halfExtent = halfExtent * 0.9 + maxExtent * 1.2 * 0.1;
  }

  function draw() {
    var dims = fit(canvas);
    var w = dims.w, h = dims.h;
    var cx = w / 2, cy = h / 2;
    var scale = (Math.min(w, h) / 2) / halfExtent;

    var g = ctx.createRadialGradient(cx, cy, 0, cx, cy, Math.max(w, h) / 1.4);
    g.addColorStop(0, '#16263a');
    g.addColorStop(1, '#0b1620');
    ctx.fillStyle = g;
    ctx.fillRect(0, 0, w, h);

    ctx.strokeStyle = 'rgba(80,100,130,0.18)';
    ctx.lineWidth = 1;
    var step = 1000 * scale;
    while (step < 40) step *= 2;
    for (var x = (cx % step); x < w; x += step) {
      ctx.beginPath(); ctx.moveTo(x, 0); ctx.lineTo(x, h); ctx.stroke();
    }
    for (var y = (cy % step); y < h; y += step) {
      ctx.beginPath(); ctx.moveTo(0, y); ctx.lineTo(w, y); ctx.stroke();
    }

    ctx.fillStyle = 'rgba(180,200,230,0.4)';
    ctx.beginPath(); ctx.arc(cx, cy, 3, 0, Math.PI * 2); ctx.fill();

    for (var i = 0; i < state.players.length; i++) {
      var p = state.players[i];
      var px = cx + p.x * scale;
      var py = cy - p.y * scale;
      ctx.beginPath();
      ctx.arc(px, py, 7, 0, Math.PI * 2);
      ctx.fillStyle = state.stale ? '#94a3b8' : '#6ee7b7';
      ctx.fill();
      ctx.lineWidth = 2;
      ctx.strokeStyle = '#0b1620';
      ctx.stroke();

      ctx.fillStyle = '#e8edf3';
      ctx.font = '12px -apple-system,Segoe UI,Roboto,sans-serif';
      ctx.textAlign = 'left';
      ctx.fillText(p.name || 'player', px + 11, py + 4);
    }
  }

  function renderHud() {
    hudCount.textContent = state.players.length;
    hudWorld.textContent = (state.world && state.world.name) || '—';
    if (lastError) {
      hudStatus.innerHTML = '<span class="err">' + lastError + '</span>';
    } else if (state.stale) {
      hudStatus.innerHTML = '<span class="warn">stale</span>';
    } else if (state.unix_ms) {
      hudStatus.innerHTML = '<span class="ok">live</span>';
    } else {
      hudStatus.textContent = '—';
    }

    var rows = '';
    for (var i = 0; i < state.players.length; i++) {
      var p = state.players[i];
      rows += '<div class="p"><span class="dot"></span><span class="name">'
        + (p.name || 'player') + '</span> <span class="coord">'
        + Math.round(p.x) + ',' + Math.round(p.y) + '</span></div>';
    }
    listEl.innerHTML = rows;
  }

  function poll() {
    fetch('/api/v1/map/state', { cache: 'no-store' })
      .then(function (r) {
        if (r.status === 401) throw new Error('map is private — enable Map.Public');
        if (r.status === 404) throw new Error('map disabled');
        if (!r.ok) throw new Error('http ' + r.status);
        return r.json();
      })
      .then(function (data) {
        state = data;
        if (data.lantern_version) versionEl.textContent = data.lantern_version;
        if (data.server_name)    hudServer.textContent = data.server_name;
        else if (data.instance)  hudServer.textContent = data.instance;
        lastError = null;
        autoScale(state.players || []);
        draw();
        renderHud();
      })
      .catch(function (err) {
        lastError = (err && err.message) ? err.message : 'fetch failed';
        renderHud();
      })
      .then(function () { setTimeout(poll, POLL_INTERVAL_MS); });
  }

  window.addEventListener('resize', draw);
  fit(canvas);
  renderHud();
  poll();
})();
</script>
</body>
</html>
""";
}
