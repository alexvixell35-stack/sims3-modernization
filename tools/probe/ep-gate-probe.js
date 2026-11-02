// EA app 1.69.47 product/entitlement gate probe (docs/234). Read-only Frida agent, no writes into the game.
// Attach to the game process (Sims3Launcher.exe = TS3.exe 1.69 on the EA laptop) as early as possible:
//   driver pattern: frida.attach(pid); session.create_script(this file).load(); messages arrive via send().
// Logs, relative to attach: the Origin service object [0x1244FB0] and its +0x28 (entitlements answered) / +0x24 (error)
// fields, the product flags [0x1227E54] (GetProductFlags; 0x1FEFFF = all packs) and [[0x1227E50]+0x10] (registry mask),
// plus hooks on the gate 0x596990 (ProductMount check), 0xA42A30 (service create + <=3 s wait; the jne at 0xA42A38
// skips the wait when the service already exists) and 0xA42080 (the loaded? test whose false result drops the products).
// Hooks are installed only after the DRM has decrypted .text (byte check at the two sites), polling every 20 ms.
// Addresses are for the file image at base 0x400000 (probe-out/ts3-ea169-file.exe) and are rebased to the live module.
// Stop: script.unload() from the driver; everything here is detach-safe (Interceptor.detachAll on unload).
// Result 2026-11-02 (docs/234): attached 0.5 s after process start, the EA build never reached the gate and exited by itself
// after 63 s (exit code -1, no LSX traffic). Early attach + hooks are not usable on the EA build; the values were read instead
// with OpenProcess/ReadProcessMemory from outside (no injection). Keep this as the reference for a late attach (after the menu).
'use strict';
var BASE = Process.enumerateModules()[0].base;
function A(x) { return BASE.add(x - 0x400000); }
var T0 = Date.now();
function log(k, v) { send({ t: Date.now() - T0, k: k, v: v }); }
log('attach', { module: Process.enumerateModules()[0].name, base: BASE.toString(), pid: Process.id });

function rd(f) { try { return f(); } catch (e) { return 'ERR ' + e.message; } }
function state() {
  var svc = rd(function () { return A(0x1244FB0).readPointer(); });
  var s = { svc: String(svc), flags: rd(function () { return '0x' + A(0x1227E54).readU32().toString(16); }) };
  if (svc instanceof NativePointer && !svc.isNull()) {
    s.answered = rd(function () { return svc.add(0x28).readU8(); });
    s.err = rd(function () { return svc.add(0x24).readU32(); });
  }
  var reg = rd(function () { return A(0x1227E50).readPointer(); });
  s.reg = (reg instanceof NativePointer && !reg.isNull()) ? rd(function () { return '0x' + reg.add(0x10).readU32().toString(16); }) : String(reg);
  return s;
}

var last = '', beat = 0;
var poll = setInterval(function () {
  var s = state(), j = JSON.stringify(s);
  beat += 100;
  if (j !== last || beat >= 5000) { log(j !== last ? 'change' : 'beat', s); last = j; beat = 0; }
}, 100);

function bytesAt(addr, hex) {
  var b = rd(function () { return addr.readByteArray(hex.length / 2); });
  if (!(b instanceof ArrayBuffer)) return false;
  return Array.prototype.map.call(new Uint8Array(b), function (x) { return ('0' + x.toString(16)).slice(-2); }).join('') === hex;
}

var hooked = false;
var waitDecrypt = setInterval(function () {
  if (!(bytesAt(A(0xA42A30), '51a1b04f240185c00f858e000000') && bytesAt(A(0x596990), '53558bd9e8'))) return;
  clearInterval(waitDecrypt);
  hooked = true;
  log('decrypted', state());
  Interceptor.attach(A(0x596990), {
    onEnter: function (args) { log('gate596990.enter', { ecx: this.context.ecx.toString(), st: state() }); },
    onLeave: function (r) { log('gate596990.leave', { eax: r.toString(), st: state() }); }
  });
  Interceptor.attach(A(0xA42A30), {
    onEnter: function (args) { var svc = rd(function () { return A(0x1244FB0).readPointer(); });
      log('svcA42A30.enter', { svc: String(svc), skipWait: !(svc instanceof NativePointer) || !svc.isNull(), st: state() }); },
    onLeave: function (r) { log('svcA42A30.leave', { eax: r.toString(), st: state() }); }
  });
  Interceptor.attach(A(0xA42080), {
    onLeave: function (r) { log('loadedA42080.leave', { al: r.toInt32() & 0xff, st: state() }); }
  });
}, 20);
setTimeout(function () { if (!hooked) { clearInterval(waitDecrypt); log('nohook', 'text not decrypted within 30 s; periodic reads only'); } }, 30000);

rpc.exports = { state: state };
