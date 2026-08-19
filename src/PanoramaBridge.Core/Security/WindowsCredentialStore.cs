using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PanoramaBridge.Core.Security;

/// <summary>
/// Credentials held in Windows Credential Manager.
/// </summary>
/// <remarks>
/// <para>
/// The operating system encrypts the blob with the user's own key, so another account on the
/// same machine cannot read it, and it survives the user changing their Windows password. It is
/// also visible and removable through Control Panel, which matters: a scientist should be able
/// to see and revoke what an application stored about them without needing the application.
/// </para>
/// <para>
/// Called through P/Invoke rather than a NuGet wrapper. The surface needed here is three
/// functions, and depending on a package for that would be more code to audit, not less.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialStore : ICredentialStore
{
    /// <summary>Prefix for the Credential Manager target name, so entries are identifiable.</summary>
    public const string TargetPrefix = "PanoramaBridge";

    private readonly ILogger<WindowsCredentialStore> _log;

    public WindowsCredentialStore(ILogger<WindowsCredentialStore>? log = null) =>
        _log = log ?? NullLogger<WindowsCredentialStore>.Instance;

    /// <inheritdoc />
    public bool IsAvailable => OperatingSystem.IsWindows();

    /// <summary>
    /// The Credential Manager entry name for a server.
    /// </summary>
    /// <remarks>
    /// Keyed on scheme and host only. Including the path would create a separate entry for every
    /// destination folder on the same server, which is not how anyone thinks about a login.
    /// </remarks>
    public static string TargetFor(string serverUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverUrl);

        return Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri)
            ? $"{TargetPrefix}:{uri.Scheme}://{uri.Host}"
            : $"{TargetPrefix}:{serverUrl.Trim().TrimEnd('/')}";
    }

    /// <inheritdoc />
    public StoredCredential? Read(string serverUrl)
    {
        var target = TargetFor(serverUrl);

        if (!CredReadW(target, CredentialType.Generic, 0, out var handle))
        {
            var error = Marshal.GetLastWin32Error();

            if (error != ErrorNotFound)
            {
                _log.LogWarning("Reading the stored credential failed with Win32 error {Error}.", error);
            }

            return null;
        }

        try
        {
            var native = Marshal.PtrToStructure<NativeCredential>(handle);

            var secret = native.CredentialBlobSize > 0 && native.CredentialBlob != IntPtr.Zero
                ? Marshal.PtrToStringUni(native.CredentialBlob, (int)(native.CredentialBlobSize / 2))
                : string.Empty;

            // UserName is declared as a marshalled string in the struct, so it arrives already
            // converted -- unlike CredentialBlob, which is a raw pointer with an explicit length
            // because the secret is not null-terminated.
            return new StoredCredential(native.UserName ?? string.Empty, secret ?? string.Empty);
        }
        finally
        {
            CredFree(handle);
        }
    }

    /// <inheritdoc />
    public void Write(string serverUrl, StoredCredential credential)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credential.UserName);
        ArgumentNullException.ThrowIfNull(credential.Secret);

        var target = TargetFor(serverUrl);
        var blob = Encoding.Unicode.GetBytes(credential.Secret);

        // The documented ceiling is 512 bytes for the blob, i.e. 256 UTF-16 characters. An API
        // key is 64, so this only guards against something being passed that is not a secret.
        if (blob.Length > MaximumBlobBytes)
        {
            throw new ArgumentException(
                $"The secret is too long to store ({blob.Length} bytes; the limit is {MaximumBlobBytes}).",
                nameof(credential));
        }

        var blobHandle = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobHandle, blob.Length);

            var native = new NativeCredential
            {
                Type = CredentialType.Generic,
                TargetName = target,
                CredentialBlob = blobHandle,
                CredentialBlobSize = (uint)blob.Length,
                // LocalMachine, not Enterprise: this must never roam to another machine.
                Persist = CredentialPersistence.LocalMachine,
                UserName = credential.UserName,
                Comment = "Panorama credential stored by PanoramaBridge",
            };

            if (!CredWriteW(ref native, 0))
            {
                throw new InvalidOperationException(
                    $"Could not store the credential (Win32 error {Marshal.GetLastWin32Error()}).");
            }
        }
        finally
        {
            // Zero the copy before releasing it rather than leaving the secret in freed memory.
            for (var i = 0; i < blob.Length; i++)
            {
                Marshal.WriteByte(blobHandle, i, 0);
            }

            Marshal.FreeHGlobal(blobHandle);
            Array.Clear(blob);
        }
    }

    /// <inheritdoc />
    public void Delete(string serverUrl)
    {
        var target = TargetFor(serverUrl);

        if (CredDeleteW(target, CredentialType.Generic, 0))
        {
            return;
        }

        var error = Marshal.GetLastWin32Error();
        if (error != ErrorNotFound)
        {
            _log.LogWarning("Deleting the stored credential failed with Win32 error {Error}.", error);
        }
    }

    private const int ErrorNotFound = 1168;
    private const int MaximumBlobBytes = 512;

    private enum CredentialType : uint
    {
        Generic = 1,
    }

    private enum CredentialPersistence : uint
    {
        Session = 1,
        LocalMachine = 2,
        Enterprise = 3,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public CredentialType Type;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string TargetName;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string Comment;

        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public CredentialPersistence Persist;
        public uint AttributeCount;
        public IntPtr Attributes;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string TargetAlias;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string UserName;
    }

    // DllImport rather than the source-generated LibraryImport: the generator requires
    // AllowUnsafeBlocks for the whole project, and widening the compilation's safety posture to
    // save a few nanoseconds on three calls per session is the wrong trade -- least of all in
    // the file that handles credentials.
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredReadW(string target, CredentialType type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWriteW(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDeleteW(string target, CredentialType type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
