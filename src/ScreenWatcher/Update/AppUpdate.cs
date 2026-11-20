using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace ScreenWatcher.Update;

/// <summary>A newer release on GitHub: its version and the two assets the update needs.</summary>
public sealed record UpdateInfo(Version Version, string Tag, Uri ExeUrl, Uri SigUrl)
{
    /// <summary>"1.2" (Major.Minor, like the window title; "1.2.3" when only the patch differs).</summary>
    public string Display(Version current) =>
        Version.Major == current.Major && Version.Minor == current.Minor ? Version.ToString(3) : $"{Version.Major}.{Version.Minor}";
}

/// <summary>Release versions: tags like "v1.1.0" or "1.1"; compared as Major.Minor.Build.</summary>
public static class UpdateVersion
{
    public static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        string s = tag.Trim().TrimStart('v', 'V');
        int cut = s.IndexOfAny(['-', '+']);       // "1.2.0-beta" -> "1.2.0" (pre-releases never reach /latest anyway)
        if (cut >= 0) s = s[..cut];
        return Version.TryParse(s, out var v) ? Normalize(v) : null;
    }

    /// <summary>Major.Minor.Build (missing parts 0, revision dropped).</summary>
    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    public static bool IsNewer(Version candidate, Version current) => Normalize(candidate) > Normalize(current);

    /// <summary>This app's version (the csproj &lt;Version&gt;).</summary>
    public static Version Current => Normalize(typeof(UpdateVersion).Assembly.GetName().Version ?? new Version(0, 0, 0));
}

/// <summary>
/// ECDSA P-256 / SHA-256 signature of the release exe. The .sig file is the base64 of the 64-byte
/// IEEE P1363 signature (r||s) over the exe's bytes. publish.ps1 makes it with the private key kept
/// outside the repo (C:\ScreenWatcher-keys); only the public key is in the app.
/// </summary>
public static class UpdateSignature
{
    /// <summary>SubjectPublicKeyInfo (base64) of the release signing key.</summary>
    public const string PublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEfB3n5cnle0DngqSi9rf6XctLYd/m+4psIjwRKF6JEqad0q/DN171Xu0cH41tJXj+WsAprKsaeso141o5OGG4Vg==";

    public static bool Verify(Stream data, string sigText) => Verify(data, sigText, PublicKey);

    public static bool Verify(Stream data, string sigText, string publicKeyBase64)
    {
        try
        {
            byte[] sig = Convert.FromBase64String(sigText.Trim());
            if (sig.Length != 64) return false;
            using var ec = ECDsa.Create();
            ec.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            if (ec.KeySize != 256) return false;
            return ec.VerifyData(data, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException) { return false; }
    }

    /// <summary>Signs (release tool only; the app never holds a private key).</summary>
    public static string Sign(Stream data, ECDsa privateKey) =>
        Convert.ToBase64String(privateKey.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
}

/// <summary>Asks GitHub for the latest release (no token, public repo). Any failure = no update.</summary>
public static class UpdateChecker
{
    public const string LatestUrl = "https://api.github.com/repos/yyyphotobooth-rgb/ScreenWatcher/releases/latest";
    public const string ExeName = "ScreenWatcher.exe";
    public const string SigName = "ScreenWatcher.exe.sig";

    internal static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        h.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ScreenWatcher-Updater", UpdateVersion.Current.ToString(3)));
        return h;
    }

    /// <summary>The newer release, or null (same/older version, assets missing, network or parse error).</summary>
    public static async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            using var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return Parse(await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false), current);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException) { return null; }
    }

    /// <summary>Reads a /releases/latest response. Null unless it is newer than <paramref name="current"/>
    /// and has both https assets.</summary>
    public static UpdateInfo? Parse(string json, Version current)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) return null;
            if (root.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True) return null;
            string? tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (UpdateVersion.ParseTag(tag) is not { } v || !UpdateVersion.IsNewer(v, current)) return null;
            Uri? exe = null, sig = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) continue;
                    if (string.Equals(name, ExeName, StringComparison.OrdinalIgnoreCase)) exe = uri;
                    else if (string.Equals(name, SigName, StringComparison.OrdinalIgnoreCase)) sig = uri;
                }
            return exe is null || sig is null ? null : new UpdateInfo(v, tag!, exe, sig);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return null; }
    }
}

/// <summary>Why an update click did not finish (Thai text for the banner).</summary>
public sealed class UpdateFailedException(string thai, Exception? inner = null) : Exception(thai, inner);

/// <summary>
/// Download → verify → swap → restart, only after the user clicked "อัปเดต". The running exe is
/// renamed to ScreenWatcher.exe.old (Windows allows renaming a running exe), the verified new exe is
/// moved into its place, the new one is started and this one exits; the next start deletes the .old.
/// Settings, token and data live in the data folder and are not touched.
/// </summary>
public static class UpdateInstaller
{
    public const string OldSuffix = ".old";
    public const string AfterUpdateArg = "--after-update";

    /// <summary>True only for the one-file build (dist\ScreenWatcher.exe); never under dotnet run.</summary>
    // Assembly.Location is empty exactly when running from the single-file bundle: that is the test.
#pragma warning disable IL3000
    public static bool CanSelfUpdate =>
        string.IsNullOrEmpty(typeof(UpdateInstaller).Assembly.Location)
#pragma warning restore IL3000
        && Environment.ProcessPath is { } p
        && string.Equals(Path.GetFileName(p), UpdateChecker.ExeName, StringComparison.OrdinalIgnoreCase);

    static string TempRoot => Path.Combine(Path.GetTempPath(), "ScreenWatcher-update");

    /// <summary>Downloads both assets to %TEMP% and checks the signature. Returns the verified exe path.</summary>
    public static async Task<string> DownloadAndVerifyAsync(UpdateInfo info, CancellationToken ct = default)
    {
        string dir = Path.Combine(TempRoot, info.Version.ToString(3));
        string exe = Path.Combine(dir, UpdateChecker.ExeName);
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);
            string sig;
            try
            {
                sig = await UpdateChecker.Http.GetStringAsync(info.SigUrl, ct).ConfigureAwait(false);
                using var resp = await UpdateChecker.Http.GetAsync(info.ExeUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = new FileStream(exe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                throw new UpdateFailedException("ดาวน์โหลดไม่สำเร็จ", ex);
            }
            bool ok;
            await using (var fs = File.OpenRead(exe)) ok = UpdateSignature.Verify(fs, sig);
            if (!ok) throw new UpdateFailedException("ไฟล์ไม่ผ่านการตรวจลายเซ็น");
            return exe;
        }
        catch (UpdateFailedException)
        {
            TryDeleteDir(dir);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeleteDir(dir);
            throw new UpdateFailedException("บันทึกไฟล์ดาวน์โหลดไม่ได้", ex);
        }
    }

    /// <summary>Puts <paramref name="newExe"/> in place of <paramref name="targetExe"/> (which may be
    /// running), keeping the old one as targetExe.old. On any failure the old exe is put back.</summary>
    public static void Swap(string newExe, string targetExe)
    {
        string old = targetExe + OldSuffix;
        try
        {
            if (File.Exists(old)) File.Delete(old);
            File.Move(targetExe, old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UpdateFailedException("เปลี่ยนไฟล์โปรแกรมไม่ได้", ex);
        }
        try { File.Move(newExe, targetExe); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { if (!File.Exists(targetExe)) File.Move(old, targetExe); } catch (Exception) { }
            throw new UpdateFailedException("เปลี่ยนไฟล์โปรแกรมไม่ได้", ex);
        }
    }

    /// <summary>Swaps the running exe for the verified one and starts it (it waits for this process to
    /// exit). The caller then closes the app.</summary>
    public static void InstallAndRestart(string verifiedExe, IEnumerable<string> args)
    {
        string target = Environment.ProcessPath ?? throw new UpdateFailedException("ไม่พบไฟล์โปรแกรม");
        Swap(verifiedExe, target);
        var psi = new ProcessStartInfo(target) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(target)! };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(AfterUpdateArg);
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        try { Process.Start(psi); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Could not start the new one: put the old exe back so nothing is lost.
            try { File.Delete(target); File.Move(target + OldSuffix, target); } catch (Exception) { }
            throw new UpdateFailedException("เปิดรุ่นใหม่ไม่ได้", ex);
        }
        TryDeleteDir(Path.GetDirectoryName(verifiedExe)!);
    }

    /// <summary>Start-up: wait for the process that started this one (after an update) to exit, then
    /// delete the previous exe (.old) and leftover downloads. Never throws. Returns the args without
    /// the --after-update pair.</summary>
    public static string[] OnStartup(string[] args)
    {
        int i = Array.FindIndex(args, a => string.Equals(a, AfterUpdateArg, StringComparison.OrdinalIgnoreCase));
        if (i >= 0)
        {
            if (i + 1 < args.Length && int.TryParse(args[i + 1], out int pid))
            {
                try { using var p = Process.GetProcessById(pid); p.WaitForExit(15000); }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
            args = [.. args.Take(i), .. args.Skip(i + 2)];
        }
        if (CanSelfUpdate)
        {
            try { File.Delete(Environment.ProcessPath! + OldSuffix); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            TryDeleteDir(TempRoot);
        }
        return args;
    }

    static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
