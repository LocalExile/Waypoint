// Standalone client-state agent: the Gateway publishes named documents (its ClientState hand-over,
// data/clients/state/<channel>.json); StateBridge.py reads them and calls setState(channel, payload) through RPC. A
// channel's handler installs its document into the client on the game's main thread, in the per-frame state-stack
// update. Adding a channel is adding a handler here: nothing else changes.
const mod = (() => {
  for (const n of ['Game.patched.exe', 'PathOfExileSteam.exe', 'PathOfExile.exe']) {
    try { return Process.getModuleByName(n); } catch (e) { }
  }
  const f = Process.enumerateModules().find(x => /^(Game\.patched|PathOfExile(Steam)?)\.exe$/i.test(x.name));
  if (!f) throw new Error('game module not found');
  return f;
})();
const A = rva => mod.base.add(rva - 0x140000000);
const emit = o => send({ message: JSON.stringify(o) });
const alloc = new NativeFunction(A(0x140144e30), 'pointer', ['ulong']);
let instance = null;

// The HUD's attach and the world handler give the instance, but a bridge that starts while the client is already in
// game may see neither: read it from the application global too (app + 0x348). The global's address comes from the
// instruction that uses it (mov rax,[app] at 0x1400f3b30), because data moves between client builds whose code does not.
const appGlobal = (() => { const p = A(0x1400f3b30); return p.add(7).add(p.add(3).readS32()); })();
const instanceNow = () => {
  try {
    const app = appGlobal.readPointer();
    if (app.isNull()) return null;
    const inst = app.add(0x348).readPointer();
    return inst.isNull() || inst.add(0xc278).readPointer().isNull() ? null : inst;
  } catch (e) { return null; }
};

// affliction-rewards: the Simulacrum wave's reward tiles, drawn from the instance's vector at +0xda58. No server
// message reaches that vector in the supported builds, so this is the only way to fill them.
const installTiles = new NativeFunction(A(0x142172a00), 'void', ['pointer', 'pointer']);
let tilesInstalled = 0;
function tiles(payload, inst) {
  if (inst === null) return false;
  const entries = Array.isArray(payload && payload.entries) ? payload.entries : [];
  const n = entries.length;
  let buf = ptr(0);
  if (n > 0) {
    buf = alloc(n * 0x14);
    for (let i = 0; i < n; i++) {
      const e = buf.add(i * 0x14), s = entries[i];
      e.writeU32(s.type >>> 0); e.add(4).writeU32(Math.max(0, s.cur | 0)); e.add(8).writeU32(Math.max(1, s.max | 0));
      e.add(12).writeU32((Number(s.x) | 0) >>> 0);  // the tile's count: an integer the client prints as is
    }
  }
  const vec = alloc(0x18);
  vec.writePointer(buf); vec.add(8).writePointer(buf.add(n * 0x14)); vec.add(0x10).writePointer(buf.add(n * 0x14));
  installTiles(inst, vec);
  emit({ hook: 'installed', channel: 'affliction-rewards', n, installed: ++tilesInstalled });
  return true;
}

// The tile widget tweens a changed count as a float and prints that float's bit pattern for a frame before the integer
// arrives (live 2026-11-24: 8 flashed as 1090519040). While the update runs with such a value, hand it the integer the
// float represents and put the stored value back afterwards: same tile, no flicker, nothing else touched.
Interceptor.attach(A(0x1403353a0), {
  onEnter(args) {
    const entry = args[2];
    try {
      const f = entry.add(0xc).readFloat();
      if (!(f >= 1 && f <= 1e6 && Math.trunc(f) === f)) return;
      this.entry = entry;
      this.bits = entry.add(0xc).readU32();
      entry.add(0xc).writeU32(f >>> 0);
    } catch (e) { }
  },
  onLeave() {
    if (!this.entry) return;
    try { this.entry.add(0xc).writeU32(this.bits); } catch (e) { }
    this.entry = null;
  }
});

const handlers = { 'affliction-rewards': tiles };
const pending = new Map();
const last = new Map();
// A new area is a new instance with an empty vector: put every channel's document back in the queue for it.
function useInstance(inst, source) {
  if (inst === null || (instance !== null && instance.equals(inst))) return;
  instance = inst;
  emit({ hook: 'instance(' + source + ')', inst: instance.toString() });
  for (const [channel, payload] of last) pending.set(channel, payload);
}
Interceptor.attach(A(0x140333f80), { onEnter(args) { if (!args[1].isNull()) useInstance(args[1], 'attach'); } });
Interceptor.attach(A(0x14218ddf0), { onEnter(args) {
  if (instance !== null) return;
  try {
    const world = args[0];
    const inst = world.add(0x968).readPointer();
    if (inst.isNull()) return;
    if (!inst.add(0xc278).readPointer().equals(world)) return;
    useInstance(inst, 'world');
  } catch (e) { }
}});
Interceptor.attach(A(0x141528f40), { onEnter() {
  if (instance === null) useInstance(instanceNow(), 'global');
  if (pending.size === 0) return;
  for (const [channel, payload] of [...pending]) {
    const handler = handlers[channel];
    if (!handler) { pending.delete(channel); emit({ hook: 'unhandled', channel }); continue; }
    try {
      // A handler that returns false is not done (no instance yet): keep its document for a later frame.
      if (handler(payload, instance) !== false) pending.delete(channel);
    } catch (e) { pending.delete(channel); emit({ error: String(e), channel }); }
  }
}});

rpc.exports = {
  setState: (channel, payload) => {
    const name = String(channel);
    last.set(name, payload);
    pending.set(name, payload);
    emit({ hook: 'pending', channel: name, hasInstance: instance !== null });
    return true;
  },
  ready: () => instance !== null,
};