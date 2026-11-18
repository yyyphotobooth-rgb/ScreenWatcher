using System.Security.Cryptography;
using System.Text;

namespace ScreenWatcher.Config;

/// <summary>
/// The Discord bot token, encrypted with DPAPI for the current Windows user
/// (<see cref="DataProtectionScope.CurrentUser"/>): only this Windows account on this PC can
/// decrypt the file. Kept in %LOCALAPPDATA%\ScreenWatcher\discord-token.bin. The token is never
/// written anywhere else and never logged.
/// </summary>
public sealed class TokenStore(string path)
{
    // Extra entropy so another app running as the same user cannot decrypt the file by simply
    // calling DPAPI without knowing this value. (Not a secret on its own; DPAPI is the protection.)
    static readonly byte[] Entropy = "ScreenWatcher.DiscordToken.v1"u8.ToArray();

    public static string DefaultPath => Path.Combine(AppSettings.DataRoot, "discord-token.bin");

    public string FilePath { get; } = path;

    public bool Exists => File.Exists(FilePath);

    public void Save(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Empty token.", nameof(token));
        byte[] plain = Encoding.UTF8.GetBytes(token.Trim());
        try
        {
            byte[] cipher = Protect(plain);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            File.WriteAllBytes(FilePath, cipher);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    /// <summary>The stored token, or null when there is none or it cannot be decrypted
    /// (e.g. the file came from another Windows user or PC).</summary>
    public string? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            byte[] plain = Unprotect(File.ReadAllBytes(FilePath));
            try { return Encoding.UTF8.GetString(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException) { return null; }
    }

    public void Clear()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static byte[] Protect(byte[] plain) => ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
    public static byte[] Unprotect(byte[] cipher) => ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
}
