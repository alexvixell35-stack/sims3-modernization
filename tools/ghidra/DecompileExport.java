// DecompileExport.java - export pseudo-C per function to a directory, for bulk scanning.
// Read-only. Our own code. Args:
//   -postScript DecompileExport.java <outDir> [addrListFile]
// If addrListFile is given (one 0xADDR per line), only those functions are exported;
// otherwise every function in the program is exported as <outDir>/<hexEntry>.c .
// Pair with tools/rea/scan-pseudoc.py to triage the output.
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.address.Address;
import java.io.*;
import java.util.*;

public class DecompileExport extends GhidraScript {
    public void run() throws Exception {
        String[] a = getScriptArgs();
        if (a.length < 1) { println("need <outDir> [addrListFile]"); return; }
        File out = new File(a[0]); out.mkdirs();
        Set<Long> want = null;
        if (a.length >= 2) {
            want = new HashSet<>();
            BufferedReader br = new BufferedReader(new FileReader(a[1]));
            for (String ln; (ln = br.readLine()) != null; ) {
                ln = ln.trim(); if (ln.isEmpty()) continue;
                if (ln.startsWith("0x")||ln.startsWith("0X")) ln = ln.substring(2);
                try { want.add(Long.parseLong(ln, 16)); } catch (Exception e) {}
            }
            br.close();
        }
        DecompInterface d = new DecompInterface();
        d.openProgram(currentProgram);
        int n = 0;
        FunctionIterator it = currentProgram.getFunctionManager().getFunctions(true);
        while (it.hasNext() && !monitor.isCancelled()) {
            Function f = it.next();
            long ent = f.getEntryPoint().getOffset();
            if (want != null && !want.contains(ent)) continue;
            DecompileResults r = d.decompileFunction(f, 45, monitor);
            if (r == null || !r.decompileCompleted()) continue;
            DecompiledFunction df = r.getDecompiledFunction();
            if (df == null) continue;
            String hex = String.format("0x%06X", ent);
            try (PrintWriter pw = new PrintWriter(new File(out, hex + ".c"))) {
                pw.println("// " + hex + "  " + f.getName());
                pw.print(df.getC());
            }
            if ((++n % 200) == 0) println("exported " + n);
        }
        println("> DecompileExport done: " + n + " functions -> " + out);
    }
}
