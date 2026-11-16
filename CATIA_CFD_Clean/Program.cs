using System.Runtime.InteropServices;

// Attach to the already-running CATIA instance via its COM ProgID
// and print the name of the active CATPart document.
try
{
    object catiaObj = GetRunningCatia("CATIA.Application");
    dynamic catia = catiaObj;

    int count = catia.Documents.Count;
    Console.WriteLine($"Connected to CATIA. Open documents: {count}");

    if (count == 0)
    {
        Console.WriteLine("No document is open in CATIA. Open a CATPart and try again.");
        return;
    }

    dynamic doc = catia.ActiveDocument;
    Console.WriteLine($"Active document: {doc.Name}");
    Console.WriteLine($"Full name:       {doc.FullName}");
}
catch (COMException ex) when ((uint)ex.ErrorCode == 0x800401E3) // MK_E_UNAVAILABLE
{
    Console.WriteLine("No running CATIA instance found. Start CATIA and try again.");
}
catch (COMException ex)
{
    Console.WriteLine($"CATIA COM error: {ex.Message}");
}

// Replacement for Marshal.GetActiveObject, which is not available in .NET Core+.
static object GetRunningCatia(string progId)
{
    int hr = CLSIDFromProgID(progId, out Guid clsid);
    Marshal.ThrowExceptionForHR(hr);

    hr = GetActiveObject(ref clsid, IntPtr.Zero, out object obj);
    Marshal.ThrowExceptionForHR(hr);
    return obj;
}

[DllImport("ole32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
static extern int CLSIDFromProgID([MarshalAs(UnmanagedType.LPWStr)] string progId, out Guid clsid);

[DllImport("oleaut32.dll", PreserveSig = true)]
static extern int GetActiveObject(ref Guid rclsid, IntPtr pvReserved, [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);
