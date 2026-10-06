using System.ComponentModel;

namespace QualificationOutcomeWireSource;

internal static class ChildStartObservation
{
    internal static DateTime? Capture(Func<DateTime> readStart, Func<bool> hasExited)
    {
        try
        {
            return readStart();
        }
        catch (Win32Exception) when (hasExited())
        {
            return null;
        }
        catch (InvalidOperationException) when (hasExited())
        {
            return null;
        }
    }
}
