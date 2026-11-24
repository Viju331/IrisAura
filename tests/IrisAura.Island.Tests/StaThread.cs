using System.Runtime.ExceptionServices;

namespace IrisAura.Island.Tests;

/// <summary>Runs WPF code on a dedicated single-threaded-apartment thread, as WPF requires.</summary>
internal static class StaThread
{
    public static void Run(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
