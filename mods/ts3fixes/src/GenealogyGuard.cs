using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Socializing;
using Sims3.UI.CAS;

namespace Ts3Fixes
{
    // genealogycycle + spouseunlink (tunings kGenealogyCycle, kSpouseUnlink; default off until measured; docs/191 "family data").
    //
    // EA bugs in Sims3.Gameplay.Socializing.Genealogy [verified IL, 2026-11-11]:
    // 1. get_Ancestors (idx 0-35) builds the mAncestors cache breadth-first over mNaturalParents with no visited set. A
    //    shared ancestor (cousins who married, and their descendants) is added once per path, so the list doubles with
    //    each repeated cousin marriage; a loop in the parent links (a Sim adopted by its own descendant, reachable through
    //    the debug Adopt cheat or mods) never ends and grows the list until the game runs out of memory.
    // 2. ClearDerivedData (idx 0-37) recurses into every child with no visited set: the same loop overflows the stack.
    // 3. Divorce (idx 0-14) writes through mSpouse without a null check (NullReferenceException with no spouse).
    // 4. ApplySpouseUpdate (Marry, Engage, Dating; idx 0-17) links this <-> new spouse but never clears the old spouse's
    //    back-link. Adult pets that are romantic call Marry on every relationship update with no break-up first
    //    (LongTermRelationship.RomanceAndMarriageRelatedLtrUpdates), so a pet romantic with two others leaves one-way
    //    links; step-relation checks (IsStepParent, IsStepSibling, IsParentInLaw, IsSiblingInLaw) read them. Humans break up
    //    first (Partner = null -> Divorce), so this is a no-op for them unless the data is already one-way.
    //
    // genealogycycle (exact): get_Ancestors builds the same list without repeats (first occurrence kept, so the order
    // is EA's breadth-first order), stopping at nodes already listed; every caller only uses Contains or "any match"
    // loops (IsBloodRelated, IsAncestor), so results are unchanged, and a loop no longer hangs. ClearDerivedData clears
    // the same fields on the same nodes (EA's per-node step: mAncestors = null; mSiblings = null only while the node has
    // parents, because a parentless node's sibling list is primary data), visiting each node once, iteratively.
    // Divorce with no spouse clears this node's own fields as EA does and skips the null write.
    // spouseunlink (EA bug fix, changes results only where links were already one-way): before ApplySpouseUpdate links
    // A and B, A's old spouse and B's old spouse lose their link back to A or B the way Divorce clears both sides
    // (mSpouse = null, mPartnerType = 0), but only when that back-link actually points at A or B.
    // Counters: [2229] duplicate ancestor entries skipped, [2230] parent loops stopped, [2231] Divorce calls with no
    // spouse, [2232] old spouses unlinked, [2233] ApplySpouseUpdate calls seen, [2234] errors.
    public static partial class Fixes
    {
        static FieldInfo sGgAnc, sGgParents, sGgSiblings, sGgChildren, sGgSpouse, sGgPartner;

        sealed class GgRefEq : IEqualityComparer<Genealogy>
        {
            public bool Equals(Genealogy x, Genealogy y) { return object.ReferenceEquals(x, y); }
            public int GetHashCode(Genealogy o) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o); }
        }
        static readonly GgRefEq sGgRef = new GgRefEq();

        static void ApplyGenealogyGuard()
        {
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type g = typeof(Genealogy);
            sGgAnc = g.GetField("mAncestors", inst);
            sGgParents = g.GetField("mNaturalParents", inst);
            sGgSiblings = g.GetField("mSiblings", inst);
            sGgChildren = g.GetField("mChildren", inst);
            sGgSpouse = g.GetField("mSpouse", inst);
            sGgPartner = g.GetField("mPartnerType", inst);
            if (sGgAnc == null || sGgParents == null || sGgSiblings == null || sGgChildren == null || sGgSpouse == null || sGgPartner == null)
            {
                AppliedLog().Add("genealogyguard: fields not found, not patched");
                return;
            }
            Patch(typeof(Genealogy), "get_Ancestors", Type.EmptyTypes, typeof(GgHook), "AncestorsHook", "AncestorsOrig");
            Patch(typeof(Genealogy), "ClearDerivedData", Type.EmptyTypes, typeof(GgHook), "ClearHook", "ClearOrig");
            Patch(typeof(Genealogy), "Divorce", new Type[] { typeof(IGenealogy) }, typeof(GgHook), "DivorceHook", "DivorceOrig");
            Patch(typeof(Genealogy), "ApplySpouseUpdate", new Type[] { typeof(IGenealogy), typeof(PartnerType) }, typeof(GgHook), "SpouseHook", "SpouseOrig");
        }

        static void GgUnlink(Genealogy old, Genealogy from, int[] c)
        {
            if (old == null || object.ReferenceEquals(old, from)) return;
            if (!object.ReferenceEquals(sGgSpouse.GetValue(old), from)) return;
            sGgSpouse.SetValue(old, null);
            sGgPartner.SetValue(old, Enum.ToObject(sGgPartner.FieldType, 0));
            c[2232]++;
        }

        public class GgHook
        {
            public List<Genealogy> AncestorsHook()
            {
                if (!ShortcutOn("genealogycycle")) return AncestorsOrig();
                object self = this;
                List<Genealogy> cached = sGgAnc.GetValue(self) as List<Genealogy>;
                if (cached != null) return cached;
                int[] c = PatchCounters();
                List<Genealogy> anc = new List<Genealogy>();
                sGgAnc.SetValue(self, anc);  // EA sets the field before filling it too
                try
                {
                    Dictionary<Genealogy, bool> seen = new Dictionary<Genealogy, bool>(sGgRef);
                    List<Genealogy> queue = new List<Genealogy>();
                    List<Genealogy> p0 = sGgParents.GetValue(self) as List<Genealogy>;
                    if (p0 != null) queue.AddRange(p0);
                    for (int head = 0; head < queue.Count; head++)
                    {
                        Genealogy n = queue[head];
                        if (n == null) { anc.Add(n); continue; }  // EA adds whatever is in the list
                        if (seen.ContainsKey(n)) { c[2229]++; continue; }
                        seen[n] = true;
                        if (object.ReferenceEquals(n, self)) c[2230]++;  // reached itself: a parent loop
                        anc.Add(n);
                        List<Genealogy> pn = sGgParents.GetValue(n) as List<Genealogy>;
                        if (pn != null) queue.AddRange(pn);
                    }
                }
                catch (Exception) { c[2234]++; }
                return anc;
            }
            public List<Genealogy> AncestorsOrig() { return null; }

            public void ClearHook()
            {
                if (!ShortcutOn("genealogycycle")) { ClearOrig(); return; }
                int[] c = PatchCounters();
                try
                {
                    Dictionary<Genealogy, bool> seen = new Dictionary<Genealogy, bool>(sGgRef);
                    Stack<Genealogy> todo = new Stack<Genealogy>();
                    todo.Push((Genealogy)(object)this);
                    while (todo.Count > 0)
                    {
                        Genealogy n = todo.Pop();
                        if (n == null) continue;
                        if (seen.ContainsKey(n)) { if (object.ReferenceEquals(n, this)) c[2230]++; continue; }
                        seen[n] = true;
                        sGgAnc.SetValue(n, null);
                        List<Genealogy> parents = sGgParents.GetValue(n) as List<Genealogy>;
                        if (sGgSiblings.GetValue(n) != null && parents != null && parents.Count > 0) sGgSiblings.SetValue(n, null);
                        List<Genealogy> kids = sGgChildren.GetValue(n) as List<Genealogy>;
                        if (kids != null) for (int i = kids.Count - 1; i >= 0; i--) todo.Push(kids[i]);
                    }
                }
                catch (Exception) { c[2234]++; }
            }
            public void ClearOrig() { }

            public void DivorceHook(IGenealogy other)
            {
                if (ShortcutOn("genealogycycle") && sGgSpouse.GetValue(this) == null)
                {
                    PatchCounters()[2231]++;
                    sGgPartner.SetValue(this, Enum.ToObject(sGgPartner.FieldType, 0));
                    return;
                }
                DivorceOrig(other);
            }
            public void DivorceOrig(IGenealogy other) { }

            public void SpouseHook(IGenealogy other, PartnerType type)
            {
                int[] c = PatchCounters();
                c[2233]++;
                if (ShortcutOn("spouseunlink"))
                {
                    try
                    {
                        Genealogy me = (Genealogy)(object)this, nw = other as Genealogy;
                        GgUnlink(sGgSpouse.GetValue(me) as Genealogy == nw ? null : sGgSpouse.GetValue(me) as Genealogy, me, c);
                        if (nw != null) GgUnlink(sGgSpouse.GetValue(nw) as Genealogy == me ? null : sGgSpouse.GetValue(nw) as Genealogy, nw, c);
                    }
                    catch (Exception) { c[2234]++; }
                }
                SpouseOrig(other, type);
            }
            public void SpouseOrig(IGenealogy other, PartnerType type) { }
        }
    }
}
