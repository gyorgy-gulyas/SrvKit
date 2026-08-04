using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ServiceKit.Net
{
    public static class WorkflowServiceCollectionExtension
    {
        // Wire up the workflow worker host. The register callback is where the generated per workflow
        // registrations go:
        //
        //     services.UseWorkflows( registry => {
        //         FulfilOrderRegistration.Register( registry );
        //         CancelOrderRegistration.Register( registry );
        //     } );
        //
        // The activity implementations themselves are registered separately, as singletons, against
        // their generated interface.
        public static IServiceCollection UseWorkflows(this IServiceCollection services, Action<WorkflowRegistry> register, Action<WorkflowWorkerOptions> configure = null)
        {
            if (register == null)
                throw new ArgumentNullException(nameof(register));

            var registry = new WorkflowRegistry();
            register(registry);

            var options = new WorkflowWorkerOptions();
            configure?.Invoke(options);

            services.AddSingleton(registry);
            services.AddSingleton(options);
            services.AddHostedService<WorkflowWorkerHost>();

            return services;
        }
    }
}
