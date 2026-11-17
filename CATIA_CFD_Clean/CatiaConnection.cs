using System.Runtime.InteropServices;

namespace CATIA_CFD_Clean;

// Attaches to the already-running CATIA instance through COM.
internal static class CatiaConnection
{
    // Cached application proxy. Looking CATIA up in the running object table is comparatively slow.
    private static object? _catia;

    // Returns the active document of the running CATIA instance.
    public static object GetActiveDocument()
    {
        dynamic catia = GetApplication();
        if (catia.Documents.Count == 0)
            throw new InvalidOperationException("No document is open in CATIA.");

        return catia.ActiveDocument;
    }

    // Returns the cached application if it is still alive, otherwise re-attaches.
    // A cached proxy goes stale when CATIA is restarted.
    private static object GetApplication()
    {
        if (_catia is not null)
        {
            try
            {
                dynamic cached = _catia;
                _ = cached.Name; // cheap liveness probe
                return _catia;
            }
            catch (COMException)
            {
                _catia = null;
            }
            catch (InvalidComObjectException)
            {
                _catia = null;
            }
        }

        _catia = GetRunningCatia("CATIA.Application");
        return _catia;
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
