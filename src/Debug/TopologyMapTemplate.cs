#if DEBUG
namespace AITraffic.Diagnostics
{
    /// <summary>
    /// Embedded standalone single-file HTML5/SVG vector map viewer.
    /// Provides pan/zoom, interactive layers, corridor route validation, and track telemetry.
    /// Excluded entirely from Release builds via #if DEBUG.
    /// </summary>
    internal static class TopologyMapTemplate
    {
        public static string GetHtml(string jsonData)
        {
            return RawHtml.Replace("/*__DATA_PAYLOAD__*/", jsonData);
        }

        private const string RawHtml = @"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
<title>Derail Valley AI Traffic — Vector Topology & Route Inspector</title>
<style>
  :root {
    --bg-main: #090d16;
    --bg-card: #131b2e;
    --bg-card-hover: #1c2742;
    --border: #23304e;
    --text-main: #e2e8f0;
    --text-muted: #94a3b8;
    --accent: #38bdf8;
    --pass: #22c55e;
    --fail: #ef4444;
    --gold: #f59e0b;
    --cyan: #06b6d4;
    --purple: #a855f7;
    --blue: #3b82f6;
  }
  * { box-sizing: border-box; margin: 0; padding: 0; }
  body {
    font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;
    background: var(--bg-main);
    color: var(--text-main);
    height: 100vh;
    overflow: hidden;
    display: flex;
    flex-direction: column;
  }
  /* Top Header */
  header {
    height: 48px;
    background: #0f172a;
    border-bottom: 1px solid var(--border);
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 0 16px;
    z-index: 10;
    user-select: none;
  }
  .brand {
    font-weight: 700;
    font-size: 15px;
    display: flex;
    align-items: center;
    gap: 8px;
    color: var(--accent);
  }
  .brand span { color: var(--text-muted); font-weight: 400; font-size: 13px; }
  .stats-bar { display: flex; gap: 16px; align-items: center; font-size: 12px; }
  .stat-badge {
    background: #1e293b;
    padding: 4px 10px;
    border-radius: 6px;
    border: 1px solid var(--border);
    display: flex;
    gap: 6px;
  }
  .stat-badge b { color: #f8fafc; }
  .header-actions { display: flex; gap: 8px; }
  .btn {
    background: #1e293b;
    color: var(--text-main);
    border: 1px solid var(--border);
    padding: 5px 12px;
    border-radius: 6px;
    font-size: 12px;
    cursor: pointer;
    transition: background 0.15s, border-color 0.15s;
    font-weight: 500;
  }
  .btn:hover { background: #334155; border-color: var(--accent); }
  .btn-primary { background: #0284c7; border-color: #38bdf8; color: white; }
  .btn-primary:hover { background: #0369a1; }

  /* Main Workspace */
  .workspace { display: flex; flex: 1; position: relative; overflow: hidden; }

  /* Left Sidebar */
  aside {
    width: 380px;
    background: #0c1322;
    border-right: 1px solid var(--border);
    display: flex;
    flex-direction: column;
    z-index: 5;
  }
  .tabs {
    display: flex;
    border-bottom: 1px solid var(--border);
    background: #0f172a;
  }
  .tab-btn {
    flex: 1;
    padding: 10px 4px;
    font-size: 12px;
    font-weight: 600;
    text-align: center;
    cursor: pointer;
    color: var(--text-muted);
    border-bottom: 2px solid transparent;
    transition: all 0.15s;
  }
  .tab-btn.active {
    color: var(--accent);
    border-bottom-color: var(--accent);
    background: var(--bg-card);
  }
  .tab-content { flex: 1; overflow-y: auto; padding: 12px; display: none; }
  .tab-content.active { display: block; }

  /* Corridors Tab */
  .search-box {
    width: 100%;
    background: #1e293b;
    border: 1px solid var(--border);
    border-radius: 6px;
    padding: 7px 10px;
    color: var(--text-main);
    font-size: 12px;
    margin-bottom: 10px;
    outline: none;
  }
  .search-box:focus { border-color: var(--accent); }
  .corridor-list { display: flex; flex-direction: column; gap: 8px; }
  .corridor-card {
    background: var(--bg-card);
    border: 1px solid var(--border);
    border-radius: 8px;
    padding: 10px;
    cursor: pointer;
    transition: all 0.15s;
  }
  .corridor-card:hover { background: var(--bg-card-hover); border-color: #3b82f6; }
  .corridor-card.selected { border-color: var(--gold); background: #262217; }
  .card-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 6px; }
  .corridor-title { font-weight: 700; font-size: 13px; display: flex; align-items: center; gap: 6px; }
  .badge-pass { background: #14532d; color: #4ade80; font-size: 10px; font-weight: 700; padding: 2px 6px; border-radius: 4px; }
  .badge-fail { background: #7f1d1d; color: #f87171; font-size: 10px; font-weight: 700; padding: 2px 6px; border-radius: 4px; }
  .card-meta { font-size: 11px; color: var(--text-muted); display: grid; grid-template-columns: 1fr 1fr; gap: 4px; }
  .card-meta div span { color: #f1f5f9; font-weight: 500; }

  /* Layers Tab */
  .layer-item {
    display: flex;
    align-items: center;
    gap: 10px;
    padding: 8px 10px;
    border-radius: 6px;
    background: var(--bg-card);
    border: 1px solid var(--border);
    margin-bottom: 6px;
    cursor: pointer;
    user-select: none;
    font-size: 12px;
  }
  .layer-item:hover { background: var(--bg-card-hover); }
  .layer-item input { cursor: pointer; }
  .color-dot { width: 12px; height: 12px; border-radius: 3px; }

  /* Custom Route Tab */
  .form-group { margin-bottom: 12px; }
  .form-group label { display: block; font-size: 11px; font-weight: 600; color: var(--text-muted); margin-bottom: 4px; }
  .select-box {
    width: 100%;
    background: #1e293b;
    border: 1px solid var(--border);
    border-radius: 6px;
    padding: 7px 10px;
    color: var(--text-main);
    font-size: 12px;
    outline: none;
  }

  /* Bottom Inspector Panel in Sidebar */
  .inspector {
    border-top: 1px solid var(--border);
    padding: 10px 14px;
    background: #090e1a;
    font-size: 11px;
    min-height: 110px;
  }
  .inspector-title { font-weight: 700; color: var(--accent); margin-bottom: 6px; font-size: 12px; }
  .inspector-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 4px; }
  .inspector-grid div span { color: #f8fafc; font-weight: 500; }

  /* SVG Map Area */
  main { flex: 1; position: relative; background: #070a12; cursor: grab; }
  main:active { cursor: grabbing; }
  svg { width: 100%; height: 100%; display: block; }

  /* Map Styles */
  .trk-base { stroke: #334155; stroke-width: 2.0; fill: none; stroke-linecap: round; stroke-linejoin: round; }
  .trk-spawn { stroke: var(--pass); stroke-width: 3.5; fill: none; }
  .trk-freight { stroke: var(--cyan); stroke-width: 3.5; fill: none; }
  .trk-storage { stroke: var(--blue); stroke-width: 3.0; fill: none; }
  .trk-pax { stroke: var(--purple); stroke-width: 3.5; fill: none; }
  .trk-loop { stroke: var(--gold); stroke-width: 3.0; fill: none; }
  .trk-steep { stroke: #f97316; stroke-dasharray: 6 3; stroke-width: 3.5; fill: none; }
  .trk-occupied { stroke: var(--fail); stroke-width: 4.0; fill: none; }
  .trk-highlight { stroke: #fbbf24; stroke-width: 6.5; fill: none; filter: drop-shadow(0 0 6px rgba(251,191,36,0.8)); }
  .trk-hover { stroke: #38bdf8 !important; stroke-width: 5.0 !important; filter: drop-shadow(0 0 4px #38bdf8); }

  .station-pin { fill: #f8fafc; font-size: 32px; font-weight: 700; text-anchor: middle; paint-order: stroke; stroke: #090d16; stroke-width: 6px; pointer-events: none; }
  .station-circle { fill: #0284c7; stroke: #ffffff; stroke-width: 4px; }

  /* Map HUD Controls */
  .map-controls {
    position: absolute;
    bottom: 20px;
    right: 20px;
    display: flex;
    flex-direction: column;
    gap: 6px;
    z-index: 10;
  }
  .map-btn {
    width: 36px;
    height: 36px;
    background: #1e293b;
    border: 1px solid var(--border);
    color: var(--text-main);
    border-radius: 8px;
    font-size: 16px;
    font-weight: 700;
    display: flex;
    align-items: center;
    justify-content: center;
    cursor: pointer;
    box-shadow: 0 4px 12px rgba(0,0,0,0.4);
  }
  .map-btn:hover { background: #334155; border-color: var(--accent); }

  /* Floating Tooltip */
  div#tooltip {
    position: absolute;
    background: rgba(15, 23, 42, 0.94);
    border: 1px solid var(--accent);
    padding: 6px 10px;
    border-radius: 6px;
    font-size: 11px;
    pointer-events: none;
    display: none;
    z-index: 100;
    box-shadow: 0 4px 14px rgba(0,0,0,0.5);
  }
</style>
</head>
<body>

<header>
  <div class=""brand"">
    🚂 Derail Valley AI Traffic <span>| Topology & Route Inspector (Debug)</span>
  </div>
  <div class=""stats-bar"">
    <div class=""stat-badge"">Tracks: <b id=""statTracks"">-</b></div>
    <div class=""stat-badge"">Network: <b id=""statLen"">- km</b></div>
    <div class=""stat-badge"">Stations: <b id=""statStations"">-</b></div>
    <div class=""stat-badge"">Corridors: <b id=""statCorridors"">-</b></div>
  </div>
  <div class=""header-actions"">
    <button class=""btn"" onclick=""resetView()"">Fit Valley</button>
    <button class=""btn"" onclick=""clearHighlights()"">Clear Route</button>
  </div>
</header>

<div class=""workspace"">
  <aside>
    <div class=""tabs"">
      <div class=""tab-btn active"" onclick=""switchTab('corridors', this)"">Corridors</div>
      <div class=""tab-btn"" onclick=""switchTab('custom', this)"">Custom Route</div>
      <div class=""tab-btn"" onclick=""switchTab('layers', this)"">Layers</div>
    </div>

    <!-- Corridors Tab -->
    <div id=""tab-corridors"" class=""tab-content active"">
      <input type=""text"" id=""searchCorridor"" class=""search-box"" placeholder=""Filter corridor (e.g. HB, MF, pass, fail)..."" oninput=""filterCorridors()"">
      <div id=""corridorList"" class=""corridor-list""></div>
    </div>

    <!-- Custom Route Tab -->
    <div id=""tab-custom"" class=""tab-content"">
      <div class=""form-group"">
        <label>Origin Station</label>
        <select id=""customOrigin"" class=""select-box""></select>
      </div>
      <div class=""form-group"">
        <label>Destination Station</label>
        <select id=""customDest"" class=""select-box""></select>
      </div>
      <button class=""btn btn-primary"" style=""width:100%; margin-top: 4px;"" onclick=""traceCustomRoute()"">Trace Route</button>
      <div id=""customResult"" style=""margin-top: 14px; font-size: 12px;""></div>
    </div>

    <!-- Layers Tab -->
    <div id=""tab-layers"" class=""tab-content"">
      <label class=""layer-item""><input type=""checkbox"" id=""layerBase"" checked onchange=""toggleLayer('tracks-base', this.checked)""><div class=""color-dot"" style=""background:#334155""></div>Base Rail Network</label>
      <label class=""layer-item""><input type=""checkbox"" id=""layerSpawn"" checked onchange=""toggleLayer('tracks-spawn', this.checked)""><div class=""color-dot"" style=""background:var(--pass)""></div>Candidate Departures / Spawns</label>
      <label class=""layer-item""><input type=""checkbox"" id=""layerFreight"" checked onchange=""toggleLayer('tracks-freight', this.checked)""><div class=""color-dot"" style=""background:var(--cyan)""></div>Freight Inbound [I] Sidings</label>
      <label class=""layer-item""><input type=""checkbox"" id=""layerStorage"" checked onchange=""toggleLayer('tracks-storage', this.checked)""><div class=""color-dot"" style=""background:var(--blue)""></div>Yard Storage [S] Sidings</label>
      <label class=""layer-item""><input type=""checkbox"" id=""layerPax"" checked onchange=""toggleLayer('tracks-pax', this.checked)""><div class=""color-dot"" style=""background:var(--purple)""></div>Passenger Platforms [P]</label>
      <label class=""layer-item""><input type=""checkbox"" id=""layerLoop"" checked onchange=""toggleLayer('tracks-loop', this.checked)""><div class=""color-dot"" style=""background:var(--gold)""></div>Passing Loops / Mainlines</label>
      <label class=""layer-item""><input type=""checkbox"" id=""layerSteep"" checked onchange=""toggleLayer('tracks-steep', this.checked)""><div class=""color-dot"" style=""background:#f97316""></div>Steep Incline (>0.40%)</label>
      <label class=""layer-item""><input type=""checkbox"" id=""layerOccupied"" checked onchange=""toggleLayer('tracks-occupied', this.checked)""><div class=""color-dot"" style=""background:var(--fail)""></div>Occupied Tracks</label>
      <label class=""layer-item""><input type=""checkbox"" id=""layerStations"" checked onchange=""toggleLayer('stations', this.checked)""><div class=""color-dot"" style=""background:#fff""></div>Station Labels & Nodes</label>
      <label class=""layer-item""><input type=""checkbox"" id=""layerTrains"" checked onchange=""toggleLayer('active-trains', this.checked)""><div class=""color-dot"" style=""background:var(--accent)""></div>Active AI Trains</label>
    </div>

    <!-- Inspector Box -->
    <div class=""inspector"">
      <div class=""inspector-title"" id=""inspTitle"">Track Inspector</div>
      <div class=""inspector-grid"">
        <div>Name: <span id=""inspName"">-</span></div>
        <div>Logic ID: <span id=""inspLogic"">-</span></div>
        <div>Length: <span id=""inspLen"">-</span></div>
        <div>Grade: <span id=""inspGrade"">-</span></div>
        <div>Dead End: <span id=""inspDead"">-</span></div>
        <div>Role: <span id=""inspRole"">-</span></div>
      </div>
    </div>
  </aside>

  <main id=""mapContainer"">
    <svg id=""mapSvg"">
      <g id=""viewport"">
        <g id=""tracks-base""></g>
        <g id=""tracks-storage""></g>
        <g id=""tracks-loop""></g>
        <g id=""tracks-freight""></g>
        <g id=""tracks-pax""></g>
        <g id=""tracks-spawn""></g>
        <g id=""tracks-steep""></g>
        <g id=""tracks-occupied""></g>
        <g id=""highlight-layer""></g>
        <g id=""stations""></g>
        <g id=""active-trains""></g>
      </g>
    </svg>

    <div class=""map-controls"">
      <button class=""map-btn"" onclick=""zoomStep(0.75)"">+</button>
      <button class=""map-btn"" onclick=""zoomStep(1.33)"">-</button>
      <button class=""map-btn"" onclick=""resetView()"">⌂</button>
    </div>

    <div id=""tooltip""></div>
  </main>
</div>

<script>
const DATA = /*__DATA_PAYLOAD__*/;

let viewBox = { x: 0, y: 0, w: 1000, h: 1000 };
let initialViewBox = { x: 0, y: 0, w: 1000, h: 1000 };
const svg = document.getElementById('mapSvg');
const mapContainer = document.getElementById('mapContainer');
const tooltip = document.getElementById('tooltip');
let isPanning = false;
let startX = 0, startY = 0;
let trackElementsMap = {};

function init() {
  if (!DATA || !DATA.valleyBounds) return;
  const b = DATA.valleyBounds;
  initialViewBox = { x: 0, y: 0, w: b.width, h: b.height };
  viewBox = { ...initialViewBox };
  updateViewBox();

  // Populate Stats
  document.getElementById('statTracks').textContent = DATA.tracks.length;
  let totalKm = DATA.tracks.reduce((acc, t) => acc + (t.len || 0), 0) / 1000;
  document.getElementById('statLen').textContent = totalKm.toFixed(1) + ' km';
  document.getElementById('statStations').textContent = DATA.stations.length;

  let passed = DATA.corridors.filter(c => c.passed).length;
  document.getElementById('statCorridors').textContent = `${passed}/${DATA.corridors.length} Pass`;

  renderTracks();
  renderStations();
  renderActiveTrains();
  renderCorridors();
  populateStationDropdowns();
  setupPanZoom();
}

function updateViewBox() {
  svg.setAttribute('viewBox', `${viewBox.x} ${viewBox.y} ${viewBox.w} ${viewBox.h}`);
}

function renderTracks() {
  const gBase = document.getElementById('tracks-base');
  const gSpawn = document.getElementById('tracks-spawn');
  const gFreight = document.getElementById('tracks-freight');
  const gStorage = document.getElementById('tracks-storage');
  const gPax = document.getElementById('tracks-pax');
  const gLoop = document.getElementById('tracks-loop');
  const gSteep = document.getElementById('tracks-steep');
  const gOccupied = document.getElementById('tracks-occupied');

  DATA.tracks.forEach(trk => {
    if (!trk.pts || trk.pts.length < 2) return;
    let d = 'M ' + trk.pts.map(p => `${p[0]},${p[1]}`).join(' L ');
    
    let path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
    path.setAttribute('d', d);
    path.setAttribute('class', 'trk-base');
    path.dataset.id = trk.id;
    gBase.appendChild(path);
    trackElementsMap[trk.id] = { data: trk, path: path };

    // Classification overlays
    if (trk.isSpawn) createOverlay(gSpawn, d, 'trk-spawn', trk);
    if (trk.isFreightIn) createOverlay(gFreight, d, 'trk-freight', trk);
    if (trk.isStorage) createOverlay(gStorage, d, 'trk-storage', trk);
    if (trk.isPax) createOverlay(gPax, d, 'trk-pax', trk);
    if (trk.isLoop) createOverlay(gLoop, d, 'trk-loop', trk);
    if (trk.isSteep) createOverlay(gSteep, d, 'trk-steep', trk);
    if (trk.isOccupied) createOverlay(gOccupied, d, 'trk-occupied', trk);

    // Event listeners
    path.addEventListener('mouseenter', (e) => onTrackHover(trk, e));
    path.addEventListener('mouseleave', () => onTrackLeave());
    path.addEventListener('click', () => onTrackClick(trk));
  });
}

function createOverlay(parent, d, className, trk) {
  let p = document.createElementNS('http://www.w3.org/2000/svg', 'path');
  p.setAttribute('d', d);
  p.setAttribute('class', className);
  p.dataset.id = trk.id;
  p.addEventListener('mouseenter', (e) => onTrackHover(trk, e));
  p.addEventListener('mouseleave', () => onTrackLeave());
  p.addEventListener('click', () => onTrackClick(trk));
  parent.appendChild(p);
}

function renderStations() {
  const g = document.getElementById('stations');
  DATA.stations.forEach(st => {
    let circle = document.createElementNS('http://www.w3.org/2000/svg', 'circle');
    circle.setAttribute('cx', st.x);
    circle.setAttribute('cy', st.y);
    circle.setAttribute('r', '24');
    circle.setAttribute('class', 'station-circle');
    g.appendChild(circle);

    let text = document.createElementNS('http://www.w3.org/2000/svg', 'text');
    text.setAttribute('x', st.x);
    text.setAttribute('y', st.y - 32);
    text.setAttribute('class', 'station-pin');
    text.textContent = `${st.name} [${st.id}]`;
    g.appendChild(text);
  });
}

function renderActiveTrains() {
  const g = document.getElementById('active-trains');
  if (!DATA.activeTrains) return;
  DATA.activeTrains.forEach(tr => {
    let circle = document.createElementNS('http://www.w3.org/2000/svg', 'circle');
    circle.setAttribute('cx', tr.x);
    circle.setAttribute('cy', tr.y);
    circle.setAttribute('r', '18');
    circle.setAttribute('fill', '#38bdf8');
    circle.setAttribute('stroke', '#ffffff');
    circle.setAttribute('stroke-width', '3');
    circle.style.cursor = 'pointer';
    circle.onclick = () => {
      if (tr.path && tr.path.length > 0) highlightRouteTracks(tr.path);
    };
    g.appendChild(circle);

    let text = document.createElementNS('http://www.w3.org/2000/svg', 'text');
    text.setAttribute('x', tr.x);
    text.setAttribute('y', tr.y + 30);
    text.setAttribute('fill', '#38bdf8');
    text.setAttribute('font-size', '20px');
    text.setAttribute('font-weight', '700');
    text.setAttribute('text-anchor', 'middle');
    text.style.cursor = 'pointer';
    text.onclick = () => {
      if (tr.path && tr.path.length > 0) highlightRouteTracks(tr.path);
    };
    text.textContent = `${tr.carId} (${tr.speed} km/h)`;
    g.appendChild(text);
  });
}

function renderCorridors() {
  const container = document.getElementById('corridorList');
  container.innerHTML = '';
  DATA.corridors.forEach((c, idx) => {
    let card = document.createElement('div');
    card.className = 'corridor-card';
    card.id = `corridor-card-${idx}`;
    card.innerHTML = `
      <div class=""card-header"">
        <div class=""corridor-title"">${c.orig} ➔ ${c.dest}</div>
        <span class=""${c.passed ? 'badge-pass' : 'badge-fail'}"">${c.passed ? 'PASS' : 'FAIL'}</span>
      </div>
      <div class=""card-meta"">
        <div>Type: <span>${c.consist}</span></div>
        <div>Dist: <span>${c.dist > 0 ? (c.dist / 1000).toFixed(2) + ' km' : 'N/A'}</span></div>
        <div>Grade: <span>${c.depGrade > 0 ? '+' : ''}${c.depGrade}%</span></div>
        <div>Spawns: <span>${c.depTrack || 'None'}</span></div>
      </div>
    `;
    card.onclick = () => selectCorridor(c, card);
    container.appendChild(card);
  });
}

function selectCorridor(c, cardElem) {
  document.querySelectorAll('.corridor-card').forEach(el => el.classList.remove('selected'));
  if (cardElem) cardElem.classList.add('selected');

  highlightRouteTracks(c.trackIds);
}

function highlightRouteTracks(trackIds) {
  const gHighlight = document.getElementById('highlight-layer');
  gHighlight.innerHTML = '';
  if (!trackIds || trackIds.length === 0) return;

  let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity;

  trackIds.forEach(id => {
    let entry = trackElementsMap[id];
    if (entry && entry.data && entry.data.pts) {
      let d = 'M ' + entry.data.pts.map(p => {
        if (p[0] < minX) minX = p[0];
        if (p[0] > maxX) maxX = p[0];
        if (p[1] < minY) minY = p[1];
        if (p[1] > maxY) maxY = p[1];
        return `${p[0]},${p[1]}`;
      }).join(' L ');

      let p = document.createElementNS('http://www.w3.org/2000/svg', 'path');
      p.setAttribute('d', d);
      p.setAttribute('class', 'trk-highlight');
      gHighlight.appendChild(p);
    }
  });

  // Focus view on route
  if (minX !== Infinity) {
    let pad = 200;
    viewBox.x = minX - pad;
    viewBox.y = minY - pad;
    viewBox.w = (maxX - minX) + pad * 2;
    viewBox.h = (maxY - minY) + pad * 2;
    updateViewBox();
  }
}

function clearHighlights() {
  document.getElementById('highlight-layer').innerHTML = '';
  document.querySelectorAll('.corridor-card').forEach(el => el.classList.remove('selected'));
}

function populateStationDropdowns() {
  const selOrig = document.getElementById('customOrigin');
  const selDest = document.getElementById('customDest');
  selOrig.innerHTML = '';
  selDest.innerHTML = '';

  DATA.stations.forEach(st => {
    let opt1 = document.createElement('option');
    opt1.value = st.id;
    opt1.textContent = `${st.name} [${st.id}]`;
    selOrig.appendChild(opt1);

    let opt2 = document.createElement('option');
    opt2.value = st.id;
    opt2.textContent = `${st.name} [${st.id}]`;
    selDest.appendChild(opt2);
  });
  if (selDest.options.length > 1) selDest.selectedIndex = 1;
}

function traceCustomRoute() {
  const orig = document.getElementById('customOrigin').value;
  const dest = document.getElementById('customDest').value;
  const resultDiv = document.getElementById('customResult');

  let match = DATA.corridors.find(c => c.orig === orig && c.dest === dest);
  if (match) {
    selectCorridor(match, null);
    resultDiv.innerHTML = `<div style=""color:var(--pass); font-weight:700;"">Predefined Corridor Found!</div>
      <div>Distance: ${(match.dist / 1000).toFixed(2)} km</div>
      <div>Departure Track: ${match.depTrack}</div>
      <div>Destination Track: ${match.destTrack}</div>`;
  } else {
    clearHighlights();
    // Highlight origin candidate spawns in green and dest sidings in cyan
    let origTracks = DATA.tracks.filter(t => t.station === orig && t.isSpawn).map(t => t.id);
    let destTracks = DATA.tracks.filter(t => t.station === dest && (t.isFreightIn || t.isPax)).map(t => t.id);
    highlightRouteTracks(origTracks.concat(destTracks));
    resultDiv.innerHTML = `<div style=""color:var(--gold);"">No direct predefined corridor in schedule.</div>
      <div>Highlighted <b>${origTracks.length}</b> candidate spawns at [${orig}] and <b>${destTracks.length}</b> arrival sidings at [${dest}].</div>`;
  }
}

function onTrackHover(trk, e) {
  tooltip.style.display = 'block';
  tooltip.innerHTML = `<b>${trk.name}</b><br>Length: ${trk.len.toFixed(1)}m | Grade: ${trk.grade.toFixed(2)}%`;
  tooltip.style.left = (e.clientX + 14) + 'px';
  tooltip.style.top = (e.clientY + 14) + 'px';

  document.getElementById('inspTitle').textContent = 'Track: ' + trk.name;
  document.getElementById('inspName').textContent = trk.name;
  document.getElementById('inspLogic').textContent = trk.logicId || 'None';
  document.getElementById('inspLen').textContent = trk.len.toFixed(1) + ' m';
  document.getElementById('inspGrade').textContent = (trk.grade > 0 ? '+' : '') + trk.grade.toFixed(2) + ' %';
  document.getElementById('inspDead').textContent = trk.isDeadEnd ? 'YES (Buffer)' : 'No';

  let roles = [];
  if (trk.isSpawn) roles.push('Departure/Spawn');
  if (trk.isFreightIn) roles.push('Freight Inbound [I]');
  if (trk.isStorage) roles.push('Storage [S]');
  if (trk.isPax) roles.push('Pax Platform [P]');
  if (trk.isLoop) roles.push('Passing Loop');
  if (trk.isSteep) roles.push('Steep Incline');
  if (trk.isOccupied) roles.push('Occupied');
  document.getElementById('inspRole').textContent = roles.length > 0 ? roles.join(', ') : 'Mainline Segment';
}

function onTrackLeave() {
  tooltip.style.display = 'none';
}

function onTrackClick(trk) {
  highlightRouteTracks([trk.id]);
}

function filterCorridors() {
  let q = document.getElementById('searchCorridor').value.toLowerCase();
  DATA.corridors.forEach((c, idx) => {
    let card = document.getElementById(`corridor-card-${idx}`);
    let text = `${c.orig} ${c.dest} ${c.consist} ${c.passed ? 'pass' : 'fail'}`.toLowerCase();
    card.style.display = text.includes(q) ? 'block' : 'none';
  });
}

function switchTab(name, btn) {
  document.querySelectorAll('.tab-btn').forEach(b => b.classList.remove('active'));
  document.querySelectorAll('.tab-content').forEach(c => c.classList.remove('active'));
  btn.classList.add('active');
  document.getElementById('tab-' + name).classList.add('active');
}

function toggleLayer(layerId, visible) {
  document.getElementById(layerId).style.display = visible ? 'inline' : 'none';
}

function resetView() {
  viewBox = { ...initialViewBox };
  updateViewBox();
}

function zoomStep(factor) {
  let cx = viewBox.x + viewBox.w / 2;
  let cy = viewBox.y + viewBox.h / 2;
  viewBox.w *= factor;
  viewBox.h *= factor;
  viewBox.x = cx - viewBox.w / 2;
  viewBox.y = cy - viewBox.h / 2;
  updateViewBox();
}

function setupPanZoom() {
  mapContainer.addEventListener('wheel', (e) => {
    e.preventDefault();
    const rect = svg.getBoundingClientRect();
    const mx = viewBox.x + (e.clientX - rect.left) * (viewBox.w / rect.width);
    const my = viewBox.y + (e.clientY - rect.top) * (viewBox.h / rect.height);
    const factor = e.deltaY < 0 ? 0.82 : 1.22;

    viewBox.x = mx - (mx - viewBox.x) * factor;
    viewBox.y = my - (my - viewBox.y) * factor;
    viewBox.w *= factor;
    viewBox.h *= factor;
    updateViewBox();
  });

  mapContainer.addEventListener('mousedown', (e) => {
    if (e.button === 0) {
      isPanning = true;
      startX = e.clientX;
      startY = e.clientY;
    }
  });

  window.addEventListener('mousemove', (e) => {
    if (!isPanning) return;
    const rect = svg.getBoundingClientRect();
    const dx = (e.clientX - startX) * (viewBox.w / rect.width);
    const dy = (e.clientY - startY) * (viewBox.h / rect.height);
    viewBox.x -= dx;
    viewBox.y -= dy;
    startX = e.clientX;
    startY = e.clientY;
    updateViewBox();
  });

  window.addEventListener('mouseup', () => { isPanning = false; });
}

window.onload = init;
</script>
</body>
</html>";
    }
}
#endif
