// The story board's page: acts, scenes and assets of the story mode, pictures of them made with Gemini, and the
// story text. Everything is kept by the local server (tools/storyboard/server.py) in docs/story/board.json.
"use strict";

const SCENE_STATUS = [["idee", "Idee"], ["entwurf", "Entwurf"], ["ausgearbeitet", "Ausgearbeitet"], ["umgesetzt", "Umgesetzt"]];
const ASSET_STATUS = [["idee", "Idee"], ["geplant", "Geplant"], ["konzept", "Konzept"], ["in-arbeit", "In Arbeit"], ["fertig", "Fertig"]];
const ASSET_KINDS = [["modell", "3D-Modell"], ["karte", "Karte / Planet"], ["level", "Innenlevel / Baukasten"], ["effekt", "Effekt / Shader"],
  ["musik", "Musik"], ["sound", "Soundeffekt"], ["stimme", "Stimme"], ["hud", "HUD / Oberfläche"], ["system", "Spielsystem"], ["text", "Text / Dialog"]];
const ASSET_SOURCES = [["vorhanden", "Vorhanden"], ["meshy", "Meshy"], ["gemini", "Gemini"], ["musikgenerator", "Musik-Generator"],
  ["sfxgenerator", "SFX-Generator"], ["tts", "Sprachausgabe"], ["selbst", "Selbst bauen"]];
const ASPECTS = ["16:9", "21:9", "4:3", "3:2", "1:1", "2:3", "9:16"];
const SIZES = [["", "Standard"], ["1K", "1K"], ["2K", "2K"], ["4K", "4K"]];

const state = {
  board: null,
  tab: "board",
  scene: null,     // id of the scene or act open on the scenes tab: {kind: "act" | "scene", id}
  asset: null,     // id of the asset open on the assets tab
  assetFilter: { kind: "", status: "", text: "" },
  galleryFilter: "",
  models: [], hasKey: false, route: "", modelError: "",
  shots: [],
  story: null,
  busy: {},        // owner id -> number of pictures being made
  errors: {},      // owner id -> last error
};

const prefs = (() => {
  const defaults = { model: "", aspect: "16:9", size: "", count: 1, useStyle: true };
  try { return Object.assign(defaults, JSON.parse(localStorage.getItem("storyboard.prefs") || "{}")); } catch { return defaults; }
})();
function savePrefs() { try { localStorage.setItem("storyboard.prefs", JSON.stringify(prefs)); } catch { /* private window */ } }

const $ = (s, root = document) => root.querySelector(s);
const h = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
const uid = (p) => `${p}-${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`;
const options = (list, value) => list.map(([v, t]) => `<option value="${h(v)}"${v === value ? " selected" : ""}>${h(t)}</option>`).join("");
const label = (list, value) => (list.find(([v]) => v === value) || [value, value])[1];

// ---------- loading and saving ----------

async function api(path, init) {
  const response = await fetch(path, init);
  const type = response.headers.get("Content-Type") || "";
  const body = type.includes("json") ? await response.json() : await response.text();
  if (!response.ok) throw new Error(body.error || body || response.statusText);
  return body;
}

let saveTimer = null;
function save() {
  $("#saved").textContent = "Ungespeichert …";
  clearTimeout(saveTimer);
  saveTimer = setTimeout(async () => {
    saveTimer = null;
    const response = await fetch("/api/board", {
      method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ rev: state.rev, board: state.board }),
    }).catch((error) => ({ ok: false, status: 0, json: async () => ({ error: error.message }) }));
    const body = await response.json().catch(() => ({}));
    if (response.ok) {
      state.rev = body.rev;
      $("#saved").textContent = "Gespeichert " + new Date().toLocaleTimeString("de-DE", { hour: "2-digit", minute: "2-digit" });
    } else if (response.status === 409) {
      // The file changed elsewhere since this page read it: take that version rather than writing over it.
      await refresh();
      $("#saved").textContent = "Das Board wurde woanders geändert und neu geladen. Deine letzte Änderung bitte wiederholen.";
    } else {
      $("#saved").textContent = "Speichern fehlgeschlagen: " + (body.error || response.status);
    }
  }, 600);
}

function adopt(data) {
  state.rev = data.rev;
  state.board = data.board;
  const b = state.board;
  b.style ??= { prompt: "", refs: [] };
  b.style.refs ??= [];
  for (const key of ["acts", "scenes", "assets"]) b[key] ??= [];
  for (const item of [...b.scenes, ...b.assets]) { item.images ??= []; item.refs ??= []; }
  for (const s of b.scenes) s.assets ??= [];
}

// Takes the board from disk when it changed there and nothing here waits to be saved.
async function refresh() {
  const data = await api("/api/board");
  if (data.rev !== state.rev) { adopt(data); render(); }
}
window.addEventListener("focus", () => { if (!saveTimer && state.board && $("#overlay").hidden) refresh().catch(() => {}); });

async function load() {
  adopt(await api("/api/board"));
  try { state.tab = localStorage.getItem("storyboard.tab") || state.tab; } catch { /* ignore */ }
  render();
  api("/api/models").then((m) => {
    state.models = m.models; state.hasKey = m.key; state.route = m.route || ""; state.modelError = m.error || "";
    if (!prefs.model || !state.models.includes(prefs.model)) prefs.model = state.models.includes(m.default) ? m.default : state.models[0];
    render();
  }).catch((e) => { state.modelError = e.message; });
  api("/api/refs").then((r) => { state.shots = r; }).catch(() => {});
}

// ---------- lookups ----------

const act = (id) => state.board.acts.find((a) => a.id === id);
const scene = (id) => state.board.scenes.find((s) => s.id === id);
const asset = (id) => state.board.assets.find((a) => a.id === id);
const owner = (id) => scene(id) || asset(id);
const scenesOf = (actId) => state.board.scenes.filter((s) => s.act === actId);
const imgUrl = (file) => (file.startsWith("/") ? file : `/images/${file}`);
const coverOf = (item) => item.cover || item.images[0]?.file || "";

function allImages() {
  const list = [];
  for (const s of state.board.scenes) for (const i of s.images) list.push({ ...i, owner: s, kind: "Szene" });
  for (const a of state.board.assets) for (const i of a.images) list.push({ ...i, owner: a, kind: "Asset" });
  return list.sort((x, y) => String(y.at).localeCompare(String(x.at)));
}

// ---------- rendering ----------

function render() {
  for (const b of document.querySelectorAll("#tabs button")) b.classList.toggle("on", b.dataset.tab === state.tab);
  const main = $("#main");
  const scroll = window.scrollY;
  main.innerHTML = { board: viewBoard, scenes: viewScenes, assets: viewAssets, gallery: viewGallery, story: viewStory, style: viewStyle }[state.tab]();
  if (state.tab === "story") fillStory();
  window.scrollTo(0, scroll);
}

function viewBoard() {
  const b = state.board;
  const done = b.scenes.filter((s) => s.status === "umgesetzt").length;
  const assetsDone = b.assets.filter((a) => a.status === "fertig").length;
  const stats = `<div class="stats">
    <div class="stat"><b>${b.acts.length}</b><span>Akte</span></div>
    <div class="stat"><b>${b.scenes.length}</b><span>Szenen · ${done} umgesetzt</span></div>
    <div class="stat"><b>${b.assets.length}</b><span>Assets · ${assetsDone} fertig</span></div>
    <div class="stat"><b>${allImages().length}</b><span>Bilder</span></div></div>`;
  const rows = b.acts.map((a) => `<section class="act-row">
      <h2>${h(a.title)} <small>${h(a.summary)}</small></h2>
      <div class="strip">${scenesOf(a.id).map(sceneCard).join("")}
        <button class="scene-card add-card" data-action="add-scene" data-act="${a.id}">+ Szene</button></div>
    </section>`).join("");
  return stats + `<div class="acts-board">${rows || '<div class="empty">Noch keine Akte.</div>'}</div>
    <p><button data-action="add-act">+ Akt</button></p>`;
}

function sceneCard(s) {
  const cover = coverOf(s);
  return `<button class="scene-card" data-action="open-scene" data-id="${s.id}">
    <div class="thumb" style="${cover ? `background-image:url('${imgUrl(cover)}')` : ""}">${cover ? "" : "▦"}</div>
    <div class="body"><div class="t"><span class="dot st-${h(s.status)}"></span>${h(s.title || "Ohne Titel")}</div>
    <div class="d">${h(s.text)}</div></div></button>`;
}

function viewScenes() {
  const b = state.board;
  const sel = state.scene || (b.scenes[0] ? { kind: "scene", id: b.scenes[0].id } : b.acts[0] ? { kind: "act", id: b.acts[0].id } : null);
  state.scene = sel;
  const side = b.acts.map((a) => `<div class="list-act">
      <button class="act ${sel?.kind === "act" && sel.id === a.id ? "on" : ""}" data-action="select-act" data-id="${a.id}">${h(a.title)}</button>
      ${scenesOf(a.id).map((s, i) => `<button class="item ${sel?.kind === "scene" && sel.id === s.id ? "on" : ""}" data-action="select-scene" data-id="${s.id}">
        <span class="n">${i + 1}</span><span class="dot st-${h(s.status)}"></span>${h(s.title || "Ohne Titel")}</button>`).join("")}
    </div>`).join("");
  const loose = b.scenes.filter((s) => !act(s.act));
  const looseList = loose.length ? `<div class="group-label">Ohne Akt</div>` + loose.map((s) => `<button class="item" data-action="select-scene" data-id="${s.id}"><span class="dot st-${h(s.status)}"></span>${h(s.title)}</button>`).join("") : "";
  let editor = '<div class="empty">Lege einen Akt an.</div>';
  if (sel?.kind === "act" && act(sel.id)) editor = actEditor(act(sel.id));
  if (sel?.kind === "scene" && scene(sel.id)) editor = sceneEditor(scene(sel.id));
  return `<div class="split"><aside class="side">${side}${looseList}<p class="row"><button data-action="add-act">+ Akt</button></p></aside>
    <section class="card">${editor}</section></div>`;
}

function actEditor(a) {
  return `<div class="row"><h2>Akt</h2><span class="fixed"><button data-action="move-act" data-id="${a.id}" data-dir="-1">↑</button>
      <button data-action="move-act" data-id="${a.id}" data-dir="1">↓</button>
      <button class="danger" data-action="delete-act" data-id="${a.id}">Akt löschen</button></span></div>
    <label>Titel</label><input data-bind="act:${a.id}:title" value="${h(a.title)}">
    <label>Kurzfassung</label><input data-bind="act:${a.id}:summary" value="${h(a.summary)}">
    <label>Notizen</label><textarea data-bind="act:${a.id}:notes" rows="8">${h(a.notes)}</textarea>
    <h3>Szenen</h3><div class="strip">${scenesOf(a.id).map(sceneCard).join("")}
      <button class="scene-card add-card" data-action="add-scene" data-act="${a.id}">+ Szene</button></div>`;
}

function sceneEditor(s) {
  const assetChips = s.assets.map((id) => asset(id)).filter(Boolean).map((a) =>
    `<span class="chip link" data-action="goto-asset" data-id="${a.id}"><span class="dot st-${h(a.status)}"></span>${h(a.name)}
      <button data-action="unlink-asset" data-scene="${s.id}" data-id="${a.id}" title="Entfernen">×</button></span>`).join("");
  const free = state.board.assets.filter((a) => !s.assets.includes(a.id));
  return `<div class="row"><h2>${h(s.title || "Szene")}</h2><span class="fixed">
      <button data-action="move-scene" data-id="${s.id}" data-dir="-1">↑</button>
      <button data-action="move-scene" data-id="${s.id}" data-dir="1">↓</button>
      <button class="danger" data-action="delete-scene" data-id="${s.id}">Löschen</button></span></div>
    <div class="row">
      <div><label>Titel</label><input data-bind="scene:${s.id}:title" value="${h(s.title)}"></div>
      <div><label>Akt</label><select data-bind="scene:${s.id}:act" data-rerender>${options(state.board.acts.map((a) => [a.id, a.title]), s.act)}</select></div>
      <div><label>Status</label><select data-bind="scene:${s.id}:status" data-rerender>${options(SCENE_STATUS, s.status)}</select></div>
    </div>
    <div class="grid2">
      <div><label>Was passiert</label><textarea data-bind="scene:${s.id}:text" rows="6">${h(s.text)}</textarea></div>
      <div><label>Gameplay</label><textarea data-bind="scene:${s.id}:gameplay" rows="6">${h(s.gameplay)}</textarea></div>
      <div><label>Stimmung, Licht, Klang</label><textarea data-bind="scene:${s.id}:mood" rows="4">${h(s.mood)}</textarea></div>
      <div><label>Dialog / Durchsagen</label><textarea data-bind="scene:${s.id}:dialog" rows="4">${h(s.dialog)}</textarea></div>
    </div>
    <label>Benötigte Assets</label>
    <div class="chips">${assetChips || '<span class="hint">noch keine</span>'}</div>
    <div class="row" style="margin-top:6px"><select id="link-asset"><option value="">Asset verknüpfen …</option>${options(free.map((a) => [a.id, `${a.name} (${label(ASSET_KINDS, a.kind)})`]), "")}</select>
      <span class="fixed"><button data-action="link-asset" data-scene="${s.id}">Verknüpfen</button>
      <button data-action="new-asset-for" data-scene="${s.id}">+ Neues Asset</button></span></div>
    ${generator(s)}`;
}

function viewAssets() {
  const b = state.board, f = state.assetFilter;
  const matches = b.assets.filter((a) => (!f.kind || a.kind === f.kind) && (!f.status || a.status === f.status) &&
    (!f.text || `${a.name} ${a.text}`.toLowerCase().includes(f.text.toLowerCase())));
  if (!state.asset || !asset(state.asset)) state.asset = matches[0]?.id || b.assets[0]?.id || null;
  const groups = ASSET_KINDS.map(([k, t]) => {
    const items = matches.filter((a) => a.kind === k);
    return items.length ? `<div class="group-label">${h(t)} · ${items.length}</div>` + items.map((a) =>
      `<button class="item ${state.asset === a.id ? "on" : ""}" data-action="select-asset" data-id="${a.id}"><span class="dot st-${h(a.status)}"></span>${h(a.name || "Ohne Namen")}</button>`).join("") : "";
  }).join("");
  const side = `<input id="asset-text" placeholder="Suchen …" value="${h(f.text)}">
    <div class="row" style="margin-top:6px"><select id="asset-kind"><option value="">Alle Arten</option>${options(ASSET_KINDS, f.kind)}</select>
    <select id="asset-status"><option value="">Jeder Status</option>${options(ASSET_STATUS, f.status)}</select></div>
    ${groups || '<div class="empty">Keine Treffer.</div>'}
    <p><button data-action="add-asset">+ Asset</button></p>`;
  const a = asset(state.asset);
  return `<div class="split"><aside class="side">${side}</aside><section class="card">${a ? assetEditor(a) : '<div class="empty">Noch keine Assets.</div>'}</section></div>`;
}

function assetEditor(a) {
  const usedIn = state.board.scenes.filter((s) => s.assets.includes(a.id));
  return `<div class="row"><h2>${h(a.name || "Asset")}</h2><span class="fixed"><button class="danger" data-action="delete-asset" data-id="${a.id}">Löschen</button></span></div>
    <div class="row">
      <div><label>Name</label><input data-bind="asset:${a.id}:name" value="${h(a.name)}"></div>
      <div><label>Art</label><select data-bind="asset:${a.id}:kind" data-rerender>${options(ASSET_KINDS, a.kind)}</select></div>
      <div><label>Quelle</label><select data-bind="asset:${a.id}:source">${options(ASSET_SOURCES, a.source)}</select></div>
      <div><label>Status</label><select data-bind="asset:${a.id}:status" data-rerender>${options(ASSET_STATUS, a.status)}</select></div>
    </div>
    <div class="grid2">
      <div><label>Beschreibung</label><textarea data-bind="asset:${a.id}:text" rows="5">${h(a.text)}</textarea></div>
      <div><label>Prompt für Meshy / Generator / Sprachausgabe</label><textarea data-bind="asset:${a.id}:sourcePrompt" rows="5">${h(a.sourcePrompt)}</textarea></div>
    </div>
    <label>Notizen zur Umsetzung</label><textarea data-bind="asset:${a.id}:notes" rows="3">${h(a.notes)}</textarea>
    <label>Gebraucht in</label>
    <div class="chips">${usedIn.map((s) => `<span class="chip link" data-action="open-scene" data-id="${s.id}">${h(s.title)}</span>`).join("") || '<span class="hint">keiner Szene zugeordnet</span>'}</div>
    ${generator(a)}`;
}

function generator(item) {
  const busy = state.busy[item.id] || 0;
  const refs = item.refs.map((url) => `<span class="ref"><img src="${h(imgUrl(url))}" alt=""><button data-action="drop-ref" data-owner="${item.id}" data-url="${h(url)}" title="Entfernen">×</button></span>`).join("");
  const styleNote = prefs.useStyle && (state.board.style.prompt || state.board.style.refs.length)
    ? `<span class="hint">+ Stil-Vorgabe${state.board.style.refs.length ? ` und ${state.board.style.refs.length} Stil-Referenz(en)` : ""}</span>` : "";
  const pics = [...item.images].reverse().map((img) => picture(item, img)).join("");
  return `<div class="gen">
      <label style="margin-top:0">Bild-Prompt (Englisch klappt meist besser)</label>
      <textarea data-bind="${item.act !== undefined ? "scene" : "asset"}:${item.id}:prompt" rows="4">${h(item.prompt)}</textarea>
      <div class="refs"><span class="hint">Referenzen:</span>${refs}<button data-action="pick-refs" data-owner="${item.id}">+ Referenzbild</button> ${styleNote}</div>
      <div class="row" style="margin-top:10px">
        <select data-pref="model">${options(state.models.map((m) => [m, m]), prefs.model)}</select>
        <select data-pref="aspect" class="fixed">${options(ASPECTS.map((a) => [a, a]), prefs.aspect)}</select>
        <select data-pref="size" class="fixed">${options(SIZES, prefs.size)}</select>
        <select data-pref="count" class="fixed">${options([1, 2, 3, 4].map((n) => [String(n), `${n}×`]), String(prefs.count))}</select>
        <label class="fixed" style="margin:0;text-transform:none"><input type="checkbox" data-pref="useStyle" ${prefs.useStyle ? "checked" : ""} style="width:auto"> Stil</label>
        <button class="primary fixed" data-action="generate" data-owner="${item.id}" ${busy || !state.hasKey ? "disabled" : ""}>
          ${busy ? `<span class="spin"></span> ${busy} Bild(er) …` : "Bild generieren"}</button>
      </div>
      ${state.hasKey ? "" : `<div class="error">Kein Zugang zu Gemini: GEMINI_API_KEY setzen oder gcloud anmelden und den Server neu starten.</div>`}
      ${state.errors[item.id] ? `<div class="error">${h(state.errors[item.id])}</div>` : ""}
    </div>
    <div class="pics">${pics || ""}</div>`;
}

function picture(item, img) {
  const isCover = coverOf(item) === img.file;
  const isRef = item.refs.includes(img.file);
  const isStyle = state.board.style.refs.includes(img.file);
  return `<div class="pic ${isCover ? "cover" : ""}">
    <img src="${h(imgUrl(img.file))}" loading="lazy" data-action="zoom" data-owner="${item.id}" data-file="${h(img.file)}" alt="">
    <div class="bar">
      <button class="${isCover ? "on" : ""}" data-action="cover" data-owner="${item.id}" data-file="${h(img.file)}" title="Titelbild dieser Szene / dieses Assets">★</button>
      <button class="${isRef ? "on" : ""}" data-action="toggle-ref" data-owner="${item.id}" data-file="${h(img.file)}" title="Als Referenz für weitere Bilder hier">Ref</button>
      <button class="${isStyle ? "on" : ""}" data-action="toggle-style-ref" data-file="${h(img.file)}" title="Als Stil-Referenz für alle Bilder">Stil</button>
      <button data-action="reuse-prompt" data-owner="${item.id}" data-file="${h(img.file)}" title="Prompt dieses Bildes übernehmen">⟲</button>
      <button class="danger" data-action="delete-image" data-owner="${item.id}" data-file="${h(img.file)}" title="Bild löschen">🗑</button>
    </div>
    <div class="cap">${h(img.model || "")} · ${h(img.aspect || "")} · ${h((img.at || "").slice(0, 16).replace("T", " "))}</div></div>`;
}

function viewGallery() {
  const f = state.galleryFilter.toLowerCase();
  const list = allImages().filter((i) => !f || `${i.owner.title || i.owner.name} ${i.prompt}`.toLowerCase().includes(f));
  return `<div class="row"><input id="gallery-text" placeholder="Nach Szene, Asset oder Prompt filtern …" value="${h(state.galleryFilter)}"></div>
    <div class="pics">${list.map((i) => `<div class="pic"><img src="${h(imgUrl(i.file))}" loading="lazy" data-action="zoom" data-owner="${i.owner.id}" data-file="${h(i.file)}" alt="">
      <div class="cap" style="padding-top:6px">${i.kind}: <a href="#" data-action="${i.kind === "Szene" ? "open-scene" : "goto-asset"}" data-id="${i.owner.id}" style="color:var(--accent2)">${h(i.owner.title || i.owner.name)}</a></div></div>`).join("")
      || '<div class="empty">Noch keine Bilder. Öffne eine Szene oder ein Asset und generiere eins.</div>'}</div>`;
}

function viewStory() {
  return `<div class="row"><span class="hint">docs/STORY.md · nur lesen, bearbeitet wird die Datei selbst</span><button class="fixed" data-action="reload-story">Neu laden</button></div>
    <article class="md card" id="story-text"><span class="spin"></span></article>`;
}

async function fillStory() {
  if (state.story === null) {
    try { state.story = await api("/api/story"); } catch (e) { state.story = "Konnte docs/STORY.md nicht laden: " + e.message; }
  }
  const box = $("#story-text");
  if (!box) return;
  if (window.marked) box.innerHTML = window.marked.parse(state.story);
  else { box.textContent = state.story; box.style.whiteSpace = "pre-wrap"; }
}

function viewStyle() {
  const s = state.board.style;
  const refs = s.refs.map((url) => `<span class="ref"><img src="${h(imgUrl(url))}" alt=""><button data-action="drop-style-ref" data-url="${h(url)}">×</button></span>`).join("");
  return `<section class="card">
    <h2>Stil-Vorgabe</h2>
    <p class="hint">Steht vor jedem Bild-Prompt, wenn „Stil“ angehakt ist. Hält die Bilder einheitlich.</p>
    <textarea data-bind="style::prompt" rows="7">${h(s.prompt)}</textarea>
    <label>Stil-Referenzen (gehen bei jedem Bild mit)</label>
    <div class="refs">${refs}<button data-action="pick-style-refs">+ Referenzbild</button></div>
    <h3>Gemini</h3>
    <p>${state.hasKey ? `Bilder über ${h(state.route)}.` : "Kein Zugang: GEMINI_API_KEY setzen oder gcloud anmelden (gcloud auth application-default login) und den Server neu starten."}
      ${state.modelError ? `<span class="error">${h(state.modelError)}</span>` : ""}</p>
    <p class="hint">Bildmodelle: ${state.models.map(h).join(", ") || "…"}. Die Flash-Modelle sind schnell und günstig, „pro“ ist genauer, besonders bei Text im Bild und bei Referenzen.</p>
    <p class="hint">Bilder landen in docs/story/images, die Daten in docs/story/board.json.</p>
  </section>`;
}

// ---------- overlays ----------

function closeOverlay() { const o = $("#overlay"); o.hidden = true; o.innerHTML = ""; }

function zoom(ownerId, file) {
  const item = owner(ownerId);
  const img = item?.images.find((i) => i.file === file);
  const o = $("#overlay");
  o.innerHTML = `<div class="lightbox"><img src="${h(imgUrl(file))}" alt="">
    <div class="meta">${h(img?.prompt || "")}${img?.text ? "\n\nGemini: " + h(img.text) : ""}</div>
    <div class="row"><span class="hint">${h(file)}</span><a class="fixed" href="${h(imgUrl(file))}" download><button>Herunterladen</button></a><button class="fixed" data-action="close">Schließen</button></div></div>`;
  o.hidden = false;
}

function pickRefs(target) {
  // target: a scene or asset, or the style ({refs})
  const own = allImages().map((i) => ({ url: i.file, label: i.owner.title || i.owner.name }));
  const all = [...own, ...state.shots];
  const o = $("#overlay");
  const draw = () => {
    o.innerHTML = `<div class="picker"><div class="row"><h2>Referenzbilder wählen</h2><button class="fixed primary" data-action="close">Fertig</button></div>
      <p class="hint">Gemini bekommt die Bilder vor dem Prompt mit: für gleiche Figuren, Orte und Stimmung. Höchstens acht pro Bild.</p>
      <div class="pics">${all.map((r) => `<div class="pic ${target.refs.includes(r.url) ? "sel" : ""}"><img src="${h(imgUrl(r.url))}" data-pick="${h(r.url)}" alt="">
        <div class="cap" style="padding-top:6px">${h(r.label)}</div></div>`).join("") || '<div class="empty">Noch keine Bilder.</div>'}</div></div>`;
  };
  o.onclick = (e) => {
    const url = e.target.dataset?.pick;
    if (!url) return;
    target.refs = target.refs.includes(url) ? target.refs.filter((u) => u !== url) : [...target.refs, url];
    save(); draw();
  };
  draw();
  o.hidden = false;
}

// ---------- making pictures ----------

async function generate(item) {
  const own = (item.prompt || "").trim();
  if (!own) { state.errors[item.id] = "Erst einen Prompt schreiben."; render(); return; }
  const style = state.board.style;
  const prompt = prefs.useStyle && style.prompt.trim() ? `${style.prompt.trim()}\n\n${own}` : own;
  const refs = [...(prefs.useStyle ? style.refs : []), ...item.refs].map(imgUrl).slice(0, 8);
  const count = Number(prefs.count) || 1;
  const request = { prompt, model: prefs.model, aspect: prefs.aspect, size: prefs.size || undefined, refs };
  delete state.errors[item.id];
  state.busy[item.id] = (state.busy[item.id] || 0) + count;
  render();
  await Promise.all(Array.from({ length: count }, async () => {
    try {
      const result = await api("/api/generate", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(request) });
      for (const file of result.images) {
        item.images.push({ file, prompt: own, style: prefs.useStyle, model: prefs.model, aspect: prefs.aspect, refs, text: result.text, at: new Date().toISOString() });
      }
      save();
    } catch (error) {
      state.errors[item.id] = error.message;
    } finally {
      state.busy[item.id] -= 1;
      render();
    }
  }));
}

async function deleteImage(item, file) {
  if (!confirm("Bild löschen? Die Datei wird aus docs/story/images entfernt.")) return;
  item.images = item.images.filter((i) => i.file !== file);
  if (item.cover === file) delete item.cover;
  const used = allImages().some((i) => i.file === file);
  for (const x of [...state.board.scenes, ...state.board.assets, state.board.style]) x.refs = x.refs.filter((u) => u !== file);
  if (!used) await api(`/images/${encodeURIComponent(file)}`, { method: "DELETE" }).catch(() => {});
  save(); render();
}

// ---------- editing ----------

function move(list, item, dir, sameGroup = () => true) {
  const peers = list.filter(sameGroup);
  const i = peers.indexOf(item), j = i + dir;
  if (j < 0 || j >= peers.length) return;
  const a = list.indexOf(peers[i]), b = list.indexOf(peers[j]);
  [list[a], list[b]] = [list[b], list[a]];
}

function newAsset(fields = {}) {
  const a = { id: uid("a"), name: "Neues Asset", kind: "modell", source: "meshy", status: "idee", text: "", sourcePrompt: "", notes: "", prompt: "", images: [], refs: [], ...fields };
  state.board.assets.push(a);
  return a;
}

const actions = {
  "add-act"() {
    const a = { id: uid("act"), title: "Neuer Akt", summary: "", notes: "" };
    state.board.acts.push(a);
    state.tab = "scenes"; state.scene = { kind: "act", id: a.id };
  },
  "add-scene"(d) {
    const s = { id: uid("s"), act: d.act, title: "Neue Szene", status: "idee", text: "", gameplay: "", mood: "", dialog: "", assets: [], prompt: "", images: [], refs: [] };
    state.board.scenes.push(s);
    state.tab = "scenes"; state.scene = { kind: "scene", id: s.id };
  },
  "open-scene"(d) { state.tab = "scenes"; state.scene = { kind: "scene", id: d.id }; },
  "select-scene"(d) { state.scene = { kind: "scene", id: d.id }; },
  "select-act"(d) { state.scene = { kind: "act", id: d.id }; },
  "move-scene"(d) { const s = scene(d.id); move(state.board.scenes, s, Number(d.dir), (x) => x.act === s.act); },
  "move-act"(d) { move(state.board.acts, act(d.id), Number(d.dir)); },
  "delete-scene"(d) {
    if (!confirm("Szene löschen? Ihre Bilder bleiben als Dateien liegen.")) return false;
    state.board.scenes = state.board.scenes.filter((s) => s.id !== d.id); state.scene = null;
  },
  "delete-act"(d) {
    if (scenesOf(d.id).length && !confirm("Akt löschen? Seine Szenen stehen danach unter „Ohne Akt“.")) return false;
    state.board.acts = state.board.acts.filter((a) => a.id !== d.id); state.scene = null;
  },
  "add-asset"() { state.asset = newAsset().id; },
  "select-asset"(d) { state.asset = d.id; },
  "goto-asset"(d) { state.tab = "assets"; state.asset = d.id; state.assetFilter = { kind: "", status: "", text: "" }; },
  "delete-asset"(d) {
    if (!confirm("Asset löschen?")) return false;
    state.board.assets = state.board.assets.filter((a) => a.id !== d.id);
    for (const s of state.board.scenes) s.assets = s.assets.filter((id) => id !== d.id);
    state.asset = null;
  },
  "link-asset"(d) { const id = $("#link-asset").value; if (!id) return false; scene(d.scene).assets.push(id); },
  "unlink-asset"(d) { const s = scene(d.scene); s.assets = s.assets.filter((id) => id !== d.id); },
  "new-asset-for"(d) { const a = newAsset(); scene(d.scene).assets.push(a.id); state.tab = "assets"; state.asset = a.id; },
  "generate"(d) { generate(owner(d.owner)); return false; },
  "cover"(d) { owner(d.owner).cover = d.file; },
  "toggle-ref"(d) { const o = owner(d.owner); o.refs = o.refs.includes(d.file) ? o.refs.filter((u) => u !== d.file) : [...o.refs, d.file]; },
  "toggle-style-ref"(d) { const s = state.board.style; s.refs = s.refs.includes(d.file) ? s.refs.filter((u) => u !== d.file) : [...s.refs, d.file]; },
  "drop-ref"(d) { const o = owner(d.owner); o.refs = o.refs.filter((u) => u !== d.url); },
  "drop-style-ref"(d) { const s = state.board.style; s.refs = s.refs.filter((u) => u !== d.url); },
  "reuse-prompt"(d) { const o = owner(d.owner); const img = o.images.find((i) => i.file === d.file); if (img) o.prompt = img.prompt; },
  "delete-image"(d) { deleteImage(owner(d.owner), d.file); return false; },
  "pick-refs"(d) { pickRefs(owner(d.owner)); return false; },
  "pick-style-refs"() { pickRefs(state.board.style); return false; },
  "zoom"(d) { zoom(d.owner, d.file); return false; },
  "close"() { closeOverlay(); render(); return false; },
  "reload-story"() { state.story = null; },
};

document.addEventListener("click", (e) => {
  const tab = e.target.closest("#tabs button");
  if (tab) {
    state.tab = tab.dataset.tab;
    try { localStorage.setItem("storyboard.tab", state.tab); } catch { /* ignore */ }
    window.scrollTo(0, 0); render(); return;
  }
  const el = e.target.closest("[data-action]");
  if (!el) {
    if (e.target.id === "overlay") { closeOverlay(); render(); }
    return;
  }
  e.preventDefault();
  if (el.tagName !== "BUTTON" && e.target.closest("button") && e.target.closest("button") !== el) return;
  const result = actions[el.dataset.action]?.(el.dataset);
  if (result === false) return;
  save(); render();
});

function bindTarget(spec) {
  const [kind, id] = spec.split(":");
  return { act, scene, asset }[kind]?.(id) || (kind === "style" ? state.board.style : null);
}

document.addEventListener("input", (e) => {
  const el = e.target;
  if (el.dataset.bind) {
    const field = el.dataset.bind.split(":")[2];
    const target = bindTarget(el.dataset.bind);
    if (!target) return;
    target[field] = el.value;
    save();
    if (field === "title" || field === "name") {
      const head = el.closest(".card")?.querySelector("h2");
      if (head) head.textContent = el.value;
    }
    return;
  }
  if (el.id === "asset-text") { state.assetFilter.text = el.value; rerenderKeepingFocus(el); }
  if (el.id === "gallery-text") { state.galleryFilter = el.value; rerenderKeepingFocus(el); }
});

document.addEventListener("change", (e) => {
  const el = e.target;
  if (el.dataset.bind && el.hasAttribute("data-rerender")) { render(); return; }
  if (el.dataset.pref) {
    prefs[el.dataset.pref] = el.type === "checkbox" ? el.checked : el.value;
    savePrefs(); render(); return;
  }
  if (el.id === "asset-kind") { state.assetFilter.kind = el.value; render(); }
  if (el.id === "asset-status") { state.assetFilter.status = el.value; render(); }
});

function rerenderKeepingFocus(el) {
  const id = el.id, pos = el.selectionStart;
  render();
  const again = document.getElementById(id);
  if (again) { again.focus(); again.setSelectionRange(pos, pos); }
}

document.addEventListener("keydown", (e) => { if (e.key === "Escape" && !$("#overlay").hidden) { closeOverlay(); render(); } });

load().catch((error) => { $("#main").innerHTML = `<div class="empty error">Konnte das Story-Board nicht laden: ${h(error.message)}</div>`; });
