using System.Runtime.InteropServices;
using System.Text;

namespace SIGFUR.Wpf.Services;

/// <summary>
/// Protege pequenos segredos com o DPAPI do Windows e escopo do usuário atual.
/// O conteúdo salvo no JSON não pode ser aberto por outro usuário do computador.
/// </summary>
public static class WindowsSecretProtector
{
    public static string Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return string.Empty;
        if (!OperatingSystem.IsWindows()) return Convert.ToBase64String(Encoding.UTF8.GetBytes(plainText));

        var input = BlobFromBytes(Encoding.UTF8.GetBytes(plainText));
        try
        {
            if (!CryptProtectData(ref input, "SIGFUR SisBol", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x1, out var output))
                throw new InvalidOperationException("O Windows não conseguiu proteger a credencial do SisBol.");
            try { return Convert.ToBase64String(BytesFromBlob(output)); }
            finally { if (output.Data != IntPtr.Zero) LocalFree(output.Data); }
        }
        finally { FreeBlob(input); }
    }

    public static string Unprotect(string? protectedText)
    {
        if (string.IsNullOrWhiteSpace(protectedText)) return string.Empty;
        byte[] encrypted;
        try { encrypted = Convert.FromBase64String(protectedText); }
        catch { return string.Empty; }
        if (!OperatingSystem.IsWindows()) return Encoding.UTF8.GetString(encrypted);

        var input = BlobFromBytes(encrypted);
        try
        {
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x1, out var output))
                return string.Empty;
            try { return Encoding.UTF8.GetString(BytesFromBlob(output)); }
            finally { if (output.Data != IntPtr.Zero) LocalFree(output.Data); }
        }
        finally { FreeBlob(input); }
    }

    private static DataBlob BlobFromBytes(byte[] bytes)
    {
        var blob = new DataBlob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Marshal.Copy(bytes, 0, blob.Data, bytes.Length);
        return blob;
    }

    private static byte[] BytesFromBlob(DataBlob blob)
    {
        if (blob.Length <= 0 || blob.Data == IntPtr.Zero) return [];
        var bytes = new byte[blob.Length];
        Marshal.Copy(blob.Data, bytes, 0, blob.Length);
        return bytes;
    }

    private static void FreeBlob(DataBlob blob)
    {
        if (blob.Data != IntPtr.Zero) Marshal.FreeHGlobal(blob.Data);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
