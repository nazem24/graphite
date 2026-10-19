using System.Runtime.InteropServices;
using System.Text;

namespace Graphite.App.Interop;

/// <summary>
/// Windows Data Protection (DPAPI) for the current user: what is protected here can only be read back by the
/// same Windows account on this PC. Used for the Canvas access token, so the settings file never holds it in
/// plain text. Calls crypt32 directly, so no extra package is needed.
/// </summary>
public static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    // Ties the protected bytes to Graphite: another program running as the same user would also need this.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Graphite.Canvas.Token.v1");

    /// <summary>Protect a secret; returns base64 for storing in a settings file.</summary>
    public static string Protect(string secret)
    {
        byte[] plain = Encoding.UTF8.GetBytes(secret);
        try { return Convert.ToBase64String(Run(plain, protect: true)); }
        finally { Array.Clear(plain); }
    }

    /// <summary>The secret back, or null when it cannot be read (another user or PC, damaged value).</summary>
    public static string? Unprotect(string? base64)
    {
        if (string.IsNullOrEmpty(base64)) return null;
        try
        {
            byte[] plain = Run(Convert.FromBase64String(base64), protect: false);
            try { return Encoding.UTF8.GetString(plain); }
            finally { Array.Clear(plain); }
        }
        catch (Exception ex) when (ex is FormatException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static byte[] Run(byte[] input, bool protect)
    {
        var inHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        var dataIn = new DataBlob { Size = input.Length, Data = inHandle.AddrOfPinnedObject() };
        var entropy = new DataBlob { Size = Entropy.Length, Data = entHandle.AddrOfPinnedObject() };
        var dataOut = new DataBlob();
        try
        {
            bool ok = protect
                ? CryptProtectData(ref dataIn, "Graphite", ref entropy, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref dataOut)
                : CryptUnprotectData(ref dataIn, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref dataOut);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[dataOut.Size];
            Marshal.Copy(dataOut.Data, result, 0, dataOut.Size);
            return result;
        }
        finally
        {
            if (dataOut.Data != IntPtr.Zero)
            {
                // wipe the unmanaged copy before handing it back to Windows
                for (int i = 0; i < dataOut.Size; i++) Marshal.WriteByte(dataOut.Data, i, 0);
                LocalFree(dataOut.Data);
            }
            inHandle.Free();
            entHandle.Free();
        }
    }
}
