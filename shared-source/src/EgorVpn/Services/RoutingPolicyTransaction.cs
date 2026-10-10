using EgorVpn.Models;
namespace EgorVpn.Services;

// Caller holds the existing settings/connection mutation gate. Only the atomic
// settings writer persists; callbacks activate and verify the captured server.
public static class RoutingPolicyTransaction
{
    public static async Task ApplyAsync(AppSettings previous, AppSettings candidate,
        Func<AppSettings,CancellationToken,Task> validate,
        Func<AppSettings,CancellationToken,Task> activateAndCheck,
        Func<AppSettings,CancellationToken,Task> persist,
        Func<CancellationToken,Task> restoreAndCheck,
        CancellationToken ct=default)
    {
        var frozen=candidate.Copy();
        await validate(frozen,ct);
        ct.ThrowIfCancellationRequested();
        try {
            await activateAndCheck(frozen,ct);
            ct.ThrowIfCancellationRequested();
            await persist(frozen,ct);
            // Do not throw for cancellation after the atomic save committed.
        }
        catch(Exception original) {
            using var rollback=new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(previous.ConnectTimeoutSeconds+15,30,150)));
            try { await restoreAndCheck(rollback.Token); }
            catch(Exception failure) { throw new RoutingPolicyRollbackException(original,failure); }
            throw;
        }
    }
}
public sealed class RoutingPolicyRollbackException(Exception original,Exception rollback)
    : AggregateException("Не удалось применить режим и восстановить прежнее подключение.",original,rollback);
