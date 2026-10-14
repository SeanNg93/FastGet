// Shows a "Download this video" panel just above the top-right corner of a video, like a download manager's video panel.
(() => {
  const api = globalThis.browser ?? globalThis.chrome;
  if (window.__fastgetVideoPanel) return;
  window.__fastgetVideoPanel = true;

  const GAP = 4; // pixels between the panel and the video frame
  let current = null;
  let hideTimer = 0;
  let mediaCount = 0;
  const dismissed = new WeakSet(); // videos the user closed the panel for, until the page reloads

  // Shadow DOM keeps page CSS from restyling the panel.
  const host = document.createElement("fastget-video-panel");
  host.style.cssText = "all:initial;position:absolute;z-index:2147483647;display:none;";
  const root = host.attachShadow({ mode: "closed" });
  root.innerHTML = `
    <style>
      .panel { display: flex; align-items: stretch; font: 600 12px/1 system-ui, "Segoe UI", sans-serif;
               border-radius: 4px; overflow: hidden; box-shadow: 0 2px 8px rgba(0,0,0,.35); }
      button { font: inherit; color: #fff; background: #0a6cd8; border: 0; cursor: pointer; margin: 0; }
      button:hover { background: #0b5fc0; }
      .download { display: flex; align-items: center; gap: 6px; padding: 7px 10px 7px 8px; }
      .close { padding: 0 9px; font-size: 15px; font-weight: 400; border-left: 1px solid rgba(255,255,255,.35); }
      .busy .download { background: #555; }
      svg { width: 14px; height: 14px; }
    </style>
    <div class="panel">
      <button type="button" class="download" title="Send this video to FastGet">
        <svg viewBox="0 0 16 16" fill="currentColor"><path d="M8 1a1 1 0 0 1 1 1v7.6l2.3-2.3a1 1 0 1 1 1.4 1.4l-4 4a1 1 0 0 1-1.4 0l-4-4a1 1 0 1 1 1.4-1.4L7 9.6V2a1 1 0 0 1 1-1zM2 13a1 1 0 0 1 1 1h10a1 1 0 1 1 0 2H3a2 2 0 0 1-2-2 1 1 0 0 1 1-1z"/></svg>
        <span>Download this video</span>
      </button>
      <button type="button" class="close" title="Hide for this video" aria-label="Hide">×</button>
    </div>`;
  const panel = root.querySelector(".panel");
  const button = root.querySelector(".download");
  const close = root.querySelector(".close");
  const label = root.querySelector("span");

  function attach() {
    if (!host.isConnected) (document.body || document.documentElement).appendChild(host);
  }

  function directSource(video) {
    const src = video.currentSrc || video.src || "";
    return /^https?:/i.test(src) ? src : "";
  }

  function worthShowing(video) {
    const r = video.getBoundingClientRect();
    return !dismissed.has(video) && r.width >= 200 && r.height >= 120 && (mediaCount > 0 || directSource(video));
  }

  // Above the frame, right-aligned; inside the top edge only when there is no room above (video at the top of the page).
  function place(video) {
    const r = video.getBoundingClientRect();
    const w = panel.offsetWidth || 190;
    const h = panel.offsetHeight || 30;
    const roomAbove = r.top + window.scrollY >= h + GAP;
    host.style.left = `${Math.max(0, window.scrollX + r.right - w)}px`;
    host.style.top = `${window.scrollY + (roomAbove ? r.top - h - GAP : r.top + GAP)}px`;
  }

  async function show(video) {
    clearTimeout(hideTimer);
    if (mediaCount === 0) {
      try { mediaCount = (await api.runtime.sendMessage({ type: "fastget-count" })).count || 0; } catch { mediaCount = 0; }
    }
    if (!worthShowing(video)) return;
    current = video;
    attach();
    host.style.visibility = "hidden"; // measure before placing
    host.style.display = "block";
    place(video);
    host.style.visibility = "visible";
  }

  function hide() {
    host.style.display = "none";
  }

  function scheduleHide() {
    clearTimeout(hideTimer);
    hideTimer = setTimeout(hide, 2500);
  }

  button.addEventListener("click", async e => {
    e.preventDefault();
    e.stopPropagation();
    if (!current) return;
    panel.classList.add("busy");
    label.textContent = "Sending to FastGet...";
    let ok = false;
    try {
      ok = (await api.runtime.sendMessage({ type: "fastget-video", src: directSource(current), title: document.title })).ok;
    } catch { ok = false; }
    label.textContent = ok ? "Sent to FastGet" : "FastGet is not available";
    setTimeout(() => {
      panel.classList.remove("busy");
      label.textContent = "Download this video";
    }, 2500);
  });
  close.addEventListener("click", e => {
    e.preventDefault();
    e.stopPropagation();
    if (current) dismissed.add(current);
    hide();
  });
  host.addEventListener("mouseenter", () => clearTimeout(hideTimer));
  host.addEventListener("mouseleave", scheduleHide);

  // Event delegation catches videos added later by single-page players.
  document.addEventListener("mouseover", e => {
    const video = e.target instanceof Element ? e.target.closest("video") : null;
    if (video) show(video);
  }, true);
  document.addEventListener("mouseout", e => {
    if (e.target instanceof Element && e.target.closest("video")) scheduleHide();
  }, true);
  document.addEventListener("play", e => {
    if (e.target instanceof HTMLVideoElement) {
      show(e.target);
      scheduleHide();
    }
  }, true);
  const reposition = () => { if (current && host.style.display !== "none") place(current); };
  window.addEventListener("scroll", reposition, { passive: true });
  window.addEventListener("resize", reposition, { passive: true });
  document.addEventListener("fullscreenchange", hide);

  api.runtime.onMessage.addListener(msg => {
    if (msg.type === "fastget-media") mediaCount = msg.count;
  });
})();
