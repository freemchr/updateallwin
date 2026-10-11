using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace UpdateAllWin.Core.Infrastructure;

/// <summary>Verifies a file is validly Authenticode-signed (WinVerifyTrust) by an expected publisher.</summary>
public static class Authenticode
{
    public static bool IsSignedBy(string path, string publisherFragment, out string detail)
    {
        if (!VerifyTrust(path, out var code))
        {
            detail = $"signature is not trusted (0x{code:X8})";
            return false;
        }

        try
        {
#pragma warning disable SYSLIB0057 // still the simplest way to read the Authenticode signer certificate
            using var signer = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            using var cert = X509CertificateLoader.LoadCertificate(signer.GetRawCertData());
            if (cert.Subject.Contains(publisherFragment, StringComparison.OrdinalIgnoreCase))
            {
                detail = cert.Subject;
                return true;
            }
            detail = $"signed by '{cert.Subject}', expected '{publisherFragment}'";
        }
        catch (Exception ex)
        {
            detail = $"could not read signer: {ex.Message}";
        }
        return false;
    }

    private static bool VerifyTrust(string path, out int code)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = path,
        };
        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,               // WTD_UI_NONE
                fdwRevocationChecks = 0,      // WTD_REVOKE_NONE
                dwUnionChoice = 1,            // WTD_CHOICE_FILE
                pFile = pFile,
                dwStateAction = 0,            // WTD_STATEACTION_IGNORE
                dwProvFlags = 0x00000080,     // WTD_REVOCATION_CHECK_NONE
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            code = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            return code == 0;
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
            Marshal.FreeHGlobal(pFile);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
