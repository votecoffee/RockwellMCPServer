using System.Security.AccessControl;
using System.Security.Principal;
using Studio5000.Core.Resources;
using Xunit;

namespace Studio5000.Tests;

public sealed class ResourceDirectoryPrivacyTests
{
    [Fact]
    public void CorrectPrivateDirectoryDoesNotReapplyInheritablePermissionsToItsChildren()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Path.GetTempPath(), "rockwell-acl-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.True(ResourceLedger.PrivateDirectory(path));
            File.WriteAllText(Path.Combine(path, "retained-fixture.json"), "{}");
            Assert.False(ResourceLedger.PrivateDirectory(path));
            var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access);
            Assert.True(security.AreAccessRulesProtected);
            Assert.Equal(2, security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Count);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Fact]
    public void ChangedPermissionsAreRecheckedAndRepairedInsteadOfTrustingACachedPath()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Path.GetTempPath(), "rockwell-acl-" + Guid.NewGuid().ToString("N"));
        try
        {
            ResourceLedger.PrivateDirectory(path);
            var directory = new DirectoryInfo(path); var security = directory.GetAccessControl(AccessControlSections.Access);
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow));
            directory.SetAccessControl(security);
            Assert.True(ResourceLedger.PrivateDirectory(path));
            Assert.False(ResourceLedger.PrivateDirectory(path));
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
}
