using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using HalconTraining.Application.Environment;

namespace HalconTraining.Persistence.Environment;

public sealed class NvidiaGpuTelemetry(string executable = "nvidia-smi", TimeSpan? timeout = null) : IGpuTelemetry
{
    public async Task<GpuTelemetryReport> CaptureAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(2));
        using var process=new Process {StartInfo=new ProcessStartInfo(executable)
        {
            UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,
            StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8
        }};
        process.StartInfo.ArgumentList.Add("--query-gpu=index,name,utilization.gpu,memory.used,memory.total,temperature.gpu,power.draw");
        process.StartInfo.ArgumentList.Add("--format=csv,noheader,nounits");
        var started=false;
        try
        {
            process.Start(); started=true;
            var output=process.StandardOutput.ReadToEndAsync(deadline.Token);
            var error=process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            var stdout=await output.ConfigureAwait(false);
            var stderr=await error.ConfigureAwait(false);
            return process.ExitCode==0 ? Parse(stdout) : Unavailable("GPU 查询失败："+stderr.Trim());
        }
        catch(OperationCanceledException) when(!cancellationToken.IsCancellationRequested)
        { return Unavailable("GPU 状态查询超时，将自动重试。"); }
        catch(Exception error) when(error is Win32Exception or IOException or InvalidOperationException)
        { return Unavailable("无法读取 NVIDIA GPU 状态："+error.Message); }
        finally
        {
            if(started)
            {
                try {if(!process.HasExited)process.Kill(entireProcessTree:true);}
                catch(Exception error) when(error is Win32Exception or InvalidOperationException) { }
            }
        }
    }

    public static GpuTelemetryReport Parse(string csv)
    {
        var devices=new List<GpuTelemetryDevice>();
        double? Number(string text,double min,double max=double.MaxValue) =>
            double.TryParse(text.Trim(),NumberStyles.Float,CultureInfo.InvariantCulture,out var value) &&
            double.IsFinite(value) && value>=min && value<=max ? value : null;
        foreach(var line in csv.Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries))
        {
            var fields=line.Split(',');
            if(fields.Length<7 || !int.TryParse(fields[0].Trim(),NumberStyles.Integer,CultureInfo.InvariantCulture,out var index) || index<0)
                return Unavailable("GPU 查询结果格式异常，将自动重试。");
            // The five metric columns are fixed at the end, even if a device name contains a comma.
            var name=string.Join(",",fields[1..^5]).Trim().Trim('"');
            if(string.IsNullOrWhiteSpace(name))return Unavailable("GPU 名称缺失，将自动重试。");
            devices.Add(new(index,name,Number(fields[^5],0,100),Number(fields[^4],0),Number(fields[^3],double.Epsilon),
                Number(fields[^2],0,150),Number(fields[^1],0)));
        }
        return devices.Count==0 ? Unavailable("未检测到 NVIDIA GPU。") : new(DateTimeOffset.Now,devices.OrderBy(d=>d.Index).ToArray());
    }

    private static GpuTelemetryReport Unavailable(string error) => new(DateTimeOffset.Now,[],error);
}
