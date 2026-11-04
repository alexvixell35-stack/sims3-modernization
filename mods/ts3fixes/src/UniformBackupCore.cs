namespace Ts3Fixes
{
    // Game-independent decision of uniformbackup (UniformBackup.cs, issue #101). Self-check: tools/selftest/uniformbackup.
    public static class UniformBackupCore
    {
        // After Occupation.ApplyOccupationUniform: clear the static backup when EA's call uncached it and left it in the
        // field. EA uncaches a non-null backup that the Sim is not wearing once the call gets past its early returns, and
        // a call that gets past them replaces Career[0]; a call that set a new backup has a different object in the field.
        public static bool ClearAfter(bool hadBackup, bool sameBackupAfter, bool backupWorn, bool careerReplaced)
        {
            return hadBackup && sameBackupAfter && !backupWorn && careerReplaced;
        }
    }
}
