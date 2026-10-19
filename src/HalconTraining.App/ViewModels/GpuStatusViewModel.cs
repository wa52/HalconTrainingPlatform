using System.Globalization;
using HalconTraining.Application.Environment;

namespace HalconTraining.App.ViewModels;

public sealed class GpuStatusViewModel(IGpuTelemetry telemetry) : ObservableModel, IDisposable
{
    private readonly CancellationTokenSource _lifetime=new();
    private Task? _polling;
    private GpuTelemetryReport? _report;
    private bool _disposed;
    public int SampleCount {get;private set;}
    public bool IsAvailable => _report?.Devices.Count>0;
    private GpuTelemetryDevice? Device => _report?.Devices.FirstOrDefault();
    public string Summary => Device is { } d ? $"GPU {d.Index} · {Value(d.UtilizationPercent,"0","%")}"+
        $" · {Value(d.TemperatureCelsius,"0"," °C")}" : _report is null ? "GPU · 正在读取" : "GPU · 状态不可用";
    public string Memory => Device is { } d ? $"显存 {Value(d.UsedMemoryMiB/1024,"0.0")} / {Value(d.TotalMemoryMiB/1024,"0.0")} GiB" : "显存 —";
    public string Details => _report is null ? "正在查询 NVIDIA GPU 状态。" : _report.Error ??
        string.Join(Environment.NewLine,_report.Devices.Select(d=>$"GPU {d.Index} · {d.Name}\n利用率 {Value(d.UtilizationPercent,"0","%")} · 温度 {Value(d.TemperatureCelsius,"0"," °C")} · 功耗 {Value(d.PowerWatts,"0.0"," W")}\n显存 {Value(d.UsedMemoryMiB,"0")} / {Value(d.TotalMemoryMiB,"0")} MiB"))+
        $"\n整卡占用，包含其他程序；每两秒自动刷新。\n采样时间：{_report.CapturedAt:HH:mm:ss}。不代表 HALCON 训练兼容性检查结果。";

    public void Start()
    {
        if(_disposed || _polling is not null)return;
        _polling=PollAsync();
    }

    private async Task PollAsync()
    {
        try
        {
            while(!_lifetime.IsCancellationRequested)
            {
                // Sequential awaits prevent overlapping samples; UI context is retained for property notifications.
                try { _report=await telemetry.CaptureAsync(_lifetime.Token); }
                catch(OperationCanceledException) when(_lifetime.IsCancellationRequested) { return; }
                catch(Exception error) { _report=new(DateTimeOffset.Now,[],"GPU 状态读取失败："+error.Message); }
                if(_lifetime.IsCancellationRequested)return;
                SampleCount++;
                foreach(var property in new[]{nameof(IsAvailable),nameof(Summary),nameof(Memory),nameof(Details),nameof(SampleCount)})OnPropertyChanged(property);
                await Task.Delay(TimeSpan.FromSeconds(2),_lifetime.Token);
            }
        }
        catch(OperationCanceledException) when(_lifetime.IsCancellationRequested) { }
    }

    private static string Value(double? number,string format,string suffix="") => number is { } n ? n.ToString(format,CultureInfo.InvariantCulture)+suffix : "—";
    public void Dispose()
    {
        if(_disposed)return;
        _disposed=true;_lifetime.Cancel();
        // Do not dispose the CTS until the pending native query and delay have observed cancellation.
        if(_polling is null)_lifetime.Dispose();
        else _=_polling.ContinueWith(_=>_lifetime.Dispose(),TaskScheduler.Default);
    }
}
