namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;

public enum ExpensiveOperationKind
{
    AutoGenStatus,
    AutoGenPlanHandoff,
    AutoGenPlanRead,
    AutoGenPlanAction,
    WeekValidation,
    WeekMutation,
    ModuleSequenceSave,
    TimeSlotEditorMutation
}

// Не дозволяє дорогим запитам накопичувати необмежену чергу всередині процесу.
public sealed class ExpensiveOperationGate : IDisposable
{
    private readonly SemaphoreSlim _autoGenStatus = new(16, 16);
    private readonly SemaphoreSlim _autoGenPlanHandoff = new(8, 8);
    private readonly SemaphoreSlim _autoGenPlanRead = new(2, 2);
    private readonly SemaphoreSlim _autoGenPlanAction = new(1, 1);
    private readonly SemaphoreSlim _weekValidation = new(2, 2);
    private readonly SemaphoreSlim _weekMutation = new(1, 1);
    private readonly SemaphoreSlim _moduleSequenceSave = new(2, 2);
    private readonly SemaphoreSlim _timeSlotEditorMutation = new(1, 1);

    public async ValueTask<IDisposable?> TryEnterAsync(
        ExpensiveOperationKind kind,
        CancellationToken cancellationToken)
    {
        var gate = Resolve(kind);
        return await gate.WaitAsync(TimeSpan.Zero, cancellationToken)
            ? new Lease(gate)
            : null;
    }

    public void Dispose()
    {
        _autoGenStatus.Dispose();
        _autoGenPlanHandoff.Dispose();
        _autoGenPlanRead.Dispose();
        _autoGenPlanAction.Dispose();
        _weekValidation.Dispose();
        _weekMutation.Dispose();
        _moduleSequenceSave.Dispose();
        _timeSlotEditorMutation.Dispose();
    }

    private SemaphoreSlim Resolve(ExpensiveOperationKind kind)
        => kind switch
        {
            ExpensiveOperationKind.AutoGenStatus => _autoGenStatus,
            ExpensiveOperationKind.AutoGenPlanHandoff => _autoGenPlanHandoff,
            ExpensiveOperationKind.AutoGenPlanRead => _autoGenPlanRead,
            ExpensiveOperationKind.AutoGenPlanAction => _autoGenPlanAction,
            ExpensiveOperationKind.WeekValidation => _weekValidation,
            ExpensiveOperationKind.WeekMutation => _weekMutation,
            ExpensiveOperationKind.ModuleSequenceSave => _moduleSequenceSave,
            ExpensiveOperationKind.TimeSlotEditorMutation => _timeSlotEditorMutation,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose()
            => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
