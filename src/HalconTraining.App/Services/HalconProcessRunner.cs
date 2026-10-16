using System.Diagnostics;
using System.IO;
using HalconTraining.Application.Workspace;

namespace HalconTraining.App.Services;

public sealed class HalconProcessRunner : IHalconProcessRunner
{
    public async Task<WorkerOutput> RunAsync(IReadOnlyList<string> arguments, IProgress<string>? output, CancellationToken cancellationToken)
    {
        var worker=Path.Combine(AppContext.BaseDirectory,"worker","HalconTraining.Worker.exe");
        if(!File.Exists(worker))throw new FileNotFoundException("找不到训练Worker，请重新生成程序。",worker);
        using var process=new Process { StartInfo = new ProcessStartInfo(worker) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=AppContext.BaseDirectory } };
        foreach(var argument in arguments)process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        using var registration=cancellationToken.Register(()=>{try{if(!process.HasExited)process.Kill(true);}catch(InvalidOperationException){} });
        var stderr=process.StandardError.ReadToEndAsync();
        var lines=new List<string>();
        while(await process.StandardOutput.ReadLineAsync() is { } line){lines.Add(line);output?.Report(line);}
        await process.WaitForExitAsync(CancellationToken.None);
        var error=await stderr;
        cancellationToken.ThrowIfCancellationRequested();
        return new(process.ExitCode,string.Join(Environment.NewLine,lines),error);
    }
}
