using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.SimIFace;
using Sims3.UI;

namespace Ts3Fixes
{
    // blanktns (tuning kBlankTns, off by default, on in betas; docs/869). Diagnostic: a notification shown with empty text names
    // the code that sent it. Beta testers see empty notices (also without ts3fixes); EA's StringTable.GetLocalizedString answers
    // string.Empty for a key with no text in the loaded string tables [verified, decompiled], so the blank box alone cannot
    // say which mod or EA path sent it.
    //
    // [verified, decompiled + call graph] Every StyledNotification EA's script code builds is made in the 5-argument static
    // StyledNotification.Show (the only newobj of the class), which hands it straight to NotificationManager.Add with the
    // sender still on the stack; the 1-, 2- and 3-argument overloads, NotificationSystem.Show and the Sim.ShowTNS* helpers all
    // end there. The text is first read when the queue displays the notice (instance Show(): mTextArea.Caption = mFormat.mText);
    // neither the constructor nor Add/ShouldAddNotification reads it. BtnHook swaps NotificationManager.ShouldAddNotification,
    // the first call inside Add, so the sender is still on the stack. In game both earlier targets were refused as already
    // compiled when ts3fixes loaded: Show (pass 13, 2026-11-22) and then Add (pass 14). [inferred] Some startup code calls
    // Show while there is no NotificationManager, and compiling Show marks the methods it calls (Add, the constructor) too;
    // Add itself never ran, so the methods Add calls stay unmarked. The hook also sees a mod that calls the public
    // constructor and Add itself.
    // Switch on and the main text (mFormat.mText) null, empty or whitespace: the text becomes "[blank notice] sent by A < B < C",
    // the first 3 frames outside the notification plumbing (BtnCore.IsPlumbing), full type names; the same line plus the style
    // and up to 8 frames with their assemblies go to ts3native.log ([UserDiag] channel, DiagLine), for the first kBtnLogCases
    // blank notices of a session. A notice with text is untouched. Format is a struct: the hook reads a boxed copy of mFormat,
    // changes its text and stores it back before EA queues the notice. A null text with a timeout still throws in EA's Show
    // before Add (unchanged; such a notice never shows). Switch off: EA's path, and a count of blank notices seen (no stack
    // read). The hook never throws: an error is counted and EA's ShouldAddNotification runs with the notice as it was.
    // Household notices are included on purpose: only a blank notice's text changes, nothing in the game state.
    // Not done on purpose: no hook on StringTable (a hot path), so the missing string key itself is not reported.
    // Counters: [2362] blank notices renamed (switch on), [2363] blank notices seen with the switch off, [2364] blank notices not
    // logged (session cap reached, or the log channel refused the line), [2365] errors.
    // Check: BlankTnsCheck.
    public static partial class Fixes
    {
        const int kBtnFrames = 3, kBtnLogFrames = 8, kBtnLogCases = 20;
        static readonly Type[] kBtnArgs = { typeof(Notification), typeof(NotificationManager.TNSCategory) };
        static readonly FieldInfo sBtnFormat = typeof(StyledNotification).GetField("mFormat", kInstAll);  // the notice's Format (its text)
        static readonly List<string> sBtnLast = new List<string>();  // the last 3 senders, newest last
        static int sBtnLogged;
        static bool sBtnHooked;

        static void ApplyBlankTns()
        {
            if (sBtnHooked) return;
            sBtnHooked = true;
            if (sBtnFormat == null) { AppliedLog().Add("blanktns: not found: StyledNotification.mFormat"); return; }
            Patch(typeof(NotificationManager), "ShouldAddNotification", kBtnArgs, typeof(BtnHook), "ShouldHook", "ShouldOrig");
        }

        public class BtnHook
        {
            public bool ShouldHook(Notification notification, NotificationManager.TNSCategory category)
            {
                try
                {
                    StyledNotification sn = notification as StyledNotification;
                    if (sn != null)
                    {
                        StyledNotification.Format f = (StyledNotification.Format)sBtnFormat.GetValue(sn);
                        if (BtnCore.IsBlank(f.mText))
                        {
                            if (ShortcutOn("blanktns")) { f.mText = BtnNote(f); sBtnFormat.SetValue(sn, f); }
                            else PatchCounters()[2363]++;
                        }
                    }
                }
                catch (Exception) { PatchCounters()[2365]++; }
                return ShouldOrig(notification, category);
            }
            public bool ShouldOrig(Notification notification, NotificationManager.TNSCategory category) { return true; }  // receives EA's NotificationManager.ShouldAddNotification
        }

        // The sender's text for a blank notice; logs it with the style and a fuller stack.
        static string BtnNote(StyledNotification.Format f)
        {
            int[] c = PatchCounters();
            c[2362]++;
            System.Diagnostics.StackTrace st = new System.Diagnostics.StackTrace(1, false);
            int n = st.FrameCount;
            string[] types = new string[n], methods = new string[n], asms = new string[n];
            for (int i = 0; i < n; i++)
            {
                MethodBase m = st.GetFrame(i).GetMethod();
                Type t = m != null ? m.DeclaringType : null;
                types[i] = t != null ? t.FullName : null;
                methods[i] = m != null ? m.Name : null;
                asms[i] = t != null ? t.Assembly.GetName().Name : "?";
            }
            string text = BtnCore.Text(BtnCore.Pick(types, methods, null, kBtnFrames));
            if (sBtnLast.Count >= 3) sBtnLast.RemoveAt(0);
            sBtnLast.Add(text.Substring(BtnCore.kPrefix.Length));
            if (sBtnLogged >= kBtnLogCases) { c[2364]++; return text; }
            sBtnLogged++;
            try
            {
                string kind = f.mText == null ? "null text" : f.mText.Length == 0 ? "empty text" : "whitespace text";
                if (!DiagLine("blanktns: " + text + "; style " + f.mStyle + ", " + kind + ", case " + c[2362])
                    || !DiagList("blanktns stack: ", BtnCore.Pick(types, methods, asms, kBtnLogFrames))) c[2364]++;
            }
            catch (Exception) { c[2364]++; c[2365]++; }  // the notice still gets its text
            return text;
        }

        // BlankTnsCheck run: shows one blank notice (a kSystemMessage) and one with text from this method, and reads back the
        // text EA kept in each. off BROKEN = the blank one stays blank; on FIXED = it names this method
        // ("Ts3Fixes.Fixes.BlankTnsCheck" first). Control: the notice with text keeps it. Two notices appear in game.
        //   result  counters 2362-2365 and the last 3 senders
        public static object BlankTnsCheck(string what)
        {
            int[] c = PatchCounters();
            string live = CkLive("blanktns");  // "" when the swap is in (CheckLiveMap.cs)
            string counters = "blanktns: renamed 2362=" + c[2362] + ", seen with the switch off 2363=" + c[2363] + ", not logged 2364=" + c[2364]
                + ", errors 2365=" + c[2365] + "; last senders: " + (sBtnLast.Count == 0 ? "none" : string.Join(" | ", sBtnLast.ToArray()))
                + "; live path: " + (live.Length == 0 ? "ok" : live);
            if (what == "result") return counters;
            if (what != "run") return "blanktns <run|result>";
            bool on = ShortcutOn("blanktns");
            const string kept = "blanktns check: a notice with text";
            string blank = BtnShown(StyledNotification.Show(new StyledNotification.Format("", StyledNotification.NotificationStyle.kSystemMessage)));
            string control = BtnShown(StyledNotification.Show(new StyledNotification.Format(kept, StyledNotification.NotificationStyle.kSystemMessage)));
            bool named = blank != null && blank.StartsWith(BtnCore.kPrefix + "Ts3Fixes.Fixes.BlankTnsCheck");
            bool stayed = blank != null && BtnCore.IsBlank(blank);
            bool controlOk = control == kept;
            string v = on ? (named ? "FIXED" : "UNEXPECTED") : (stayed ? "BROKEN" : "UNEXPECTED");
            string verdict = !controlOk || live.Length > 0 || sBtnFormat == null ? "UNEXPECTED" : v;
            return "blanktns check (" + (on ? "on" : "off") + "): blank notice's text EA got=\"" + (blank ?? "<no notice>") + "\" (" + v
                + "); control kept its text=" + controlOk + "; verdict: " + verdict + "; " + counters;
        }

        // The text EA stored in a shown notice; null when none was made (tutorial running, no notification manager).
        static string BtnShown(StyledNotification n)
        {
            if (n == null || sBtnFormat == null) return null;
            return ((StyledNotification.Format)sBtnFormat.GetValue(n)).mText;
        }
    }
}
