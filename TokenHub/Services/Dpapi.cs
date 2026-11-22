using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TokenHub.Services;

/// <summary>
/// DPAPI wrapper (crypt32) used to keep API keys out of plain text in settings.json.
/// Machine scope is used on purpose: the optional Windows service runs as LocalSystem
/// and would not be able to read user-scoped blobs. Machine-scoped ciphertext is bound
/// to this PC, so a copy (backup, cloud sync, another machine) cannot be decrypted.
/// </summary>
internal static class Dpapi
{
    private const string Prefix = "enc:v1:";
    private const int CryptprotectLocalMachine = 0x4;
    private const int CryptprotectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        IntPtr entropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        IntPtr entropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    /// <summary>Encrypts a secret. Returns null when protection is not possible.</summary>
    public static string? TryProtect(string? plain)
    {
        if (string.IsNullOrEmpty(plain) || plain.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return plain;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(plain);
        IntPtr input = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, input, bytes.Length);
            var blobIn = new DataBlob { cbData = bytes.Length, pbData = input };

            if (!CryptProtectData(ref blobIn, "TokenHub", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptprotectLocalMachine | CryptprotectUiForbidden, out var blobOut))
            {
                return null;
            }

            try
            {
                var protectedBytes = new byte[blobOut.cbData];
                Marshal.Copy(blobOut.pbData, protectedBytes, 0, blobOut.cbData);
                return Prefix + Convert.ToBase64String(protectedBytes);
            }
            finally
            {
                LocalFree(blobOut.pbData);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(input);
        }
    }

    /// <summary>Decrypts a value produced by <see cref="TryProtect"/>. Plain text passes through.</summary>
    public static string? TryUnprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return stored;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(stored[Prefix.Length..]);
        }
        catch
        {
            return null;
        }

        IntPtr input = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, input, bytes.Length);
            var blobIn = new DataBlob { cbData = bytes.Length, pbData = input };

            if (!CryptUnprotectData(ref blobIn, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptprotectLocalMachine | CryptprotectUiForbidden, out var blobOut))
            {
                return null;
            }

            try
            {
                var plainBytes = new byte[blobOut.cbData];
                Marshal.Copy(blobOut.pbData, plainBytes, 0, blobOut.cbData);
                return Encoding.UTF8.GetString(plainBytes);
            }
            finally
            {
                LocalFree(blobOut.pbData);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(input);
        }
    }

    /// <summary>WhatsApp-style mask used everywhere a key might otherwise be shown or logged.</summary>
    public static string Mask(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return "(нет)";
        }

        return key.Length <= 12 ? key[..1] + "…" : $"{key[..8]}…{key[^4..]}";
    }
}

/// <summary>Transparently encrypts a string property on disk, decrypts it in memory.</summary>
internal sealed class ProtectedStringConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        string? value = reader.GetString();
        return Dpapi.TryUnprotect(value) ?? value ?? string.Empty;
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(Dpapi.TryProtect(value) ?? value);
    }
}
