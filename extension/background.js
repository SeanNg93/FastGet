// FastGet browser integration: hands downloads and links to the desktop app through native messaging.
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
    const cookies = await chrome.cookies.getAll({ url });
    return cookies.map(c => `${c.name}=${c.value}`).join("; ");
  } catch {
    return "";
  }
}

function sendToApp(message) {
  return new Promise(resolve => {
    chrome.runtime.sendNativeMessage(HOST, message, reply => {
      if (chrome.runtime.lastError) {
        console.warn("FastGet host unavailable:", chrome.runtime.lastError.message);
        resolve(false);
      } else {
        resolve(Boolean(reply && reply.ok));
      }
    });
  });
}

async function isEnabled() {
  const { enabled = true } = await chrome.storage.local.get("enabled");
  return enabled;
}

async function updateBadge() {
  const enabled = await isEnabled();
  await chrome.action.setBadgeText({ text: enabled ? "" : "OFF" });
  await chrome.action.setBadgeBackgroundColor({ color: "#888" });
  await chrome.action.setTitle({
    title: enabled ? "FastGet: capturing downloads (click to turn off)" : "FastGet: capture is off (click to turn on)",
  });
}

chrome.downloads.onCreated.addListener(async item => {
  const url = item.finalUrl || item.url;
  if (!/^https?:/i.test(url) || passThrough.delete(url)) return;
  if (!(await isEnabled()) || !shouldCapture(item)) return;

  // Hold the browser download while asking the app; fall back to the browser if the app is not reachable.
  await chrome.downloads.pause(item.id).catch(() => {});
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
    await chrome.downloads.cancel(item.id).catch(() => {});
    await chrome.downloads.erase({ id: item.id }).catch(() => {});
  } else {
    await chrome.downloads.resume(item.id).catch(() => {});
  }
});

function createMenus() {
  chrome.contextMenus.removeAll(() => {
    chrome.contextMenus.create({ id: "link", title: "Download with FastGet", contexts: ["link"] });
    chrome.contextMenus.create({ id: "media", title: "Download with FastGet", contexts: ["video", "audio", "image"] });
    chrome.contextMenus.create({ id: "all", title: "Download all links with FastGet", contexts: ["page", "selection"] });
  });
}

chrome.runtime.onInstalled.addListener(() => {
  createMenus();
  updateBadge();
});
chrome.runtime.onStartup.addListener(updateBadge);

chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  const referer = (tab && tab.url) || info.pageUrl || "";
  if (info.menuItemId === "link" || info.menuItemId === "media") {
    const url = info.menuItemId === "link" ? info.linkUrl : info.srcUrl;
    if (!url || !/^https?:/i.test(url)) return;
    const ok = await sendToApp({
      type: "download", url, referer, cookies: await cookiesFor(url), userAgent: navigator.userAgent,
    });
    if (!ok) {
      passThrough.add(url);
      chrome.downloads.download({ url });
    }
    return;
  }

  if (info.menuItemId === "all" && tab && tab.id !== undefined) {
    const [result] = await chrome.scripting.executeScript({
      target: { tabId: tab.id },
      func: () => {
        const sel = window.getSelection();
        const inSelection = a => !sel || sel.isCollapsed || sel.containsNode(a, true);
        const links = [...document.querySelectorAll("a[href]")].filter(inSelection)
          .map(a => ({ url: a.href, text: (a.textContent || "").trim().slice(0, 200) }));
        const media = [...document.querySelectorAll("video[src], audio[src], source[src]")]
          .map(m => ({ url: m.src, text: "media" }));
        return links.concat(media);
      },
    });
    const links = ((result && result.result) || []).filter(l => /^https?:/i.test(l.url));
    if (links.length === 0) return;
    await sendToApp({
      type: "links", links, referer, cookies: await cookiesFor(referer), userAgent: navigator.userAgent,
    });
  }
});

chrome.action.onClicked.addListener(async () => {
  await chrome.storage.local.set({ enabled: !(await isEnabled()) });
  await updateBadge();
});
