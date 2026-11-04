Thanks, very solid work.

All 449 rows match our `docs/data/mint-opcodes.csv` on name and handler. We spot-checked 10 opcodes (`ldc.i4.m1`, `starg.i4`, `strmfld`, `callvirt`, `con.callvirt`, `ldelem.any.vt`, `div.i4`, `sim_sleep`, superops `0x1AB` and `0x1C0`) against the handlers, and they agree.

The `div.i4` / `rem.i4` `INT_MIN / -1` finding is confirmed: the handler at `0xE55B4C` checks only for a zero divisor before `idiv`. We will probably turn it into a small EA bug fix (a guard that throws like the CLI expects, off by default).

The x87 precision note is worth a live check of the sim thread's control word too.

One change before merge: master already has docs/208-211. Please rename to `docs/213-mint-opcode-spec.md`, update the heading and change `docs/209` to `docs/213` in your CLAUDE.md and PLAN.md lines.
