using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Openza.Tasks.Core.Credentials;

namespace Openza.Tasks.Desktop.Services;

public sealed class WindowsCredentialStore(string credentialNamespace) : ICredentialStore
{
    private const uint GenericCredential = 1;
    private const uint PersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private readonly string _credentialNamespace = string.IsNullOrWhiteSpace(credentialNamespace)
        ? throw new ArgumentException("A credential namespace is required.", nameof(credentialNamespace))
        : credentialNamespace;

    public Task SaveAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        EnsureWindows();

        var target = TargetName(key);
        var targetPointer = Marshal.StringToCoTaskMemUni(target);
        var userPointer = Marshal.StringToCoTaskMemUni(key);
        var blob = Encoding.UTF8.GetBytes(value);
        var blobPointer = Marshal.AllocCoTaskMem(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobPointer, blob.Length);
            var credential = new NativeCredential
            {
                Type = GenericCredential,
                TargetName = targetPointer,
                CredentialBlobSize = checked((uint)blob.Length),
                CredentialBlob = blobPointer,
                Persist = PersistLocalMachine,
                UserName = userPointer,
            };

            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows Credential Manager could not save the credential.");
            }
        }
        finally
        {
            if (blob.Length > 0)
            {
                Marshal.Copy(new byte[blob.Length], 0, blobPointer, blob.Length);
            }
            Marshal.FreeCoTaskMem(blobPointer);
            Marshal.FreeCoTaskMem(userPointer);
            Marshal.FreeCoTaskMem(targetPointer);
        }

        return Task.CompletedTask;
    }

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        EnsureWindows();

        if (!CredRead(TargetName(key), GenericCredential, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return Task.FromResult<string?>(null);
            }

            throw new Win32Exception(error, "Windows Credential Manager could not read the credential.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return Task.FromResult<string?>(null);
            }

            var blob = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);
            return Task.FromResult<string?>(Encoding.UTF8.GetString(blob));
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        EnsureWindows();

        if (!CredDelete(TargetName(key), GenericCredential, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
            {
                throw new Win32Exception(error, "Windows Credential Manager could not remove the credential.");
            }
        }

        return Task.CompletedTask;
    }

    private string TargetName(string key) => $"Openza.Tasks.Desktop/{_credentialNamespace}/{key}";

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Credential Manager is available only on Windows.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
