// Room left for new pages on the temp volume of a Linux test rig (docs/683). Wine keeps the pages of every unnamed file
// mapping (CreateFileMapping on the page file: DXVK's 32-bit texture memory, shared sections) in unlinked files under
// /tmp/.wine-<uid>/, so on a tmpfs /tmp they count against the volume's size and the user's quota (systemd mounts /tmp with
// usrquota, 80% of the volume by default). When either runs out, the first write to a not yet touched page of such a view
// gets SIGBUS, which the game sees as an access violation writing valid memory (run s194: memset in ucrtbase called by d3d9).
//   node tools/harness/tmp-headroom.ts [--dir /tmp] [--min-mb 1024]     one line; exit 0 = enough room, 2 = low
//   node tools/harness/tmp-headroom.ts --self-check
// Call it before a game start and again when a game died: a LOW line next to a crash marks a rig failure, not a game crash.
// Not Linux, or the directory is not there: prints "n/a" and exits 0.
import { execFileSync } from 'node:child_process';
import { existsSync, statfsSync } from 'node:fs';

export interface Room { freeMb: number; quotaLimitMb: number; quotaUsedMb: number } // quotaLimitMb 0 = no quota (or unknown)

// MB a user can still add: the volume's free space, cut by what is left of the quota.
export function headroomMb(r: Room): number {
  const left = r.quotaLimitMb > 0 ? Math.max(0, r.quotaLimitMb - r.quotaUsedMb) : Infinity;
  return Math.floor(Math.min(r.freeMb, left));
}

export function line(dir: string, r: Room, minMb: number): string {
  const quota = r.quotaLimitMb > 0 ? `user quota ${Math.round(r.quotaLimitMb)} MB, used ${Math.round(r.quotaUsedMb)} MB` : 'no user quota';
  const room = headroomMb(r);
  return `tmp headroom: ${dir} free ${Math.round(r.freeMb)} MB, ${quota}: room ${room} MB (${room >= minMb ? 'OK' : 'LOW'}, min ${minMb} MB)`;
}

// The quota helper prints "<hard limit in 1 KiB blocks> <bytes in use>", e.g. "12847190 11574210560"; anything else = no quota.
const QUOTA_RE = /^(\d+) (\d+)$/;
export function parseQuota(out: string): { quotaLimitMb: number; quotaUsedMb: number } {
  const m = QUOTA_RE.exec(out.trim());
  return m ? { quotaLimitMb: Number(m[1]) / 1024, quotaUsedMb: Number(m[2]) / 1048576 } : { quotaLimitMb: 0, quotaUsedMb: 0 };
}

// quotactl_fd(2) (x86-64 syscall 443, Linux 5.14+) with Q_GETQUOTA for the calling user: tmpfs has no block device to name,
// so the path form of quotactl does not work and Node has no binding for either.
const QUOTA_PY = [
  'import ctypes,os,struct,sys',
  'fd=os.open(sys.argv[1],os.O_RDONLY)',
  'b=ctypes.create_string_buffer(72)',
  'r=ctypes.CDLL(None).syscall(443,fd,0x80000700,os.getuid(),b)',
  'v=struct.unpack("QQQ",b.raw[:24])',
  'print(v[0],v[2]) if r==0 else print("none")',
].join('\n');

function readRoom(dir: string): Room {
  const s = statfsSync(dir);
  const freeMb = (Number(s.bavail) * Number(s.bsize)) / 1048576;
  let out = '';
  try { out = execFileSync('python3', ['-I', '-c', QUOTA_PY, dir], { encoding: 'utf8', timeout: 10000, stdio: ['ignore', 'pipe', 'ignore'] }); } catch { /* no python3 or no quotactl_fd: free space only */ }
  return { freeMb, ...parseQuota(out) };
}

function selfCheck(): void {
  const eq = (a: unknown, b: unknown, what: string) => { if (JSON.stringify(a) !== JSON.stringify(b)) { console.error(`FAIL ${what}: ${JSON.stringify(a)} != ${JSON.stringify(b)}`); process.exitCode = 1; } };
  // the rig's real answer on 2026-11-20 (12.25 GiB limit, 10.78 GiB in use)
  eq(parseQuota('12847190 11574210560\n'), { quotaLimitMb: 12847190 / 1024, quotaUsedMb: 11574210560 / 1048576 }, 'quota line');
  eq(parseQuota('none\n'), { quotaLimitMb: 0, quotaUsedMb: 0 }, 'no quota');
  eq(parseQuota(''), { quotaLimitMb: 0, quotaUsedMb: 0 }, 'helper failed');
  eq(parseQuota('12847190 11574210560 extra'), { quotaLimitMb: 0, quotaUsedMb: 0 }, 'other text is not a quota line');
  eq(headroomMb({ freeMb: 4500, quotaLimitMb: 12546, quotaUsedMb: 11038 }), 1508, 'quota is the smaller');
  eq(headroomMb({ freeMb: 300, quotaLimitMb: 12546, quotaUsedMb: 11038 }), 300, 'free space is the smaller');
  eq(headroomMb({ freeMb: 3700, quotaLimitMb: 12546, quotaUsedMb: 12546.2 }), 0, 'over quota with free space left (the s194 state)');
  eq(headroomMb({ freeMb: 4500, quotaLimitMb: 0, quotaUsedMb: 0 }), 4500, 'no quota');
  eq(line('/tmp', { freeMb: 3700, quotaLimitMb: 12546, quotaUsedMb: 12546 }, 1024), 'tmp headroom: /tmp free 3700 MB, user quota 12546 MB, used 12546 MB: room 0 MB (LOW, min 1024 MB)', 'low line');
  eq(line('/tmp', { freeMb: 4500, quotaLimitMb: 0, quotaUsedMb: 0 }, 1024), 'tmp headroom: /tmp free 4500 MB, no user quota: room 4500 MB (OK, min 1024 MB)', 'ok line');
  if (!process.exitCode) console.log('tmp-headroom self-check ok');
}

if (process.argv[1] && process.argv[1].endsWith('tmp-headroom.ts')) {
  const arg = (name: string, dflt: string) => { const i = process.argv.indexOf(name); return i > 0 && process.argv[i + 1] ? process.argv[i + 1] : dflt; };
  if (process.argv.includes('--self-check')) selfCheck();
  else {
    const dir = arg('--dir', '/tmp');
    const minMb = Number(arg('--min-mb', '1024'));
    if (process.platform !== 'linux' || !existsSync(dir)) console.log(`tmp headroom: n/a (${process.platform}, ${dir})`);
    else {
      const r = readRoom(dir);
      console.log(line(dir, r, minMb));
      if (headroomMb(r) < minMb) process.exitCode = 2;
    }
  }
}
