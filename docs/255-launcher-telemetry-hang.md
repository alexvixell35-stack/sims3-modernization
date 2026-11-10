# 255. Steam launcher stays running: telemetry busy-wait in TSLHost.dll ([LauncherFix])

2026-11-10. Static analysis only: no live run yet, because a queue chain was running all day. Steam 1.67 files: `Sims3LauncherW.exe` (pe 52DEC865) and `TSLHost.dll` (pe 52DEC86A, preferred base 0x10000000). Addresses below are TSLHost VAs at that base.

## 1. Report
[community] A tester (foulplay), and older EA forum threads: on the Steam edition, `Sims3LauncherW.exe` stays running after its window closes. The forums report 13-15 % CPU, which is one busy core on an 8-thread machine. A process left over from an earlier start also blocks the next start. The workaround: deny writes to `<Documents>\Electronic Arts\The Sims 3\Sims3Logs.xml` and `Sims3LauncherLogFile.log`.

## 2. Who touches the files
- `Sims3LauncherW.exe` is a .NET 2.0 x86 IL assembly with no native code. All of its business logic is P/Invoke into `TSLHost.dll` (`S3L_g_InitBusinessLogicEngine`, `S3L_g_UpdateBusinessLogicEngine`, `S3L_g_DestroyBusinessLogicEngine` = 0x100074D0). `TSLHost.dll` imports `WININET.dll`, so the Ultimate ASI Loader (`wininet.dll` in Bin) loads into the launcher process too. That is why ts3native wrote `SKIP all: host is not TS3W.exe` there [verified].
- **The game writes `Sims3Logs.xml`:** in TS3W.exe, 0x892CD0 rewrites it with a `<Sims3Telemetry count=...>` root (the file opened with mode 2/2, which means write) [verified]. TSLHost has no writer for it. It only reads, uploads and deletes the file [verified, see §3].
- `Sims3LauncherLogFile.log` is the NamedPipeServer log path. Both TSLHost (0x1004F390) and TS3W (0x60CB30, the same library linked into the game) create it with `CreateFileW(GENERIC_READ, OPEN_ALWAYS)` (0x102191F0). They write to it only when `Sims3Launcher.ini` sets `GameLauncherLogging` [verified]. Its role in the hang is not known.

## 3. The busy-wait [verified, static]
- The telemetry job's run function is 0x1003FCB0:
  `pump(); r = StartTelemetryUpload(); if (r == 0) while (!job.cancel && FileExists(Sims3Logs.xml)) pump();`
  `FileExists` (0x10219270) is `GetFileAttributesW`, and the loop has no sleep.
- `StartTelemetryUpload` is 0x10034450. It returns 0 when the file is missing, which makes the loop exit at once. Otherwise it calls 0x10032CF0, which returns 0x20 when the file is over 512 KB (skipped). Under that limit, 0x10032CF0 calls 0x10032790. That function opens the file (mode 1/6/1, which is `GENERIC_READ`, `OPEN_EXISTING`) and starts the transfer (0x102F38F0). It returns 0 only when both steps succeed, and 0x28 otherwise.
- The transfer callback 0x10032750 resets the upload slot when the connection fails, and the file stays. The pump 0x10034170 sends the file in 8 KB chunks and deletes it only after the last chunk (0x102192A0).
- So once an upload has started, the job spins until the file is deleted, and the file is deleted only after a completed upload. EA's telemetry service no longer answers [inferred], so the upload never completes. The job then spins forever on its thread, and `DestroyBusinessLogicEngine` waits about 5 s for jobs (0x100073E1, `_time64` loop) before it shuts the managers down. The process staying alive is [inferred]: it fits the forum CPU numbers, but no live stack has been taken yet.
- Second spin [verified, static]: in the pump, `do { n += Send(chunk) } while (n < len)` (0x100341F5) makes no progress when `Send` (0x102F3980) fails and returns 0.
- Why the workaround helps [inferred]: when the open in 0x10032790 fails (access denied), `StartTelemetryUpload` returns 0x28, the job skips the wait, and the launcher can exit. A plain read-only attribute would not stop that open (it only asks for read access). The tester's exact ACL is not known.

## 4. Fix: `[LauncherFix] Enabled` (ts3native 0.36.240, launcherfix.cpp)
- **Scope:** the launcher host only. That means exe name `Sims3LauncherW.exe` (or `Sims3Launcher.exe`) and exe pe 52DEC865. It also needs `TSLHost.dll` pe 52DEC86A and the 8 original bytes at TSLHost+0x34535 (`50 8B CE E8 B3 E7 FF FF` = `push eax; mov ecx,esi; call 0x10032CF0`). The site holds no relocation. Anything else is logged as `SKIP launcher fix: ...` and nothing is written.
- **Patch:** the 8 bytes become `B8 20 00 00 00 90 90 90` (`mov eax,20h`). This is the existing "file too big, skipped" result: no upload starts and the job returns at once. The callee was `ret 4`, so the `push eax` goes too, and the stack stays balanced (selftest `TestLauncherFix` runs the site before and after the patch). The game keeps writing `Sims3Logs.xml`; it is simply never uploaded.
- **Timing:** if TSLHost.dll is already loaded, it is patched at once. Otherwise a `LdrRegisterDllNotification` callback patches it when the DLL loads: after mapping, before its DllMain. No threads are created and nothing waits. `DLL_PROCESS_DETACH` returns at once in any host other than TS3W.exe.
- **Log:** a host other than TS3W.exe writes `ts3native.launcher.log`, so a launcher start no longer truncates the game's `ts3native.log` (the crash report pre-log is skipped there too). Proof line: `OK   launcher fix: TSLHost.dll+0x34535 telemetry upload skipped ...`.
- **Default:** 1 in code (a seen hang plus a fix). The repo ini has `Enabled=0` (opt-in) until the test build is confirmed. The TS3W.exe path is unchanged apart from the version string.
- **EA app 1.69** (`Sims3Launcher.exe` pe 67072CED): it ships its own TSLHost build, which has not been read. It is logged as an unknown launcher build and left alone.

## 5. Open
- Live check, when no queue runs. Does our machine reproduce the hang without the fix (a `Sims3LauncherW.exe` left after close, one busy core)? Does it exit with the fix?
- The tester's log line, plus whether the launcher now exits with the deny-write ACLs removed.
