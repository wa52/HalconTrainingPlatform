namespace HalconTraining.Application.Workers;

public sealed record BoundedJobResult<T>(T Item, bool Cancelled, Exception? Error);

/// <summary>Independent jobs with bounded concurrency; one failure never cancels sibling jobs.</summary>
public static class BoundedJobRunner
{
    public static async Task<IReadOnlyList<BoundedJobResult<T>>> RunAsync<T>(IReadOnlyList<T> items, int concurrency,
        Func<T, CancellationToken, Task> execute, CancellationToken cancellationToken)
    {
        if (concurrency < 1) throw new ArgumentOutOfRangeException(nameof(concurrency));
        using var slots = new SemaphoreSlim(concurrency);
        return await Task.WhenAll(items.Select(async item =>
        {
            var entered = false;
            try
            {
                await slots.WaitAsync(cancellationToken); entered = true;
                cancellationToken.ThrowIfCancellationRequested();
                await execute(item, cancellationToken);
                return new BoundedJobResult<T>(item, false, null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { return new BoundedJobResult<T>(item, true, null); }
            catch (Exception error) { return new BoundedJobResult<T>(item, false, error); }
            finally { if (entered) slots.Release(); }
        }));
    }
}
