using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Plinth.App.Security;

public interface ICredentialStore
{
    Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default);
    Task WriteAsync(string key, string secret, CancellationToken cancellationToken = default);
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}

public sealed class CredentialStoreException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>No file fallback. Linux requires secret-tool and an unlocked Secret Service session.</summary>
public sealed class OsCredentialStore : ICredentialStore
{
    public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return OperatingSystem.IsWindows()
            ? Task.FromResult(ReadWindows(key))
            : RunSecretToolAsync("lookup", key, null, cancellationToken);
    }

    public async Task WriteAsync(string key, string secret, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows())
            WriteWindows(key, secret);
        else
            await RunSecretToolAsync("store", key, secret, cancellationToken);
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows())
        {
            if (!CredDelete(key, 1, 0) && Marshal.GetLastWin32Error() != 1168)
                throw new CredentialStoreException("credential.delete_failed");
        }
        else
            await RunSecretToolAsync("clear", key, null, cancellationToken);
    }

    private static async Task<string?> RunSecretToolAsync(string operation, string key, string? secret, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux())
            throw new CredentialStoreException("credential.platform_unsupported");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("secret-tool")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add(operation);
        if (operation == "store")
            process.StartInfo.ArgumentList.Add("--label=Plinth OIDC refresh token");
        process.StartInfo.ArgumentList.Add("application");
        process.StartInfo.ArgumentList.Add("Plinth");
        process.StartInfo.ArgumentList.Add("account");
        process.StartInfo.ArgumentList.Add(key);
        try
        {
            process.Start();
            // stderr is never logged; it may contain implementation-specific sensitive data.
            var output = ReadBoundedAsync(process.StandardOutput, timeout.Token);
            var errors = ReadBoundedAsync(process.StandardError, timeout.Token);
            if (secret is not null)
            {
                if (secret.Length > 32768 || secret.Contains('\n') || secret.Contains('\r'))
                    throw new CredentialStoreException("credential.secret_invalid");
                await process.StandardInput.WriteAsync(secret.AsMemory(), timeout.Token);
            }
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            var result = await output;
            var error = await errors;
            if (process.ExitCode == 1 && operation is ("lookup" or "clear") && error.Length == 0)
                return null; // Secret Service reports no matching item.
            if (process.ExitCode != 0)
                throw new CredentialStoreException("credential.unavailable");
            return operation == "lookup" ? result.TrimEnd('\r', '\n') : null;
        }
        catch (Win32Exception)
        {
            throw new CredentialStoreException("credential.unavailable");
        }
        catch (Exception ex) when (ex is System.IO.IOException or InvalidOperationException)
        {
            throw new CredentialStoreException("credential.unavailable");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new CredentialStoreException("credential.timeout");
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
        }
    }

    private static async Task<string> ReadBoundedAsync(System.IO.StreamReader reader, CancellationToken token)
    {
        var buffer = new char[1024];
        var result = new StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
        {
            if (result.Length + count > 32768)
                throw new CredentialStoreException("credential.response_too_large");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }

    private static string? ReadWindows(string key)
    {
        if (!CredRead(key, 1, 0, out var pointer))
        {
            if (Marshal.GetLastWin32Error() == 1168) return null;
            throw new CredentialStoreException("credential.unavailable");
        }
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlobSize > 2560)
                throw new CredentialStoreException("credential.secret_too_large");
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            try { return Encoding.UTF8.GetString(bytes); }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { CredFree(pointer); }
    }

    private static void WriteWindows(string key, string secret)
    {
        var bytes = Encoding.UTF8.GetBytes(secret);
        try
        {
            if (bytes.Length > 2560)
                throw new CredentialStoreException("credential.secret_too_large");
            var pointer = Marshal.AllocCoTaskMem(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, pointer, bytes.Length);
                var credential = new NativeCredential
                {
                    Type = 1,
                    TargetName = key,
                    CredentialBlobSize = (uint)bytes.Length,
                    CredentialBlob = pointer,
                    Persist = 2,
                    UserName = "Plinth",
                };
                if (!CredWrite(ref credential, 0))
                    throw new CredentialStoreException("credential.write_failed");
            }
            finally
            {
                for (var i = 0; i < bytes.Length; i++) Marshal.WriteByte(pointer, i, 0);
                Marshal.FreeCoTaskMem(pointer);
            }
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credential);
}
