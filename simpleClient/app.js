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

function authTokenKey(url) {
  try {
    return `ramune314159265.remotesimpleclient.token.${new URL(url).host}`;
  } catch {
    return `ramune314159265.remotesimpleclient.token.${url}`;
  }
}

function loadAuthToken(url) {
  try {
    return localStorage.getItem(authTokenKey(url));
  } catch {
    return null;
  }
}

function saveAuthToken(url, token) {
  try {
    localStorage.setItem(authTokenKey(url), token);
  } catch { }
}

const authDeniedMessages = {
  denied: "ホストで拒否されました",
  busy: "ホストが別の端末の承認待ちです。しばらくしてからやり直してください",
  locked: "拒否が続いたため、しばらく接続できません",
};

async function handleSignalingMessage(msg) {
  if (msg.type === "authPending") {
    log("Waiting for approval on the host");
    setStatus("ホストの承認待ち...", "connecting");
  } else if (msg.type === "authOk") {
    if (msg.token) {
      saveAuthToken(wsUrlInput.value.trim(), msg.token);
      log("Device approved, token saved");
    }
    setStatus("接続中...", "connecting");
  } else if (msg.type === "authDenied") {
    authDeniedReason = authDeniedMessages[msg.reason] ?? "認証に失敗しました";
    log("Authentication failed:", authDeniedReason);
    setStatus(authDeniedReason, "error");
  } else if (msg.type === "offer") {
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

let authDeniedReason = null;

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

  authDeniedReason = null;

  ws.onopen = () => {
    log("Signaling connected:", url);
    sendMessage({ type: "auth", token: loadAuthToken(baseUrl) });
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
    if (!authDeniedReason) setStatus("未接続", "idle");
    cleanupPeer();
    connectBtn.disabled = false;
    disconnectBtn.disabled = true;
  };

  connectBtn.disabled = true;
  disconnectBtn.disabled = false;
}

function cleanupPeer() {
  stopRapidClick();
  clearMedia();
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
  const { width, height } = getAvailableBoxSizePx();
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
 * ブラウザのウィンドウ(innerWidth / innerHeight)の大きさをデバイスピクセル単位で返す。
 * video 要素の大きさは映像の解像度やレイアウトに左右されるため参照しない。映像回転中は縦横を入れ替える。
 */
function getAvailableBoxSizePx() {
  const dpr = window.devicePixelRatio || 1;
  const width = Math.round(window.innerWidth * dpr);
  const height = Math.round(window.innerHeight * dpr);
  return rotated ? { width: height, height: width } : { width, height };
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

let rotated = false;

/**
 * <video> 要素の描画ボックスのうち、映像そのものが描画されている範囲(黒帯を除く)を求める。
 * cw/ch は映像自身の向き(回転前)での幅と高さ。回転中は要素のバウンディングボックスの縦横が入れ替わる。
 */
function getVideoLayout() {
  const rect = videoEl.getBoundingClientRect();
  const elW = rotated ? rect.height : rect.width;
  const elH = rotated ? rect.width : rect.height;
  let cw = elW;
  let ch = elH;

  if (videoEl.videoWidth && videoEl.videoHeight) {
    const videoRatio = videoEl.videoWidth / videoEl.videoHeight;
    if (elW / elH > videoRatio) {
      ch = elH;
      cw = elH * videoRatio;
    } else {
      cw = elW;
      ch = elW / videoRatio;
    }
  }
  return { rect, cw, ch };
}

/**
 * 画面上の座標を、映像自身の向きでの正規化座標(0〜1)へ変換する。
 * 90度(反時計回り)回転中は、見た目の上方向が映像の右方向になる。
 */
function clientToVideoPoint(clientX, clientY) {
  const { rect, cw, ch } = getVideoLayout();
  const dx = clientX - (rect.left + rect.width / 2);
  const dy = clientY - (rect.top + rect.height / 2);
  const u = rotated ? -dy : dx;
  const v = rotated ? dx : dy;
  return { x: (u + cw / 2) / cw, y: (v + ch / 2) / ch };
}

videoEl.tabIndex = 0;
videoEl.addEventListener("click", () => videoEl.focus());
videoEl.addEventListener("contextmenu", (e) => e.preventDefault());

const RAPID_CLICK_INTERVAL_MS = 50;
const MOUSE_TOUCH_ID = 1000;

function clamp01(value) {
  return Math.max(Math.min(value, 1), 0);
}

/** 入力元(mouse / touch)と入力モードの設定から、実際に使う入力方式(mouse / touch / rapid)を決める。 */
function resolveInputMode(source) {
  const mode = inputSelect.value;
  if (mode === "auto") return source === "touch" ? "touch" : "mouse";
  return mode;
}

function videoPointClamped(clientX, clientY) {
  const point = clientToVideoPoint(clientX, clientY);
  return { x: clamp01(point.x), y: clamp01(point.y) };
}

let rapidTimer = null;

function startRapidClick(button) {
  stopRapidClick();
  const click = () => {
    sendControl({ type: "mouseButton", button, action: "down" });
    sendControl({ type: "mouseButton", button, action: "up" });
  };
  click();
  rapidTimer = setInterval(click, RAPID_CLICK_INTERVAL_MS);
}

function stopRapidClick() {
  if (rapidTimer !== null) {
    clearInterval(rapidTimer);
    rapidTimer = null;
  }
}

window.addEventListener("blur", stopRapidClick);

let mousePressMode = null;

videoEl.addEventListener("mousemove", (e) => {
  const raw = clientToVideoPoint(e.clientX, e.clientY);
  if (raw.x < 0 || raw.x > 1 || raw.y < 0 || raw.y > 1) return;

  const mode = mousePressMode ?? resolveInputMode("mouse");
  if (mode === "touch") {
    if (mousePressMode === "touch") {
      sendControl({ type: "touch", touches: [{ id: MOUSE_TOUCH_ID, x: raw.x, y: raw.y, phase: "move" }] });
    }
    return;
  }
  sendControl({ type: "mouseMove", mode: "absolute", x: raw.x, y: raw.y });
});

videoEl.addEventListener("mousedown", (e) => {
  e.preventDefault();
  const mode = resolveInputMode("mouse");
  mousePressMode = mode;

  if (mode === "touch") {
    const point = videoPointClamped(e.clientX, e.clientY);
    sendControl({ type: "touch", touches: [{ id: MOUSE_TOUCH_ID, x: point.x, y: point.y, phase: "down" }] });
  } else if (mode === "rapid") {
    startRapidClick(buttonName(e.button));
  } else {
    sendControl({ type: "mouseButton", button: buttonName(e.button), action: "down" });
  }
});

videoEl.addEventListener("mouseup", (e) => {
  const mode = mousePressMode ?? resolveInputMode("mouse");
  mousePressMode = null;

  if (mode === "touch") {
    const point = videoPointClamped(e.clientX, e.clientY);
    sendControl({ type: "touch", touches: [{ id: MOUSE_TOUCH_ID, x: point.x, y: point.y, phase: "up" }] });
  } else if (mode === "rapid") {
    stopRapidClick();
  } else {
    sendControl({ type: "mouseButton", button: buttonName(e.button), action: "up" });
  }
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
  const dtos = [];
  for (const touch of touchList) {
    const point = videoPointClamped(touch.clientX, touch.clientY);
    dtos.push({ id: touch.identifier, x: point.x, y: point.y, phase });
  }
  return dtos;
}

function sendTouchBatch(touchList, phase) {
  const touches = touchListToDtos(touchList, phase);
  if (touches.length === 0) return;
  sendControl({ type: "touch", touches });
}

function sendMouseMoveFromTouch(touch) {
  const point = videoPointClamped(touch.clientX, touch.clientY);
  sendControl({ type: "mouseMove", mode: "absolute", x: point.x, y: point.y });
}

let touchPressMode = null;

videoEl.addEventListener(
  "touchstart",
  (e) => {
    e.preventDefault();
    const mode = e.touches.length === e.changedTouches.length || touchPressMode === null
      ? resolveInputMode("touch")
      : touchPressMode;
    touchPressMode = mode;

    if (mode === "touch") {
      sendTouchBatch(e.changedTouches, "down");
      return;
    }
    if (e.touches.length > e.changedTouches.length) return;

    sendMouseMoveFromTouch(e.changedTouches[0]);
    if (mode === "rapid") {
      startRapidClick("left");
    } else {
      sendControl({ type: "mouseButton", button: "left", action: "down" });
    }
  },
  { passive: false }
);

videoEl.addEventListener(
  "touchmove",
  (e) => {
    e.preventDefault();
    const mode = touchPressMode ?? resolveInputMode("touch");
    if (mode === "touch") {
      sendTouchBatch(e.changedTouches, "move");
      return;
    }
    sendMouseMoveFromTouch(e.changedTouches[0]);
  },
  { passive: false }
);

videoEl.addEventListener(
  "touchend",
  (e) => {
    e.preventDefault();
    const mode = touchPressMode ?? resolveInputMode("touch");
    if (e.touches.length === 0) touchPressMode = null;

    if (mode === "touch") {
      sendTouchBatch(e.changedTouches, "up");
      return;
    }
    if (e.touches.length > 0) return;

    sendMouseMoveFromTouch(e.changedTouches[0]);
    if (mode === "rapid") {
      stopRapidClick();
    } else {
      sendControl({ type: "mouseButton", button: "left", action: "up" });
    }
  },
  { passive: false }
);

videoEl.addEventListener(
  "touchcancel",
  (e) => {
    e.preventDefault();
    const mode = touchPressMode ?? resolveInputMode("touch");
    if (e.touches.length === 0) touchPressMode = null;

    if (mode === "touch") {
      sendTouchBatch(e.changedTouches, "cancel");
    } else if (mode === "rapid") {
      stopRapidClick();
    } else if (e.touches.length === 0) {
      sendControl({ type: "mouseButton", button: "left", action: "up" });
    }
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
const rotateBtn = document.getElementById("rotateBtn");

rotateBtn.addEventListener("click", () => {
  rotated = !rotated;
  videoEl.classList.toggle("rotated", rotated);
  scheduleSendViewportSize();
});

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

  if (msg.type === "mediaState") {
    applyMediaState(msg);
    return;
  }
  if (msg.type === "mediaThumbnail") {
    applyMediaThumbnail(msg);
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
    lastSentViewportSize = { width: target.width, height: target.height };
    sendControl({ type: "setViewportSize", width: target.width, height: target.height });
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


videoEl.addEventListener("pause", () => {
  if (!pc || pc.connectionState === "closed" || videoEl.ended) return;
  videoEl.play().catch((err) => log("video.play() に失敗:", err.message ?? err));
});

const mediaSessionApi = "mediaSession" in navigator ? navigator.mediaSession : null;
const mediaThumbs = new Map();
let currentMedia = null;
let lastMediaSignature = "";

function selectMediaSession(msg) {
  const sessions = Array.isArray(msg.sessions) ? msg.sessions : [];
  return sessions.find((s) => s.id === msg.current)
    ?? sessions.find((s) => s.status === "playing")
    ?? sessions[0]
    ?? null;
}

function estimatedMediaPositionMs() {
  if (!currentMedia) return 0;
  const { session, receivedAt } = currentMedia;
  let position = session.positionMs;
  if (session.status === "playing") position += (performance.now() - receivedAt) * (session.rate || 1);
  return Math.min(Math.max(position, 0), session.durationMs || position);
}

function sendMediaControl(mediaAction, positionMs) {
  if (!currentMedia) return;
  sendControl({ type: "mediaControl", sessionId: currentMedia.session.id, mediaAction, positionMs });
  setTimeout(() => updateMediaSession(true), 800);
}

function setMediaAction(action, enabled, handler) {
  try {
    mediaSessionApi.setActionHandler(action, enabled ? handler : null);
  } catch { }
}

function updateMediaSession(reapply = false) {
  if (!mediaSessionApi) return;

  if (!currentMedia) {
    lastMediaSignature = "";
    mediaSessionApi.metadata = null;
    mediaSessionApi.playbackState = "none";
    for (const action of ["play", "pause", "previoustrack", "nexttrack", "seekto", "seekbackward", "seekforward"]) {
      setMediaAction(action, false);
    }
    return;
  }

  const session = currentMedia.session;
  const thumb = mediaThumbs.get(session.id);
  const image = thumb && thumb.key === session.thumbKey ? thumb.image : null;
  const hasImage = typeof image === "string" && image.startsWith("data:image/jpeg;base64,");

  const signature = [session.id, session.title, session.artist, session.album, hasImage ? "img" : "noimg"].join("|");
  if (signature !== lastMediaSignature || reapply) {
    lastMediaSignature = signature;
    mediaSessionApi.metadata = new MediaMetadata({
      title: session.title || "",
      artist: session.artist || "",
      album: session.album || "",
      artwork: hasImage ? [{ src: image, sizes: "200x200", type: "image/jpeg" }] : [],
    });
  }

  mediaSessionApi.playbackState = session.status === "playing" ? "playing" : session.status === "paused" ? "paused" : "none";

  try {
    if (session.durationMs > 0) {
      mediaSessionApi.setPositionState({
        duration: session.durationMs / 1000,
        playbackRate: session.rate || 1,
        position: estimatedMediaPositionMs() / 1000,
      });
    } else {
      mediaSessionApi.setPositionState();
    }
  } catch { }

  setMediaAction("play", session.canPlay, () => sendMediaControl("play"));
  setMediaAction("pause", session.canPause, () => sendMediaControl("pause"));
  setMediaAction("previoustrack", session.canPrevious, () => sendMediaControl("previous"));
  setMediaAction("nexttrack", session.canNext, () => sendMediaControl("next"));
  setMediaAction("seekto", session.canSeek, (details) => sendMediaControl("seek", details.seekTime * 1000));
  setMediaAction("seekbackward", session.canSeek, (details) =>
    sendMediaControl("seek", Math.max(0, estimatedMediaPositionMs() - (details.seekOffset ?? 10) * 1000)));
  setMediaAction("seekforward", session.canSeek, (details) =>
    sendMediaControl("seek", Math.min(session.durationMs, estimatedMediaPositionMs() + (details.seekOffset ?? 10) * 1000)));
}

function applyMediaState(msg) {
  const session = selectMediaSession(msg);
  if (session?.id !== currentMedia?.session.id || session?.status !== currentMedia?.session.status) {
    log("[Media]", session ? `${session.status}: ${session.title}` : "なし");
  }
  currentMedia = session ? { session, receivedAt: performance.now() } : null;

  const ids = new Set((msg.sessions ?? []).map((s) => s.id));
  for (const id of mediaThumbs.keys()) {
    if (!ids.has(id)) mediaThumbs.delete(id);
  }
  updateMediaSession();
}

function applyMediaThumbnail(msg) {
  mediaThumbs.set(msg.id, { key: msg.thumbKey, image: msg.image });
  if (currentMedia?.session.id === msg.id) updateMediaSession();
}

function clearMedia() {
  currentMedia = null;
  mediaThumbs.clear();
  updateMediaSession();
}
