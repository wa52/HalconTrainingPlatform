using System.Diagnostics;
using System.Globalization;
using HalconTraining.Application.Environment;
using HalconTraining.App.ViewModels;
using HalconTraining.Persistence.Environment;

if(args.Any(a=>a.StartsWith("--query-gpu=")))
{
    var fixture=Environment.GetEnvironmentVariable("HALCON_GPU_SMOKE_FIXTURE");
    if(fixture=="hang")
    {
        File.WriteAllText(Environment.GetEnvironmentVariable("HALCON_GPU_SMOKE_PID")!,Environment.ProcessId.ToString());
        await Task.Delay(TimeSpan.FromSeconds(30));
    }
    if(fixture=="error"){Console.Error.WriteLine("driver unavailable");return 3;}
    Console.WriteLine("0, NVIDIA Test, 45, 2048, 8192, 61, 35.5");return 0;
}

void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
CultureInfo.CurrentCulture=new("fr-FR");
var parsed=NvidiaGpuTelemetry.Parse("0, NVIDIA Test, 45, 2048, 8192, 61, 35.5\n1, NVIDIA Second, Edition, N/A, [Not Supported], 8192, N/A, N/A");
Require(parsed.Devices.Count==2&&parsed.Devices[0].PowerWatts==35.5,"CSV解析不能依赖本机小数点格式。");
Require(parsed.Devices[1].Name=="NVIDIA Second, Edition"&&parsed.Devices[1].UtilizationPercent is null&&parsed.Devices[1].UsedMemoryMiB is null,"未知值不得伪装为零。");
Require(NvidiaGpuTelemetry.Parse("").Error is not null&&NvidiaGpuTelemetry.Parse("broken").Error is not null,"缺卡或异常输出必须提示不可用。");
var missing=await new NvidiaGpuTelemetry("missing-nvidia-smi-"+Guid.NewGuid()).CaptureAsync();
Require(missing.Error is not null&&missing.Devices.Count==0,"未安装工具必须降级而不是崩溃。");

var root=Path.Combine(Path.GetTempPath(),"halcon-gpu-smoke-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
try
{
    var self=Environment.ProcessPath!;
    Environment.SetEnvironmentVariable("HALCON_GPU_SMOKE_FIXTURE","normal");
    Require((await new NvidiaGpuTelemetry(self).CaptureAsync()).Devices.Count==1,"实际子进程输出未被解析。");
    Environment.SetEnvironmentVariable("HALCON_GPU_SMOKE_FIXTURE","error");
    Require((await new NvidiaGpuTelemetry(self).CaptureAsync()).Error!.Contains("driver unavailable"),"查询错误未保留原因。");
    Environment.SetEnvironmentVariable("HALCON_GPU_SMOKE_FIXTURE","hang");
    foreach(var cancel in new[]{false,true})
    {
        var pidFile=Path.Combine(root,cancel?"cancel.pid":"timeout.pid");
        Environment.SetEnvironmentVariable("HALCON_GPU_SMOKE_PID",pidFile);
        using var cts=new CancellationTokenSource();
        if(cancel)cts.CancelAfter(600);
        var cancelled=false;var clock=Stopwatch.StartNew();
        try
        {
            var result=await new NvidiaGpuTelemetry(self,TimeSpan.FromSeconds(1)).CaptureAsync(cts.Token);
            Require(!cancel&&result.Error!.Contains("超时"),"超时未返回明确状态。");
        }
        catch(OperationCanceledException) when(cancel){cancelled=true;}
        Require(!cancel||cancelled,"调用方取消未传播。");
        Require(clock.Elapsed<TimeSpan.FromSeconds(4),"采样阻塞过久。");
        Require(File.Exists(pidFile),"子进程未实际进入挂起场景。");
        var pid=int.Parse(File.ReadAllText(pidFile));
        var exited=false;
        for(var i=0;i<100&&!exited;i++)
        {
            try{using var process=Process.GetProcessById(pid);exited=process.HasExited;}
            catch(ArgumentException){exited=true;}
            if(!exited)await Task.Delay(20);
        }
        Require(exited,"超时或取消后遗留了查询子进程。");
    }
}
finally
{
    Environment.SetEnvironmentVariable("HALCON_GPU_SMOKE_FIXTURE",null);Environment.SetEnvironmentVariable("HALCON_GPU_SMOKE_PID",null);
    var target=Path.GetFullPath(root);var temp=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
    if(Path.GetDirectoryName(target)!=temp||!Path.GetFileName(target).StartsWith("halcon-gpu-smoke-",StringComparison.Ordinal))throw new InvalidOperationException("临时目录超出预期范围。");
    Directory.Delete(target,true);
}

var fake=new SequenceTelemetry();
using(var vm=new GpuStatusViewModel(fake))
{
    var updates=0;var second=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    vm.PropertyChanged+=(_,e)=>{if(e.PropertyName==nameof(vm.SampleCount)&&++updates==2)second.TrySetResult();};
    vm.Start();vm.Start();await second.Task.WaitAsync(TimeSpan.FromSeconds(6));
    Require(vm.SampleCount==2&&fake.Peak==1,"轮询发生重复启动或重叠。");
    Require(vm.Summary.Contains("不可用")&&vm.Memory=="显存 —"&&!vm.IsAvailable,"查询失败仍显示过期显存或伪造零负载。");
    vm.Dispose();await Task.Delay(100);Require(fake.Calls==2,"关闭后仍继续轮询。");
}
Console.WriteLine("PASS: GPU CSV/未知值/多卡/地区格式/缺失工具/真实进程/超时清理/取消/无重叠轮询/失效状态/关闭停止。");
return 0;

sealed class SequenceTelemetry : IGpuTelemetry
{
    public int Calls,Peak;private int _active;
    public async Task<GpuTelemetryReport> CaptureAsync(CancellationToken token=default)
    {
        Calls++;Peak=Math.Max(Peak,Interlocked.Increment(ref _active));
        try
        {
            await Task.Delay(50,token);
            return Calls==1?NvidiaGpuTelemetry.Parse("0, NVIDIA Test, 45, 2048, 8192, 61, 35.5"):new(DateTimeOffset.Now,[],"测试查询失败");
        }
        finally{Interlocked.Decrement(ref _active);}
    }
}
