using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace TermIDM.Desktop;

/// <summary>Validates machine-bound ECDSA license keys and stores them with Windows DPAPI.</summary>
internal static class LicenseService
{
    private const string Prefix = "Pass-User_";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TermIDM/license-cache/v1");
    private static readonly string TokenPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TermIDM", "license.bin");

    internal static string MachineId { get; } = ComputeMachineId();

    internal static bool TryLoad(out string? token)
    {
        token = null;
        try
        {
            if (!File.Exists(TokenPath)) return false;
            var protectedBytes = File.ReadAllBytes(TokenPath);
            var plaintext = Dpapi(protectedBytes, protect: false);
            token = Encoding.UTF8.GetString(plaintext);
            if (Validate(token, out _)) return true;
        }
        catch { }
        token = null;
        try { File.Delete(TokenPath); } catch { }
        return false;
    }

    internal static bool Validate(string token, out string reason)
    {
        reason = "The activation key is malformed or its signature is invalid.";
        if (string.IsNullOrWhiteSpace(token) || token.Length > 2048 || token.Any(char.IsControl)) return false;
        var split = token.LastIndexOf('.');
        if (split <= Prefix.Length || split == token.Length - 1) return false;
        var signedPayload = token[..split];
        if (!signedPayload.StartsWith(Prefix, StringComparison.Ordinal)) return false;

        var fields = signedPayload[Prefix.Length..].Split('|');
        if (fields.Length != 4 || fields.Any(field => field.Length == 0)) return false;
        string name, email, country;
        try
        {
            name = Uri.UnescapeDataString(fields[0]);
            email = Uri.UnescapeDataString(fields[1]);
            country = Uri.UnescapeDataString(fields[2]);
        }
        catch { return false; }
        if (name.Length is < 1 or > 100 || email.Length is < 3 or > 254 || country.Length is < 2 or > 80 ||
            !email.Contains('@') || fields[3].Length != 64 ||
            !fields[3].Equals(MachineId, StringComparison.OrdinalIgnoreCase))
        {
            reason = "This key is for a different device or contains invalid registration details.";
            return false;
        }

        try
        {
            using var verifier = ECDsa.Create();
            using var stream = typeof(LicenseService).Assembly.GetManifestResourceStream("TermIDM.Desktop.license-public-key.pem");
            if (stream is null) { reason = "The license verification key is missing from this build."; return false; }
            using var reader = new StreamReader(stream, Encoding.UTF8);
            verifier.ImportFromPem(reader.ReadToEnd());
            var signatureText = token[(split + 1)..].Replace('-', '+').Replace('_', '/');
            signatureText = signatureText.PadRight((signatureText.Length + 3) / 4 * 4, '=');
            var signature = Convert.FromBase64String(signatureText);
            var payload = Encoding.UTF8.GetBytes(signedPayload);
            if (verifier.VerifyData(payload, signature, HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            {
                reason = "Activation verified.";
                return true;
            }
        }
        catch { }
        return false;
    }

    internal static void Save(string token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(TokenPath)!);
        var bytes = Dpapi(Encoding.UTF8.GetBytes(token), protect: true);
        var temporary = TokenPath + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, TokenPath, overwrite: true);
        }
        finally { try { File.Delete(temporary); } catch { } }
    }

    private static string ComputeMachineId()
    {
        var machineGuid = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography", "MachineGuid", null) as string;
        if (string.IsNullOrWhiteSpace(machineGuid))
            throw new InvalidOperationException("Windows did not provide a machine identifier required for activation.");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("TermIDM-v1|" + machineGuid.Trim().ToUpperInvariant()))).ToLowerInvariant();
    }

    private static byte[] Dpapi(byte[] input, bool protect)
    {
        var inputHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        DataBlob output = default;
        try
        {
            var source = new DataBlob(input.Length, inputHandle.AddrOfPinnedObject());
            var optionalEntropy = new DataBlob(Entropy.Length, entropyHandle.AddrOfPinnedObject());
            var ok = protect
                ? CryptProtectData(ref source, "TermIDM activation", ref optionalEntropy, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref source, IntPtr.Zero, ref optionalEntropy, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
            inputHandle.Free();
            entropyHandle.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob(int length, IntPtr data)
    {
        public int Length = length;
        public IntPtr Data = data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob optionalEntropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob optionalEntropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
