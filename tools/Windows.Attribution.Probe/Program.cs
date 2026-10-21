if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("These local diagnostic probes require native Windows.");
    return 2;
}
if (args is ["--allocation-target"])
    return WindowsAllocationProbe.RunTarget();
if (args is ["--eventpipe-allocation-probe", var output])
    return await WindowsAllocationProbe.Run(output);
if (args is ["--legacy-pipe-probe"])
    return WindowsPipeProbe.ReproduceLegacy();
if (args is ["--pipe-only-probe"])
    return await WindowsPipeProbe.Run();
if (args is ["--pipe-client", var pipeName, var serverPid] && int.TryParse(serverPid, out var expectedServer))
    return await WindowsPipeProbe.Client(pipeName, expectedServer);
if (args is ["--inspect-etl", var tracePath, var targetPath, var inspectionPath])
    return WindowsTraceInspection.Run(tracePath, targetPath, inspectionPath);
Console.Error.WriteLine("Use --inspect-etl <saved-legacy-etl> <saved-target-status> <new-json-output>, --pipe-only-probe, or --eventpipe-allocation-probe <new-output-directory>. Windows CPU collection is performed only by stock PerfView through the lab's consent flow.");
return 2;
