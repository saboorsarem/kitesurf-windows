// Copyright (c) 2026 Cloudflare, Inc.
// Licensed under the Apache 2.0 license found in the LICENSE file or at:
// https://opensource.org/licenses/Apache-2.0

using System.Runtime.InteropServices;
using System.Text;

namespace Kitesurf.Windows.Core;

/// <summary>
/// Protects the Cloudflare Access client secret with DPAPI (current user) before it reaches
/// settings.json. A profile copied to another user or machine simply yields no secret, so the
/// engine reports an authentication failure instead of the client sending a stale token.
/// </summary>
internal static class SecretProtector
{
    private const int CryptProtectUiForbidden = 0x1;

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Kitesurf.Windows.AccessToken.v1");

    public static string? Protect(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) return null;

        var plain = Encoding.UTF8.GetBytes(secret.Trim());
        try
        {
            return Convert.ToBase64String(Transform(plain, protect: true));
        }
        catch (Exception)
        {
            // If protection fails, storing the secret in the clear is the wrong trade.
            return null;
        }
    }

    public static string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue)) return null;

        try
        {
            var bytes = Transform(Convert.FromBase64String(protectedValue), protect: false);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inputBlob = new DataBlob();
        var entropyBlob = new DataBlob();
        var outputBlob = new DataBlob();

        var inputHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);

        try
        {
            inputBlob.Size = input.Length;
            inputBlob.Data = inputHandle.AddrOfPinnedObject();
            entropyBlob.Size = Entropy.Length;
            entropyBlob.Data = entropyHandle.AddrOfPinnedObject();

            var ok = protect
                ? CryptProtectData(ref inputBlob, "Kitesurf Access token", ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out outputBlob)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out outputBlob);

            if (!ok) throw new InvalidOperationException("DPAPI rejected the operation.");

            var result = new byte[outputBlob.Size];
            Marshal.Copy(outputBlob.Data, result, 0, outputBlob.Size);
            return result;
        }
        finally
        {
            if (outputBlob.Data != IntPtr.Zero) LocalFree(outputBlob.Data);
            inputHandle.Free();
            entropyHandle.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        ref DataBlob entropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        ref DataBlob entropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}
