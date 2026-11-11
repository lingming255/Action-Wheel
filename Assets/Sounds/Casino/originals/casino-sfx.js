// Short Kenney CC0 object sounds. No game values enter this audio interface.
(() => {
  'use strict';
  const store = 'point-duel-sfx';
  const Audio = window.AudioContext || window.webkitAudioContext;
  const button = document.querySelector('#sound-toggle');
  const bank = {
    hover: { buffer: 'pick', gain: .16, interval: 0, duration: .07 },
    pick: { file: 'pick.mp3', gain: .55, interval: 90 },
    lock: { file: 'lock.mp3', gain: .75, interval: 0 },
    reveal: { file: 'reveal.mp3', gain: .6, interval: 0 },
  };
  const buffers = new Map(), voices = new Set(), lastPlayed = new Map();
  let muted = false, context, master, unavailable = !Audio, hasGesture = false;
  try { muted = localStorage.getItem(store) === 'off'; } catch {}

  // Fetch small files early, but create/resume audio only inside a user gesture.
  const files = Object.entries(bank).filter(([, sound]) => sound.file).map(async ([name, sound]) => {
    try {
      const response = await fetch(`/audio/casino/${sound.file}`, { signal: AbortSignal.timeout(5000) });
      if (!response.ok) return null;
      return [name, await response.arrayBuffer()];
    } catch { return null; }
  });
  function syncButton() {
    if (!button) return;
    button.textContent = unavailable ? '音效不可用' : `音效：${muted ? '关' : '开'}`;
    button.setAttribute('aria-pressed', String(!muted && !unavailable));
    button.setAttribute('aria-label', unavailable ? '此浏览器无法播放音效' : `赌场音效已${muted ? '关闭，点击开启' : '开启，点击关闭'}`);
    button.disabled = unavailable;
  }
  function stopVoice(voice) {
    voices.delete(voice);
    try { voice.source.stop(); } catch {}
    voice.source.disconnect(); voice.gain.disconnect();
  }
  function hush() {
    if (master) master.gain.value = 0;
    for (const voice of voices) stopVoice(voice);
    if (context && context.state !== 'closed') context.suspend().catch(() => {});
  }
  function prepare() {
    if (!hasGesture || muted || unavailable || document.hidden) return;
    try {
      if (!context) {
        context = new Audio();
        master = context.createGain(); master.gain.value = .5; master.connect(context.destination);
        for (const file of files) file.then(async entry => {
          if (!entry) return;
          try { buffers.set(entry[0], await context.decodeAudioData(entry[1])); } catch {}
        });
      }
      master.gain.value = .5;
      context.resume().catch(() => {});
    } catch { unavailable = true; syncButton(); }
  }
  function play(name) {
    const sound = bank[name], buffer = buffers.get(sound?.buffer || name);
    // Never queue a sound while loading, muted, suspended, or in the background.
    if (!sound || !buffer || muted || document.hidden || context?.state !== 'running') return;
    const now = performance.now();
    if (now - (lastPlayed.get(name) ?? -Infinity) < sound.interval) return;
    try {
      while (voices.size >= 3) stopVoice(voices.values().next().value);
      const source = context.createBufferSource(), gain = context.createGain();
      source.buffer = buffer; gain.gain.value = sound.gain;
      if (sound.duration) {
        gain.gain.setValueAtTime(sound.gain, context.currentTime);
        gain.gain.linearRampToValueAtTime(0, context.currentTime + sound.duration);
      }
      source.connect(gain); gain.connect(master);
      const voice = { source, gain }; voices.add(voice);
      source.onended = () => { voices.delete(voice); source.disconnect(); gain.disconnect(); };
      if (sound.duration) source.start(0, 0, sound.duration); else source.start();
      lastPlayed.set(name, now);
    } catch { /* Audio failure must not interrupt a move. */ }
  }
  function setMuted(value, persist = true) {
    muted = Boolean(value);
    if (persist) { try { localStorage.setItem(store, muted ? 'off' : 'on'); } catch {} }
    if (muted) hush(); else if (persist) prepare();
    syncButton();
  }
  button?.addEventListener('click', () => setMuted(!muted));
  const gesture = event => { if (event.isTrusted && !event.repeat) { hasGesture = true; prepare(); } };
  document.addEventListener('pointerdown', gesture, { capture: true, passive: true });
  document.addEventListener('keydown', gesture, true);
  // Delegation also covers controls rebuilt after each round or tutorial step.
  document.addEventListener('pointerover', event => {
    if (!['mouse', 'pen'].includes(event.pointerType)) return;
    const target = event.target.closest?.('button, a[href], summary, [role="button"], input[type="button"], input[type="submit"], input[type="reset"]');
    if (!target || target.contains(event.relatedTarget) || target.matches(':disabled') || target.closest('[inert], [hidden], [aria-disabled="true"]') || !target.getClientRects().length) return;
    const dialog = document.querySelector('dialog[open]');
    if (dialog && !dialog.contains(target)) return;
    play('hover');
  });
  document.addEventListener('visibilitychange', () => { if (document.hidden) hush(); });
  window.addEventListener('pagehide', hush);
  window.addEventListener('storage', event => {
    if (event.key === store || event.key === null) setMuted(event.newValue === 'off', false);
  });
  window.CasinoSFX = Object.freeze({ play, get muted() { return muted; } });
  syncButton();
})();
