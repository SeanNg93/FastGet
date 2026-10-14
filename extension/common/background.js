// FastGet browser integration (Chrome MV3 service worker and Firefox MV2 background page).
// Hands downloads, links and captured videos to the desktop app through native messaging.
const api = globalThis.browser ?? globalThis.chrome;
const action = api.action ?? api.browserAction;
const HOST = "com.fastget.host";

// Keep in sync with AppSettings.AutoExtensions; '*' is a prefix wildcard.
const CAPTURE_EXTENSIONS = (
  "3gp 7z aac apk avi bin bz2 deb dmg epub exe flac flv gz gzip img iso jar m4a m4v mkv mov mp3 mp4 mpeg mpg " +
  "msi msu ogg ogv pdf ppt pptx rar rmvb rpm tar tgz torrent wav webm wma wmv xz zip r0* r1*"
).split(" ");

// Downloads the user chose to let the browser handle (host missing, or retried after a failure).
const passThrough = new Set();

function extensionOf(name) {
  const clean = (name || "").split(/[?#]/)[0];
  const base = clean.split(/[\\/]/).pop();
  const dot = base.lastIndexOf(".");
  return dot > 0 ? base.substring(dot + 1).toLowerCase() : "";
}

function baseName(path) {
  return (path || "").split(/[\\/]/).pop();
}

function shouldCapture(item) {
  const ext = extensionOf(item.filename) || extensionOf(item.finalUrl || item.url);
  if (!ext) return /^application\/(octet-stream|x-msdownload|zip|x-zip|x-rar|x-7z)/.test(item.mime || "");
  return CAPTURE_EXTENSIONS.some(p => (p.endsWith("*") ? ext.startsWith(p.slice(0, -1)) : ext === p));
}

async function cookiesFor(url) {
  try {
    const cookies = await api.cookies.getAll({ url });
    return cookies.map(c => `${c.name}=${c.value}`).join("; ");
  } catch {
    return "";
  }
}

async function sendToApp(message) {
  try {
    const reply = await api.runtime.sendNativeMessage(HOST, message);
    return Boolean(reply && reply.ok);
  } catch (e) {
    console.warn("FastGet host unavailable:", e && e.message);
    return false;
  }
}

async function isEnabled() {
  const { enabled = true } = await api.storage.local.get("enabled");
  return enabled;
}

async function updateBadge() {
  const enabled = await isEnabled();
  await action.setBadgeText({ text: enabled ? "" : "OFF" });
  await action.setBadgeBackgroundColor({ color: "#888" });
  await action.setTitle({
    title: enabled ? "FastGet: capturing downloads (click to turn off)" : "FastGet: capture is off (click to turn on)",
  });
}

// ---------- downloads ----------

api.downloads.onCreated.addListener(async item => {
  const url = item.finalUrl || item.url;
  if (!/^https?:/i.test(url) || passThrough.delete(url)) return;
  if (!(await isEnabled()) || !shouldCapture(item)) return;

  // Hold the browser download while asking the app; fall back to the browser if the app is not reachable.
  await api.downloads.pause(item.id).catch(() => {});
  const ok = await sendToApp({
    type: "download",
    url,
    referer: item.referrer || "",
    filename: baseName(item.filename),
    cookies: await cookiesFor(url),
    userAgent: navigator.userAgent,
    size: item.totalBytes > 0 ? item.totalBytes : null,
  });
  if (ok) {
    await api.downloads.cancel(item.id).catch(() => {});
    await api.downloads.erase({ id: item.id }).catch(() => {});
  } else {
    await api.downloads.resume(item.id).catch(() => {});
  }
});

// ---------- video capture ----------
// Media responses are classified by Content-Type and URL, per tab. HLS playlists and whole video files are kept;
// individual stream segments are ignored because the playlist already describes them.

const MAX_PER_TAB = 30;
const MIN_FILE_BYTES = 300 * 1024;
const media = new Map(); // tabId -> Map(url -> entry)
const session = api.storage.session; // Chrome: survives service worker restarts; Firefox MV2 page is persistent

function header(details, name) {
  const h = (details.responseHeaders || []).find(x => x.name.toLowerCase() === name);
  return h ? h.value || "" : "";
}

function classify(url, type) {
  const path = new URL(url).pathname.toLowerCase();
  type = type.toLowerCase();
  if (type.includes("mpegurl") || path.endsWith(".m3u8")) return "hls";
  if (type.includes("mp2t") || /\.(ts|m4s|m4f|aac|vtt|webvtt)$/.test(path)) return null; // segments, subtitles
  if (type.includes("dash+xml") || path.endsWith(".mpd")) return null; // DASH not supported by the app yet
  if (type.startsWith("video/") || /\.(mp4|webm|mkv|flv|mov|m4v|3gp|avi|wmv)$/.test(path)) return "file";
  if (type.startsWith("audio/") && /\.(mp3|m4a|ogg|opus|flac|wav)$/.test(path)) return "file";
  return null;
}

async function tabMedia(tabId) {
  let map = media.get(tabId);
  if (!map) {
    map = new Map();
    if (session) {
      const stored = (await session.get(`media:${tabId}`))[`media:${tabId}`] || [];
      for (const e of stored) map.set(e.url, e);
    }
    media.set(tabId, map);
  }
  return map;
}

async function saveTabMedia(tabId) {
  if (session) await session.set({ [`media:${tabId}`]: [...(media.get(tabId) || new Map()).values()] }).catch(() => {});
}

async function clearTab(tabId) {
  media.delete(tabId);
  if (session) await session.remove(`media:${tabId}`).catch(() => {});
}

api.webRequest.onHeadersReceived.addListener(
  details => {
    if (details.tabId < 0 || details.statusCode >= 400 || !/^https?:/i.test(details.url)) return;
    const kind = classify(details.url, header(details, "content-type"));
    if (!kind) return;
    let size = -1;
    const range = header(details, "content-range").match(/\/(\d+)$/);
    if (range) size = Number(range[1]);
    else if (header(details, "content-length")) size = Number(header(details, "content-length"));
    if (kind === "file" && size >= 0 && size < MIN_FILE_BYTES) return; // previews, thumbnails, beacons
    (async () => {
      const map = await tabMedia(details.tabId);
      if (map.has(details.url)) return;
      map.set(details.url, { url: details.url, kind, type: header(details, "content-type"), size, at: Date.now() });
      while (map.size > MAX_PER_TAB) map.delete(map.keys().next().value);
      await saveTabMedia(details.tabId);
      api.tabs.sendMessage(details.tabId, { type: "fastget-media", count: map.size }).catch(() => {});
    })();
  },
  { urls: ["http://*/*", "https://*/*"], types: ["media", "xmlhttprequest", "object", "other"] },
  ["responseHeaders"],
);

api.tabs.onUpdated.addListener((tabId, change) => {
  if (change.url) clearTab(tabId); // navigated to another page
});
api.tabs.onRemoved.addListener(tabId => clearTab(tabId));

async function sendVideo(tab, extraUrl, title) {
  const map = await tabMedia(tab.id);
  const streams = [...map.values()].sort((a, b) => (a.kind === b.kind ? b.at - a.at : a.kind === "hls" ? -1 : 1));
  if (extraUrl && /^https?:/i.test(extraUrl) && !map.has(extraUrl)) streams.push({ url: extraUrl, kind: "file", size: -1 });
  if (streams.length === 0) return false;
  return sendToApp({
    type: "video",
    title: title || tab.title || "",
    referer: tab.url || "",
    cookies: await cookiesFor(streams[0].url),
    userAgent: navigator.userAgent,
    streams: streams.map(s => ({ url: s.url, kind: s.kind, size: s.size > 0 ? s.size : null })),
  });
}

api.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  if (!sender.tab) return false;
  if (msg.type === "fastget-count") {
    tabMedia(sender.tab.id).then(map => sendResponse({ count: map.size }));
    return true;
  }
  if (msg.type === "fastget-video") {
    sendVideo(sender.tab, msg.src, msg.title).then(ok => sendResponse({ ok }));
    return true;
  }
  return false;
});

// ---------- context menus ----------

function createMenus() {
  api.contextMenus.removeAll().then(() => {
    api.contextMenus.create({ id: "link", title: "Download with FastGet", contexts: ["link"] });
    api.contextMenus.create({ id: "media", title: "Download with FastGet", contexts: ["video", "audio", "image"] });
    api.contextMenus.create({ id: "all", title: "Download all links with FastGet", contexts: ["page", "selection"] });
  });
}

api.runtime.onInstalled.addListener(() => {
  createMenus();
  updateBadge();
});
api.runtime.onStartup.addListener(() => {
  createMenus();
  updateBadge();
});

api.contextMenus.onClicked.addListener(async (info, tab) => {
  const referer = (tab && tab.url) || info.pageUrl || "";
  if (info.menuItemId === "media" && info.mediaType !== "image" && tab) {
    // Players usually use blob: URLs; the real stream is what the page fetched.
    if (await sendVideo(tab, info.srcUrl, tab.title)) return;
  }
  if (info.menuItemId === "link" || info.menuItemId === "media") {
    const url = info.menuItemId === "link" ? info.linkUrl : info.srcUrl;
    if (!url || !/^https?:/i.test(url)) return;
    const ok = await sendToApp({
      type: "download", url, referer, cookies: await cookiesFor(url), userAgent: navigator.userAgent,
    });
    if (!ok) {
      passThrough.add(url);
      api.downloads.download({ url });
    }
    return;
  }

  if (info.menuItemId === "all" && tab && tab.id !== undefined) {
    const [result] = await api.scripting.executeScript({
      target: { tabId: tab.id },
      func: () => {
        const sel = window.getSelection();
        const inSelection = a => !sel || sel.isCollapsed || sel.containsNode(a, true);
        const links = [...document.querySelectorAll("a[href]")].filter(inSelection)
          .map(a => ({ url: a.href, text: (a.textContent || "").trim().slice(0, 200) }));
        const mediaLinks = [...document.querySelectorAll("video[src], audio[src], source[src]")]
          .map(m => ({ url: m.src, text: "media" }));
        return links.concat(mediaLinks);
      },
    });
    const links = ((result && result.result) || []).filter(l => /^https?:/i.test(l.url));
    if (links.length === 0) return;
    await sendToApp({
      type: "links", links, referer, cookies: await cookiesFor(referer), userAgent: navigator.userAgent,
    });
  }
});

action.onClicked.addListener(async () => {
  await api.storage.local.set({ enabled: !(await isEnabled()) });
  await updateBadge();
});
