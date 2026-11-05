const wsUrlInput = document.getElementById("wsUrl");
const fpsInput = document.getElementById("fpsInput");
const codecSelect = document.getElementById("codecSelect");
const inputSelect = document.getElementById("inputSelect");
const playoutCheck = document.getElementById("playoutCheck");
const burstInput = document.getElementById("burstInput");
const connectBtn = document.getElementById("connectBtn");
const disconnectBtn = document.getElementById("disconnectBtn");
const statusEl = document.getElementById("status");
const videoEl = document.getElementById("remoteVideo");
const logEl = document.getElementById("log");

wsUrlInput.value = localStorage.getItem('ramune314159265.remotesimpleclient.lasturl') ?? `ws://${location.hostname}:8787/signaling`

let burstPending = false;

let ws = null;
let pc = null;
let controlChannel = null;

function log(...args) {
  const line = args.map(a => (typeof a === "string" ? a : JSON.stringify(a))).join(" ");
  console.log(line);
  logEl.textContent += line + "\n";
  logEl.scrollTop = logEl.scrollHeight;
}

function setStatus(text, kind) {
  statusEl.textContent = text;
  statusEl.className = `status status--${kind}`;
}

function createPeerConnection() {
  const peer = new RTCPeerConnection({
    iceServers: [{ urls: "stun:stun.l.google.com:19302" }],
  });

  peer.addTransceiver("video", { direction: "recvonly" });
  peer.addTransceiver("audio", { direction: "recvonly" });

  peer.ontrack = (event) => {
    log("Track received:", event.track.kind);
    if (videoEl.srcObject !== event.streams[0]) {
      videoEl.srcObject = event.streams[0];
    }

    try {
      if ("jitterBufferTarget" in event.receiver) {
        event.receiver.jitterBufferTarget = 0;
      }
    } catch { }
  };

  peer.ondatachannel = (event) => {
    controlChannel = event.channel;
    controlChannel.onopen = () => {
      log("Control channel open");
      scheduleSendViewportSize();
      sendControl({ type: "listSources" });
    };
    controlChannel.onclose = () => log("Control channel closed");
    controlChannel.onerror = (e) => log("Control channel error:", e.message ?? e);
    controlChannel.onmessage = (e) => handleControlChannelMessage(e.data);
  };

  peer.onicecandidate = (event) => {
    if (event.candidate) {
      sendMessage({
        type: "ice",
        candidate: event.candidate.candidate,
        sdpMLineIndex: event.candidate.sdpMLineIndex,
      });
    }
  };

  peer.onconnectionstatechange = () => {
    log("PeerConnection state:", peer.connectionState);
    if (peer.connectionState === "connected") {
      setStatus("接続済み", "connected");
    } else if (["disconnected", "failed", "closed"].includes(peer.connectionState)) {
      setStatus("切断", "idle");
    }
  };

  return peer;
}

function sendMessage(obj) {
  if (ws && ws.readyState === WebSocket.OPEN) {
    ws.send(JSON.stringify(obj));
  }
}

async function handleSignalingMessage(msg) {
  if (msg.type === "offer") {
    log("Offer received");
    await pc.setRemoteDescription({ type: "offer", sdp: msg.sdp });
    const answer = await pc.createAnswer();
    await pc.setLocalDescription(answer);
    sendMessage({ type: "answer", sdp: answer.sdp });
    log("Answer sent");
  } else if (msg.type === "ice") {
    try {
      await pc.addIceCandidate({
        candidate: msg.candidate,
        sdpMLineIndex: msg.sdpMLineIndex,
      });
    } catch (err) {
      log("Failed to add ICE candidate:", err.message);
    }
  } else {
    log("Unknown message type:", msg.type);
  }
}

function connect() {
  const baseUrl = wsUrlInput.value.trim();
  if (!baseUrl) return;
  localStorage.setItem('ramune314159265.remotesimpleclient.lasturl', baseUrl)

  const fps = parseInt(fpsInput.value, 10) || 30;
  const codec = codecSelect.value;
  const separator = baseUrl.includes("?") ? "&" : "?";
  const url = `${baseUrl}${separator}fps=${fps}&codec=${encodeURIComponent(codec)}&playout=${playoutCheck.checked ? 1 : 0}&bitrate=${parseInt(bitrateInput.value, 10) || 6000}&burst=${parseInt(burstInput.value, 10) || 0}`;

  burstPending = playoutCheck.checked && (parseInt(burstInput.value, 10) || 0) > 0;
  updateVideoMuted();

  setStatus("接続中...", "connecting");
  pc = createPeerConnection();

  ws = new WebSocket(url);

  ws.onopen = () => {
    log("Signaling connected:", url);
  };

  ws.onmessage = (event) => {
    let msg;
    try {
      msg = JSON.parse(event.data);
    } catch {
      log("Invalid JSON from server:", event.data);
      return;
    }
    handleSignalingMessage(msg);
  };

  ws.onerror = () => {
    setStatus("エラー", "error");
  };

  ws.onclose = () => {
    log("Signaling closed");
    setStatus("未接続", "idle");
    cleanupPeer();
  };

  connectBtn.disabled = true;
  disconnectBtn.disabled = false;
}

function cleanupPeer() {
  burstPending = false;
  if (pc) {
    pc.close();
    pc = null;
  }
  controlChannel = null;
  videoEl.srcObject = null;
}

function disconnect() {
  if (ws) {
    ws.close();
    ws = null;
  }
  cleanupPeer();
  connectBtn.disabled = false;
  disconnectBtn.disabled = true;
  setStatus("未接続", "idle");
}

videoEl.addEventListener("loadedmetadata", updateVideoAspectRatio);
videoEl.addEventListener("resize", updateVideoAspectRatio);

function updateVideoAspectRatio() {
  log(`Video size: ${videoEl.videoWidth}x${videoEl.videoHeight}`)
  if (videoEl.videoWidth && videoEl.videoHeight) {
    videoEl.style.aspectRatio = `${videoEl.videoWidth} / ${videoEl.videoHeight}`;
  }
}

let viewportDebounceTimer = null;

function scheduleSendViewportSize() {
  clearTimeout(viewportDebounceTimer);
  viewportDebounceTimer = setTimeout(sendViewportSize, 1000);
}

function sendViewportSize() {
  const rect = getVideoContentRect();
  const dpr = window.devicePixelRatio || 1;
  const width = Math.round(rect.width * dpr);
  const height = Math.round(rect.height * dpr);
  if (width > 0 && height > 0) {
    sendViewportSizeIfChanged(width, height);
  }
}

const VIEWPORT_SIZE_TOLERANCE_PX = 2;
let lastSentViewportSize = null;

function sendViewportSizeIfChanged(width, height) {
  if (
    lastSentViewportSize &&
    Math.abs(width - lastSentViewportSize.width) < VIEWPORT_SIZE_TOLERANCE_PX &&
    Math.abs(height - lastSentViewportSize.height) < VIEWPORT_SIZE_TOLERANCE_PX
  ) {
    return;
  }
  lastSentViewportSize = { width, height };
  sendControl({ type: "setViewportSize", width, height });
}

window.addEventListener("resize", scheduleSendViewportSize);
videoEl.addEventListener("loadedmetadata", scheduleSendViewportSize);

/**
 * 現在のレイアウトで video 要素に使える最大の表示領域(デバイスピクセル単位)を返す。
 * ホスト側ウィンドウが自発的にリサイズした直後は、まだ新しい解像度の映像フレームが
 * 届いておらず videoEl の実サイズからは逆算できないため、CSS上の制約
 * (コンテナ幅 / max-height:80vh)から独立に計算する。
 */
function getAvailableBoxSizePx() {
  const dpr = window.devicePixelRatio || 1;
  const containerWidthCss = videoEl.parentElement?.clientWidth ?? videoEl.clientWidth;
  const maxHeightCss = videoEl.parentElement?.clientHeight ?? videoEl.clientHeight;
  return {
    width: Math.round(containerWidthCss * dpr),
    height: Math.round(maxHeightCss * dpr),
  };
}

/** 指定したアスペクト比(aspectW:aspectH)を、maxWidth x maxHeight の箱に contain でフィットさせる。 */
function computeFitSize(maxWidth, maxHeight, aspectW, aspectH) {
  const aspect = aspectW / aspectH;
  let w = maxWidth;
  let h = Math.round(w / aspect);
  if (h > maxHeight) {
    h = maxHeight;
    w = Math.round(h * aspect);
  }
  return { width: Math.max(1, w), height: Math.max(1, h) };
}

connectBtn.addEventListener("click", connect);
disconnectBtn.addEventListener("click", disconnect);

function sendControl(msg) {
  if (controlChannel && controlChannel.readyState === "open") {
    controlChannel.send(JSON.stringify(msg));
  }
}

function buttonName(button) {
  if (button === 0) return "left";
  if (button === 1) return "middle";
  if (button === 2) return "right";
  return "left";
}

/**
 * <video> 要素の実際の描画ボックス(rect)は、動画の実解像度とアスペクト比が
 * 異なる場合、レターボックス(上下黒帯) or ピラーボックス(左右黒帯)を含む。
 * 黒帯部分を含めて正規化座標を計算すると、縦長/横長になるほど誤差が大きくなる
 * (実際に報告された症状)。videoWidth/videoHeight(動画の実解像度)を使って、
 * 黒帯を除いた「実際に映像が描画されている矩形」を計算する。
 */
function getVideoContentRect() {
  const rect = videoEl.getBoundingClientRect();
  const videoW = videoEl.videoWidth;
  const videoH = videoEl.videoHeight;

  if (!videoW || !videoH) {
    return rect;
  }

  const videoRatio = videoW / videoH;
  const boxRatio = rect.width / rect.height;

  let contentWidth, contentHeight;
  if (boxRatio > videoRatio) {
    contentHeight = rect.height;
    contentWidth = rect.height * videoRatio;
  } else {
    contentWidth = rect.width;
    contentHeight = rect.width / videoRatio;
  }

  const offsetX = (rect.width - contentWidth) / 2;
  const offsetY = (rect.height - contentHeight) / 2;

  return {
    left: rect.left + offsetX,
    top: rect.top + offsetY,
    width: contentWidth,
    height: contentHeight,
  };
}

videoEl.tabIndex = 0;
videoEl.addEventListener("click", () => videoEl.focus());
videoEl.addEventListener("contextmenu", (e) => e.preventDefault());

videoEl.addEventListener("mousemove", (e) => {
  const rect = getVideoContentRect();
  const x = (e.clientX - rect.left) / rect.width;
  const y = (e.clientY - rect.top) / rect.height;
  if (x < 0 || x > 1 || y < 0 || y > 1) return;
  sendControl({ type: "mouseMove", mode: "absolute", x, y });
});

videoEl.addEventListener("mousedown", (e) => {
  e.preventDefault();
  sendControl({ type: "mouseButton", button: buttonName(e.button), action: "down" });
});

videoEl.addEventListener("mouseup", (e) => {
  sendControl({ type: "mouseButton", button: buttonName(e.button), action: "up" });
});

videoEl.addEventListener(
  "wheel",
  (e) => {
    e.preventDefault();
    sendControl({ type: "mouseWheel", dx: -e.deltaX, dy: -e.deltaY });
  },
  { passive: false }
);

videoEl.addEventListener("keydown", (e) => {
  e.preventDefault();
  sendControl({ type: "keyDown", code: e.code });
});

videoEl.addEventListener("keyup", (e) => {
  e.preventDefault();
  sendControl({ type: "keyUp", code: e.code });
});

function touchListToDtos(touchList, phase) {
  const rect = getVideoContentRect();
  const dtos = [];
  for (const touch of touchList) {
    const x = Math.max(Math.min((touch.clientX - rect.left) / rect.width, 1), 0);
    const y = Math.max(Math.min((touch.clientY - rect.top) / rect.height, 1), 0);
    dtos.push({ id: touch.identifier, x, y, phase });
  }
  return dtos;
}

function sendTouchBatch(touchList, phase) {
  const touches = touchListToDtos(touchList, phase);
  if (touches.length === 0) return;
  sendControl({ type: "touch", touches });
}

videoEl.addEventListener(
  "touchstart",
  (e) => {
    e.preventDefault();
    if (inputSelect.value === 'mouse') {
      const rect = getVideoContentRect();
      const x = Math.max(Math.min((e.changedTouches[0].clientX - rect.left) / rect.width, 1), 0);
      const y = Math.max(Math.min((e.changedTouches[0].clientY - rect.top) / rect.height, 1), 0);
      sendControl({ type: "mouseMove", mode: "absolute", x, y });
      sendControl({ type: "mouseButton", button: "left", action: "down" });
      return
    }
    sendTouchBatch(e.changedTouches, "down");
  },
  { passive: false }
);

videoEl.addEventListener(
  "touchmove",
  (e) => {
    e.preventDefault();
    if (inputSelect.value === 'mouse') {
      const rect = getVideoContentRect();
      const x = Math.max(Math.min((e.changedTouches[0].clientX - rect.left) / rect.width, 1), 0);
      const y = Math.max(Math.min((e.changedTouches[0].clientY - rect.top) / rect.height, 1), 0);
      sendControl({ type: "mouseMove", mode: "absolute", x, y });
      return
    }
    sendTouchBatch(e.changedTouches, "move");
  },
  { passive: false }
);

videoEl.addEventListener(
  "touchend",
  (e) => {
    e.preventDefault();
    if (inputSelect.value === 'mouse') {
      const rect = getVideoContentRect();
      const x = Math.max(Math.min((e.changedTouches[0].clientX - rect.left) / rect.width, 1), 0);
      const y = Math.max(Math.min((e.changedTouches[0].clientY - rect.top) / rect.height, 1), 0);
      sendControl({ type: "mouseMove", mode: "absolute", x, y });
      sendControl({ type: "mouseButton", button: "left", action: "up" });
      return
    }
    sendTouchBatch(e.changedTouches, "up");
  },
  { passive: false }
);

videoEl.addEventListener(
  "touchcancel",
  (e) => {
    e.preventDefault();
    sendTouchBatch(e.changedTouches, "cancel");
  },
  { passive: false }
);

const openBtn = document.getElementById("openBtn");
const closeBtn = document.getElementById("closeBtn");
const bitrateInput = document.getElementById("bitrateInput");
const applyBitrateBtn = document.getElementById("applyBitrateBtn");
const muteCheckbox = document.getElementById("muteCheckbox");
const listSourcesBtn = document.getElementById("listSourcesBtn");
const sourceSelect = document.getElementById("sourceSelect");
const switchSourceBtn = document.getElementById("switchSourceBtn");
const fullscreenBtn = document.getElementById("fullscreenBtn");
const screenshotBtn = document.getElementById("screenshotBtn");
const forceFitBtn = document.getElementById("forceFitBtn");

applyBitrateBtn.addEventListener("click", () => {
  const kbps = parseInt(bitrateInput.value, 10);
  if (!Number.isFinite(kbps)) return;
  sendControl({ type: "setBitrate", bitrateKbps: kbps });
});

muteCheckbox.addEventListener("change", () => {
  sendControl({ type: "setMute", muted: muteCheckbox.checked });
  updateVideoMuted();
});

function updateVideoMuted() {
  videoEl.muted = burstPending || muteCheckbox.checked;
}

listSourcesBtn.addEventListener("click", () => {
  sendControl({ type: "listSources" });
});

switchSourceBtn.addEventListener("click", () => {
  const value = sourceSelect.value;
  if (!value) return;
  lastSentViewportSize = null;

  if (value === "none") {
    log("Switching to no-video (audio only)");
    sendControl({ type: "switchSource", sourceMode: "none" });
  } else if (value.startsWith("monitor:")) {
    const index = parseInt(value.slice("monitor:".length), 10);
    if (!Number.isFinite(index)) return;
    log(`Switching to monitor ${index} (数秒フリーズします)`);
    sendControl({ type: "switchSource", sourceMode: "monitor", monitorIndex: index });
  } else if (value.startsWith("window:")) {
    const handle = value.slice("window:".length);
    log(`Switching to window handle ${handle} (数秒フリーズします)`);
    sendControl({ type: "switchSource", sourceMode: "window", windowHandle: Number(handle) });
  }
});

function addOptionGroup(label, items) {
  const group = document.createElement("optgroup");
  group.label = label;
  for (const [value, text] of items) {
    const opt = document.createElement("option");
    opt.value = value;
    opt.textContent = text;
    group.appendChild(opt);
  }
  sourceSelect.appendChild(group);
}

function fullscreenElement() {
  return document.fullscreenElement ?? document.webkitFullscreenElement ?? null;
}

fullscreenBtn.addEventListener("click", () => {
  if (fullscreenElement()) {
    (document.exitFullscreen ?? document.webkitExitFullscreen)?.call(document);
    return;
  }
  const target = document.documentElement;
  const request = target.requestFullscreen ?? target.webkitRequestFullscreen;
  if (!request) {
    log("このブラウザは全画面表示に対応していません");
    return;
  }
  Promise.resolve(request.call(target)).catch((err) => log("全画面表示に失敗:", err.message ?? err));
});

function updateFullscreenButton() {
  fullscreenBtn.textContent = fullscreenElement() ? "全画面を終了" : "全画面表示";
}
document.addEventListener("fullscreenchange", updateFullscreenButton);
document.addEventListener("webkitfullscreenchange", updateFullscreenButton);

screenshotBtn.addEventListener("click", () => {
  log("スクリーンショットを要求しました");
  sendControl({ type: "screenshot" });
});

forceFitBtn.addEventListener("click", () => {
  const box = getAvailableBoxSizePx();
  log({ type: "setViewportSize", width: box.width, height: box.height })
  sendControl({ type: "setViewportSize", width: box.width, height: box.height });
});

function handleControlChannelMessage(data) {
  let msg;
  try {
    msg = JSON.parse(data);
  } catch {
    log("Invalid control channel message:", data);
    return;
  }

  if (msg.type === "sourceList") {
    sourceSelect.innerHTML = "";
    addOptionGroup(
      "モニター",
      msg.monitors.map((m) => [
        `monitor:${m.index}`,
        `${m.name} (${m.width}x${m.height})${m.isPrimary ? " [プライマリ]" : ""}`,
      ])
    );
    addOptionGroup(
      "ウィンドウ",
      msg.windows.map((w) => [`window:${w.handle}`, w.title])
    );
    addOptionGroup("その他", [["none", "映像なし(音声のみ)"]]);
    if (msg.current) sourceSelect.value = msg.current;
    log(`Source list received (${msg.monitors.length} monitors, ${msg.windows.length} windows)`);
  } else if (msg.type === "screenshotResult") {
    if (msg.ok) {
      log(`スクリーンショットを保存しました: ${msg.message}`);
    } else {
      log(`スクリーンショットに失敗しました: ${msg.message}`);
      alert(`スクリーンショットに失敗しました: ${msg.message}`);
    }
  } else if (msg.type === "burstEnd") {
    log("Test stream finished, switching to the real screen/audio");
    burstPending = false;
    updateVideoMuted();
  } else if (msg.type === "sourceError") {
    log("Source error:", msg.message);
    alert(msg.message);
  } else if (msg.type === "windowResized") {
    log(`Host window resized independently: ${msg.width}x${msg.height}`);
    const box = getAvailableBoxSizePx();
    const target = computeFitSize(box.width, box.height, msg.width, msg.height);
    log(`Requesting host to fit to: ${target.width}x${target.height}`);
    sendViewportSizeIfChanged(target.width, target.height);
  } else {
    log("Unknown control channel message:", data);
  }
}

openBtn.addEventListener('click', () => {
  document.querySelector('.side').classList.remove('closing')
})

closeBtn.addEventListener('click', () => {
  document.querySelector('.side').classList.add('closing')
})

document.addEventListener("visibilitychange", () => {
  if (document.visibilityState === "hidden") {
    videoEl.play().catch((err) => log("Background play failed:", err.message ?? err));
  }
});

const FREEZE_CHECK_INTERVAL_MS = 1000;
const FREEZE_THRESHOLD_MS = 4000;

let lastVideoTime = -1;
let lastVideoProgressAt = Date.now();
let freezeRecoveryInProgress = false;

setInterval(() => {
  if (!pc || pc.connectionState !== "connected") return;
  if (!videoEl.srcObject || videoEl.paused || videoEl.ended) return;

  if (videoEl.currentTime !== lastVideoTime) {
    lastVideoTime = videoEl.currentTime;
    lastVideoProgressAt = Date.now();
    return;
  }

  const stalledMs = Date.now() - lastVideoProgressAt;
  if (stalledMs > FREEZE_THRESHOLD_MS && !freezeRecoveryInProgress) {
    log(`Video appears frozen (${stalledMs}ms with no progress), attempting recovery`);
    recoverFrozenVideo();
  }
}, FREEZE_CHECK_INTERVAL_MS);

function recoverFrozenVideo() {
  freezeRecoveryInProgress = true;
  const stream = videoEl.srcObject;

  videoEl.srcObject = null;

  requestAnimationFrame(() => {
    videoEl.srcObject = stream;
    videoEl
      .play()
      .catch((err) => log("Freeze recovery play failed:", err.message ?? err));

    lastVideoTime = videoEl.currentTime;
    lastVideoProgressAt = Date.now();

    setTimeout(() => {
      freezeRecoveryInProgress = false;
    }, FREEZE_THRESHOLD_MS);
  });
}

const STATS_INTERVAL_MS = 5000;
const LAG_WARN_MS = 250;
const LAG_RECOVER_MS = 150;

let prevInbound = {};
let lagActive = false;

function averageJitterBufferDelayMs(cur, prev) {
  if (!cur || !prev) return null;
  const emitted = (cur.jitterBufferEmittedCount ?? 0) - (prev.jitterBufferEmittedCount ?? 0);
  const delay = (cur.jitterBufferDelay ?? 0) - (prev.jitterBufferDelay ?? 0);
  if (emitted <= 0) return null;
  return (delay / emitted) * 1000;
}

function delta(cur, prev, key) {
  if (!cur || !prev) return 0;
  return (cur[key] ?? 0) - (prev[key] ?? 0);
}

setInterval(async () => {
  if (!pc || pc.connectionState !== "connected") {
    prevInbound = {};
    lagActive = false;
    return;
  }

  let report;
  try {
    report = await pc.getStats();
  } catch {
    return;
  }

  const cur = {};
  let rttMs = null;
  report.forEach((s) => {
    if (s.type === "inbound-rtp" && !s.isRemote && (s.kind === "video" || s.kind === "audio")) {
      cur[s.kind] = s;
    } else if (s.type === "candidate-pair" && s.nominated && s.state === "succeeded") {
      if (s.currentRoundTripTime !== undefined) rttMs = s.currentRoundTripTime * 1000;
    }
  });

  const videoDelay = averageJitterBufferDelayMs(cur.video, prevInbound.video);
  const audioDelay = averageJitterBufferDelayMs(cur.audio, prevInbound.audio);

  if (videoDelay !== null) {
    const fmt = (v) => (v === null ? "n/a" : `${Math.round(v)}ms`);
    const detail =
      `video buffer=${fmt(videoDelay)}, audio buffer=${fmt(audioDelay)}, ` +
      `rtt=${rttMs === null ? "n/a" : Math.round(rttMs) + "ms"}, ` +
      `fps=${cur.video.framesPerSecond ?? "n/a"}, ` +
      `lost=${delta(cur.video, prevInbound.video, "packetsLost")}, ` +
      `nack=${delta(cur.video, prevInbound.video, "nackCount")}, ` +
      `pli=${delta(cur.video, prevInbound.video, "pliCount")}, ` +
      `dropped=${delta(cur.video, prevInbound.video, "framesDropped")}`;

    if (videoDelay > LAG_WARN_MS) {
      log(`[Stats] ラグ検知: ${detail}`);
      lagActive = true;
    } else if (lagActive && videoDelay < LAG_RECOVER_MS) {
      log(`[Stats] ラグ解消: ${detail}`);
      lagActive = false;
    }
  }

  prevInbound = cur;
}, STATS_INTERVAL_MS);
