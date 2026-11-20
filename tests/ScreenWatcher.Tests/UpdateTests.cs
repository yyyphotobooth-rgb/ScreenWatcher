using System.Security.Cryptography;
using ScreenWatcher.Update;

namespace ScreenWatcher.Tests;

/// <summary>Auto-update: signature, version compare, release parsing, exe swap. No network.</summary>
public class UpdateTests
{
    static (ECDsa Key, string Public) NewKey()
    {
        var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (ec, Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo()));
    }

    static readonly byte[] Payload = [.. Enumerable.Range(0, 100_000).Select(i => (byte)(i * 31 + 7))];

    [Fact]
    public void Good_signature_is_accepted()
    {
        var (key, pub) = NewKey();
        string sig = UpdateSignature.Sign(new MemoryStream(Payload), key);
        Assert.True(UpdateSignature.Verify(new MemoryStream(Payload), sig, pub));
    }

    [Fact]
    public void Tampered_file_is_rejected()
    {
        var (key, pub) = NewKey();
        string sig = UpdateSignature.Sign(new MemoryStream(Payload), key);
        var tampered = (byte[])Payload.Clone();
        tampered[50_000] ^= 1;
        Assert.False(UpdateSignature.Verify(new MemoryStream(tampered), sig, pub));
        Assert.False(UpdateSignature.Verify(new MemoryStream([.. Payload, 0]), sig, pub));   // appended byte
    }

    [Fact]
    public void Signature_from_another_key_or_garbage_is_rejected()
    {
        var (key, _) = NewKey();
        var (_, otherPub) = NewKey();
        string sig = UpdateSignature.Sign(new MemoryStream(Payload), key);
        Assert.False(UpdateSignature.Verify(new MemoryStream(Payload), sig, otherPub));
        Assert.False(UpdateSignature.Verify(new MemoryStream(Payload), "not base64 !!", otherPub));
        Assert.False(UpdateSignature.Verify(new MemoryStream(Payload), Convert.ToBase64String(new byte[64]), otherPub));
        Assert.False(UpdateSignature.Verify(new MemoryStream(Payload), "", otherPub));
    }

    [Fact]
    public void Built_in_public_key_is_p256_and_rejects_foreign_signature()
    {
        using var ec = ECDsa.Create();
        ec.ImportSubjectPublicKeyInfo(Convert.FromBase64String(UpdateSignature.PublicKey), out _);
        Assert.Equal(256, ec.KeySize);
        var (key, _) = NewKey();
        Assert.False(UpdateSignature.Verify(new MemoryStream(Payload), UpdateSignature.Sign(new MemoryStream(Payload), key)));
    }

    [Theory]
    [InlineData("v1.1.0", 1, 1, 0)]
    [InlineData("1.2", 1, 2, 0)]
    [InlineData("V2.0.3", 2, 0, 3)]
    [InlineData("v1.3.0-beta", 1, 3, 0)]
    public void Tags_parse(string tag, int major, int minor, int build) =>
        Assert.Equal(new Version(major, minor, build), UpdateVersion.ParseTag(tag));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    public void Bad_tags_do_not_parse(string? tag) => Assert.Null(UpdateVersion.ParseTag(tag));

    [Theory]
    [InlineData("1.1.1", "1.1.0", true)]
    [InlineData("1.2.0", "1.1.9", true)]
    [InlineData("2.0.0", "1.9.9", true)]
    [InlineData("1.10.0", "1.9.0", true)]     // numeric, not text
    [InlineData("1.1.0", "1.1.0", false)]     // equal
    [InlineData("1.1", "1.1.0.0", false)]     // equal after normalising
    [InlineData("1.0.9", "1.1.0", false)]     // older
    public void Version_compare(string candidate, string current, bool newer) =>
        Assert.Equal(newer, UpdateVersion.IsNewer(Version.Parse(candidate), Version.Parse(current)));

    [Fact]
    public void App_version_is_1_2_0() => Assert.Equal(new Version(1, 2, 0), UpdateVersion.Current);

    static string Release(string tag, bool withSig = true, string scheme = "https", bool prerelease = false) => $$"""
        {
          "tag_name": "{{tag}}", "draft": false, "prerelease": {{(prerelease ? "true" : "false")}},
          "assets": [
            { "name": "ScreenWatcher.exe", "browser_download_url": "{{scheme}}://github.com/o/r/releases/download/{{tag}}/ScreenWatcher.exe" }
            {{(withSig ? $$""", { "name": "ScreenWatcher.exe.sig", "browser_download_url": "{{scheme}}://github.com/o/r/releases/download/{{tag}}/ScreenWatcher.exe.sig" }""" : "")}}
          ]
        }
        """;

    static readonly Version Current = new(1, 1, 0);

    [Fact]
    public void Newer_release_with_both_assets_is_offered()
    {
        var info = UpdateChecker.Parse(Release("v1.2.0"), Current);
        Assert.NotNull(info);
        Assert.Equal(new Version(1, 2, 0), info.Version);
        Assert.EndsWith("/ScreenWatcher.exe", info.ExeUrl.AbsolutePath);
        Assert.EndsWith("/ScreenWatcher.exe.sig", info.SigUrl.AbsolutePath);
        Assert.Equal("1.2", info.Display(Current));
        Assert.Equal("1.1.1", UpdateChecker.Parse(Release("v1.1.1"), Current)!.Display(Current));
    }

    [Theory]
    [InlineData("v1.1.0")]   // equal
    [InlineData("v1.0.5")]   // older
    public void No_update_when_equal_or_older(string tag) => Assert.Null(UpdateChecker.Parse(Release(tag), Current));

    [Fact]
    public void No_update_without_sig_asset_over_http_prerelease_or_bad_json()
    {
        Assert.Null(UpdateChecker.Parse(Release("v1.2.0", withSig: false), Current));
        Assert.Null(UpdateChecker.Parse(Release("v1.2.0", scheme: "http"), Current));
        Assert.Null(UpdateChecker.Parse(Release("v1.2.0", prerelease: true), Current));
        Assert.Null(UpdateChecker.Parse("{ not json", Current));
        Assert.Null(UpdateChecker.Parse("""{ "message": "Not Found" }""", Current));
    }

    [Fact]
    public void Swap_replaces_exe_and_keeps_old_one()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sw-update-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string target = Path.Combine(dir, "ScreenWatcher.exe"), incoming = Path.Combine(dir, "new.exe");
            File.WriteAllText(target, "v1");
            File.WriteAllText(target + UpdateInstaller.OldSuffix, "older leftover");
            File.WriteAllText(incoming, "v2");
            UpdateInstaller.Swap(incoming, target);
            Assert.Equal("v2", File.ReadAllText(target));
            Assert.Equal("v1", File.ReadAllText(target + UpdateInstaller.OldSuffix));
            Assert.False(File.Exists(incoming));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Swap_failure_puts_old_exe_back()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sw-update-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string target = Path.Combine(dir, "ScreenWatcher.exe");
            File.WriteAllText(target, "v1");
            Assert.Throws<UpdateFailedException>(() => UpdateInstaller.Swap(Path.Combine(dir, "missing.exe"), target));
            Assert.Equal("v1", File.ReadAllText(target));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData("1.1.0", "1.1")]
    [InlineData("1.1.1", "1.1.1")]
    [InlineData("1.1.2", "1.1.2")]
    [InlineData("2.0.0", "2.0")]
    public void Short_version_shows_patch_only_when_nonzero(string v, string expected) =>
        Assert.Equal(expected, AppFiles.ShortVersion(Version.Parse(v)));

    [Fact]
    public void Old_exe_is_deleted_after_a_lock_is_released()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sw-old-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string old = Path.Combine(dir, "ScreenWatcher.exe.old");
            File.WriteAllText(old, "old");
            var lockStream = new FileStream(old, FileMode.Open, FileAccess.Read, FileShare.None);
            var release = Task.Run(() => { Thread.Sleep(2000); lockStream.Dispose(); });
            var r = UpdateInstaller.CleanOldExe(old, 999999, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(100));
            release.Wait();
            Assert.True(r.Deleted, r.Error);
            Assert.True(r.Attempts > 1);
            Assert.False(File.Exists(old));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Old_exe_still_locked_reports_failure_without_throwing()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sw-old-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string old = Path.Combine(dir, "ScreenWatcher.exe.old");
        File.WriteAllText(old, "old");
        using (new FileStream(old, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var r = UpdateInstaller.CleanOldExe(old, 999999, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(100));
            Assert.False(r.Deleted);
            Assert.NotNull(r.Error);
        }
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void Startup_strips_after_update_args()
    {
        // A pid that is not running: returns at once.
        var rest = UpdateInstaller.OnStartup(["--no-send", UpdateInstaller.AfterUpdateArg, "999999"]);
        Assert.Equal(["--no-send"], rest);
        Assert.Equal(["--no-send"], UpdateInstaller.OnStartup(["--no-send"]));
    }
}
