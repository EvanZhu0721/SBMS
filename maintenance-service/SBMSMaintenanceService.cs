using System;
using System.ServiceProcess;
using System.Threading;

namespace SBMSSetup
{
    internal sealed class SBMSMaintenanceWindowsService
        : ServiceBase
    {
        private static readonly TimeSpan LifecycleBudget =
            TimeSpan.FromSeconds(20);
        private readonly MaintenanceLifecycle lifecycle =
            new MaintenanceLifecycle();

        internal SBMSMaintenanceWindowsService()
        {
            ServiceName = MaintenanceServiceIdentity.ServiceName;
            CanStop = true;
            CanShutdown = true;
            AutoLog = true;
        }

        protected override void OnStart(string[] args)
        {
            RequestAdditionalTime(
                (int)LifecycleBudget.TotalMilliseconds);
            lifecycle.Start(
                LifecycleBudget,
                StartRuntime);
        }

        protected override void OnStop()
        {
            StopRuntime();
        }

        protected override void OnShutdown()
        {
            StopRuntime();
            base.OnShutdown();
        }

        private void StopRuntime()
        {
            RequestAdditionalTime(
                (int)LifecycleBudget.TotalMilliseconds);
            lifecycle.Stop(
                LifecycleBudget,
                StopRuntimeCore);
        }

        private static void StartRuntime(
            CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            // Fail closed until pipe identity authorization and the protected
            // App-root materializer are composed with the production replay
            // store. An SCM start must never report a partial runtime.
            throw new InvalidOperationException(
                "Maintenance production dependencies are incomplete.");
        }

        private static void StopRuntimeCore(
            CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
        }
    }

    internal static class SBMSMaintenanceServiceProgram
    {
        private static int Main(string[] args)
        {
            if (args.Length != 0)
            {
                return 2;
            }
            ServiceBase.Run(
                new ServiceBase[]
                {
                    new SBMSMaintenanceWindowsService()
                });
            return 0;
        }
    }
}
