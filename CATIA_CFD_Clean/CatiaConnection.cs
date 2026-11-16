using System.Runtime.InteropServices;

namespace CATIA_CFD_Clean;

// Attaches to the already-running CATIA instance through COM.
internal static class CatiaConnection
{
    // Returns the active document of the running CATIA instance.
    public static object GetActiveDocument()
    {
        dynamic catia = GetRunningCatia("CATIA.Application");
        if (catia.Documents.Count == 0)
            throw new InvalidOperationException("No document is open in CATIA.");

        return catia.ActiveDocument;
    }

    // Replacement for Marshal.GetActiveObject, which is not available in .NET 5+.
    private static object GetRunningCatia(string progId)
    {
        int hr = CLSIDFromProgID(progId, out Guid clsid);
        Marshal.ThrowExceptionForHR(hr);

        hr = GetActiveObject(ref clsid, IntPtr.Zero, out object obj);
        Marshal.ThrowExceptionForHR(hr);
        return obj;
    }

    [DllImport("ole32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int CLSIDFromProgID([MarshalAs(UnmanagedType.LPWStr)] string progId, out Guid clsid);

    [DllImport("oleaut32.dll", PreserveSig = true)]
    private static extern int GetActiveObject(ref Guid rclsid, IntPtr pvReserved,
        [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);
}
