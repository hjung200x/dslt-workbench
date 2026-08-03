namespace Dslt.Managed.Core.Services;

public static class ProcessingEngineFactory
{
    public static IProcessingEngine Create()
    {
        try
        {
            return new NativeProcessingEngine();
        }
        catch (Exception error) when (
            error is DllNotFoundException or
            BadImageFormatException or
            EntryPointNotFoundException or
            NotSupportedException)
        {
            return new UnavailableProcessingEngine(
                $"Native core unavailable: {error.Message}. Build modern/native and place dslt_core.dll beside the app.");
        }
    }
}

