// C6 frame-reader census (docs/537): every instruction that touches the interpreter's frame state, by the handle it gets
// there through, and how each such function can be reached. No frame field is reachable without one of the handles:
//   abs      the globals: TLS key of the ThreadContext, current state, record-array base
//   host     host+0xBC8 (slot of the running root state), host+0xB84 (task container)
//   state    [reg+0x140] depth, [reg+0x13C] arena chain, [reg+0x144] max depth
//   record   [ebp+reg*n-0x4C74]: the record array of a ves_exec activation
// Args: <outPrefix> [0xaddr of an extra root the census should treat as a thread entry]...
// Writes <outPrefix>-sites.tsv (one row per instruction) and <outPrefix>-funcs.tsv (one row per function: site counts,
// reach, thread entries and address-taken ancestors on direct-call paths).
// Headless: -process TS3W.exe -noanalysis -readOnly -scriptPath tools/ghidra -postScript FrameReaders.java out/fr
// @category Sims3
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.scalar.Scalar;
import ghidra.program.model.symbol.*;
import java.io.PrintWriter;
import java.util.*;

public class FrameReaders extends GhidraScript {
    static final long[] ABS = {0x11F4160L, 0x11F4170L, 0x11F416CL};
    static final String[] ABS_NAME = {"tls-ctx-key", "cur-state", "records-base"};
    static final long[] HOST = {0xBC8L, 0xB84L};
    static final long[] STATE = {0x140L, 0x13CL, 0x144L};
    static final String[] THREAD_APIS = {"CreateThread", "_beginthreadex", "_beginthread", "CreateRemoteThread",
        "QueueUserWorkItem", "CreateTimerQueueTimer", "timeSetEvent", "RegisterWaitForSingleObject", "QueueUserAPC",
        "SetUnhandledExceptionFilter", "AddVectoredExceptionHandler", "SetConsoleCtrlHandler", "SetTimer", "SetWindowsHookExA", "SetWindowsHookExW",
        "CreateWaitableTimerA", "SetWaitableTimer", "CreateFiber", "CreateFiberEx"};

    Map<Function, int[]> counts = new HashMap<>();  // abs, host, state, record, reads, writes
    Listing lst;

    void site(PrintWriter o, Instruction in, String kind, String what) {
        Function f = getFunctionContaining(in.getAddress());
        boolean wr = false, rd = false;
        for (ghidra.program.model.pcode.PcodeOp p : in.getPcode()) {
            if (p.getOpcode() == ghidra.program.model.pcode.PcodeOp.STORE) wr = true;
            if (p.getOpcode() == ghidra.program.model.pcode.PcodeOp.LOAD) rd = true;
        }
        o.printf("%s\t%s\t%s\t%s\t%s%s\t%s%n", in.getAddress(), f == null ? "-" : f.getEntryPoint(), kind, what, rd ? "R" : "", wr ? "W" : "", in);
        if (f == null) return;
        int[] c = counts.computeIfAbsent(f, k -> new int[6]);
        c[kind.equals("abs") ? 0 : kind.equals("host") ? 1 : kind.equals("state") ? 2 : 3]++;
        if (rd) c[4]++;
        if (wr) c[5]++;
    }

    // direct-call ancestors of f (calls and tail jumps), up to `cap` functions
    Set<Function> ancestors(Function f, int cap) {
        Set<Function> seen = new LinkedHashSet<>();
        Deque<Function> q = new ArrayDeque<>();
        seen.add(f); q.add(f);
        while (!q.isEmpty() && seen.size() < cap) {
            Function g = q.poll();
            for (Reference r : getReferencesTo(g.getEntryPoint())) {
                if (!r.getReferenceType().isCall() && !r.getReferenceType().isJump()) continue;
                Function c = getFunctionContaining(r.getFromAddress());
                if (c != null && seen.add(c)) q.add(c);
            }
        }
        return seen;
    }

    @Override
    protected void run() throws Exception {
        String[] a = getScriptArgs();
        lst = currentProgram.getListing();
        Set<Long> absSet = new HashSet<>();
        for (long v : ABS) absSet.add(v);
        // thread entries: function addresses pushed or moved near a call of a thread / callback API
        Map<Function, String> entries = new LinkedHashMap<>();
        for (int i = 1; i < a.length; i++) {
            Function f = getFunctionAt(toAddr(Long.decode(a[i])));
            if (f != null) entries.put(f, "arg");
        }
        SymbolTable st = currentProgram.getSymbolTable();
        for (String api : THREAD_APIS) {
            for (Symbol s : st.getSymbols(api)) {
                List<Address> targets = new ArrayList<>();
                targets.add(s.getAddress());
                for (Reference r : getReferencesTo(s.getAddress())) targets.add(r.getFromAddress());  // thunks and IAT users
                for (Address t : targets)
                    for (Reference r : getReferencesTo(t)) {
                        Instruction in = lst.getInstructionAt(r.getFromAddress());
                        for (int k = 0; in != null && k < 14; k++, in = in.getPrevious())
                            for (int op = 0; op < in.getNumOperands(); op++)
                                for (Object o : in.getOpObjects(op))
                                    if (o instanceof Scalar) {
                                        long v = ((Scalar) o).getUnsignedValue();
                                        if (v < 0x400000L || v > 0x1800000L) continue;
                                        Function f = getFunctionAt(toAddr(v));
                                        if (f != null) entries.putIfAbsent(f, api);
                                    }
                    }
            }
        }
        try (PrintWriter o = new PrintWriter(a[0] + "-sites.tsv", "UTF-8")) {
            o.println("addr\tfunc\tkind\twhat\trw\tinstruction");
            InstructionIterator it = lst.getInstructions(true);
            while (it.hasNext()) {
                Instruction in = it.next();
                for (int op = 0; op < in.getNumOperands(); op++) {
                    Object[] objs = in.getOpObjects(op);
                    boolean mem = (in.getOperandType(op) & ghidra.program.model.lang.OperandType.DYNAMIC) != 0
                        || (in.getOperandType(op) & ghidra.program.model.lang.OperandType.ADDRESS) != 0;
                    boolean ebp = false, idx = false;
                    for (Object x : objs) if (x instanceof ghidra.program.model.lang.Register) {
                        String n = ((ghidra.program.model.lang.Register) x).getName();
                        if (n.equals("EBP")) ebp = true; else idx = true;
                    }
                    for (Object x : objs) {
                        if (x instanceof Address) {
                            long v = ((Address) x).getOffset();
                            if (absSet.contains(v)) site(o, in, "abs", String.format("0x%X", v));
                        }
                        if (!(x instanceof Scalar)) continue;
                        Scalar s = (Scalar) x;
                        long v = s.getUnsignedValue(), sv = s.getSignedValue();
                        if (absSet.contains(v)) { site(o, in, "abs", String.format("0x%X", v)); continue; }
                        if (!mem) continue;
                        for (long h : HOST) if (v == h) site(o, in, "host", String.format("+0x%X", v));
                        for (long h : STATE) if (v == h && !ebp) site(o, in, "state", String.format("+0x%X", v));
                        if (ebp && idx && sv <= -0x4900 && sv >= -0x4D00) site(o, in, "record", String.format("%d", sv));
                    }
                }
            }
        }
        try (PrintWriter o = new PrintWriter(a[0] + "-funcs.tsv", "UTF-8")) {
            o.println("func\tname\tsize\tabs\thost\tstate\trecord\treads\twrites\tancestors\tcapped\tthread-entries\taddress-taken");
            List<Function> fs = new ArrayList<>(counts.keySet());
            fs.sort(Comparator.comparing(Function::getEntryPoint));
            final int CAP = 4000;
            for (Function f : fs) {
                int[] c = counts.get(f);
                Set<Function> up = ancestors(f, CAP);
                StringBuilder te = new StringBuilder(), at = new StringBuilder();
                int nat = 0;
                for (Function g : up) {
                    if (entries.containsKey(g)) te.append(g.getEntryPoint()).append('(').append(entries.get(g)).append(") ");
                    boolean taken = false;
                    for (Reference r : getReferencesTo(g.getEntryPoint())) if (r.getReferenceType().isData()) taken = true;
                    if (taken && nat++ < 12) at.append(g.getEntryPoint()).append(' ');
                }
                o.printf("%s\t%s\t%d\t%d\t%d\t%d\t%d\t%d\t%d\t%d\t%s\t%s\t%s%n", f.getEntryPoint(), f.getName(), f.getBody().getNumAddresses(),
                    c[0], c[1], c[2], c[3], c[4], c[5], up.size(), up.size() >= CAP ? "capped" : "", te.toString().trim(), nat + ": " + at.toString().trim());
            }
        }
        try (PrintWriter o = new PrintWriter(a[0] + "-entries.tsv", "UTF-8")) {
            for (Map.Entry<Function, String> e : entries.entrySet())
                o.printf("%s\t%s\t%s%n", e.getKey().getEntryPoint(), e.getKey().getName(), e.getValue());
        }
        println("frame readers: " + counts.size() + " functions, " + entries.size() + " thread/callback entries");
    }
}
