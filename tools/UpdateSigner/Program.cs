using System.Security.Cryptography;
using ScreenWatcher.Update;

// UpdateSigner keygen <keyDir>          create the signing key once (refuses to overwrite)
// UpdateSigner sign <exe> <privateKey>  write <exe>.sig and verify it with the app's built-in public key
// The private key is never printed or logged.
const string KeyFile = "update-signing-private.pem";

try
{
    switch (args.FirstOrDefault())
    {
        case "keygen" when args.Length == 2:
        {
            Directory.CreateDirectory(args[1]);
            string path = Path.Combine(args[1], KeyFile);
            if (File.Exists(path)) { Console.Error.WriteLine($"Key already exists: {path} (not overwritten)"); return 2; }
            using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            File.WriteAllText(path, ec.ExportPkcs8PrivateKeyPem());
            string pub = Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo());
            File.WriteAllText(Path.Combine(args[1], "update-signing-public.txt"), pub + Environment.NewLine);
            Console.WriteLine("Private key: " + path);
            Console.WriteLine("Public key (put in UpdateSignature.PublicKey): " + pub);
            return 0;
        }
        case "sign" when args.Length == 3:
        {
            string exe = args[1];
            using var ec = ECDsa.Create();
            ec.ImportFromPem(File.ReadAllText(args[2]));
            string sig;
            using (var fs = File.OpenRead(exe)) sig = UpdateSignature.Sign(fs, ec);
            bool ok;
            using (var fs = File.OpenRead(exe)) ok = UpdateSignature.Verify(fs, sig);
            if (!ok) { Console.Error.WriteLine("Signature does NOT verify with the app's built-in public key (wrong key file?)"); return 3; }
            File.WriteAllText(exe + ".sig", sig);
            Console.WriteLine($"Signed: {exe}.sig (verified with the built-in public key)");
            return 0;
        }
        default:
            Console.Error.WriteLine("Usage: UpdateSigner keygen <keyDir> | sign <exe> <privateKeyPem>");
            return 1;
    }
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
{
    Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
    return 4;
}
