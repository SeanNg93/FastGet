// Shows a "Download this video" button over playing videos, like a download manager's video panel.
(() => {
  const api = globalThis.browser ?? globalThis.chrome;
  if (window.__fastgetVideoPanel) return;
  window.__fastgetVideoPanel = true;

  let current = null;
  let hideTimer = 0;
  let mediaCount = 0;

  // Shadow DOM keeps page CSS from restyling the button.
  const host = document.createElement("fastget-video-panel");
  host.style.cssText = "all:initial;position:absolute;z-index:2147483647;display:none;";
  const root = host.attachShadow({ mode: "closed" });
  root.innerHTML = `
    <style>
      button { font: 600 12px/1 system-ui, "Segoe UI", sans-serif; color: #fff; background: #0a6cd8; border: 0;
               border-radius: 4px; padding: 7px 10px 7px 8px; cursor: pointer; box-shadow: 0 2px 8px rgba(0,0,0,.35);
               display: flex; align-items: center; gap: 6px; opacity: .92; }
      button:hover { opacity: 1; background: #0b5fc0; }
      button.busy { background: #555; }
      svg { width: 14px; height: 14px; }
    </style>
    <button type="button" title="Send this video to FastGet">
      <svg viewBox="0 0 16 16" fill="currentColor"><path d="M8 1a1 1 0 0 1 1 1v7.6l2.3-2.3a1 1 0 1 1 1.4 1.4l-4 4a1 1 0 0 1-1.4 0l-4-4a1 1 0 1 1 1.4-1.4L7 9.6V2a1 1 0 0 1 1-1zM2 13a1 1 0 0 1 1 1h10a1 1 0 1 1 0 2H3a2 2 0 0 1-2-2 1 1 0 0 1 1-1z"/></svg>
      <span>Download this video</span>
    </button>`;
  const button = root.querySelector("button");
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
    return r.width >= 200 && r.height >= 120 && (mediaCount > 0 || directSource(video));
  }

  function place(video) {
    const r = video.getBoundingClientRect();
    host.style.left = `${window.scrollX + r.right - 190}px`;
    host.style.top = `${window.scrollY + r.top + 8}px`;
  }

  async function show(video) {
    clearTimeout(hideTimer);
    if (mediaCount === 0) {
      try { mediaCount = (await api.runtime.sendMessage({ type: "fastget-count" })).count || 0; } catch { mediaCount = 0; }
    }
    if (!worthShowing(video)) return;
    current = video;
    attach();
    place(video);
    host.style.display = "block";
  }

  function scheduleHide() {
    clearTimeout(hideTimer);
    hideTimer = setTimeout(() => { host.style.display = "none"; }, 2500);
  }

  button.addEventListener("click", async e => {
    e.preventDefault();
    e.stopPropagation();
    if (!current) return;
    button.classList.add("busy");
    label.textContent = "Sending to FastGet...";
    let ok = false;
    try {
      ok = (await api.runtime.sendMessage({ type: "fastget-video", src: directSource(current), title: document.title })).ok;
    } catch { ok = false; }
    label.textContent = ok ? "Sent to FastGet" : "FastGet is not available";
    setTimeout(() => {
      button.classList.remove("busy");
      label.textContent = "Download this video";
    }, 2500);
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
  window.addEventListener("scroll", () => { if (current && host.style.display !== "none") place(current); }, { passive: true });

  api.runtime.onMessage.addListener(msg => {
    if (msg.type === "fastget-media") mediaCount = msg.count;
  });
})();
