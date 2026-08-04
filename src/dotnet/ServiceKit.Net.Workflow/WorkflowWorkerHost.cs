using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Temporalio.Client;
using Temporalio.Worker;

namespace ServiceKit.Net
{
    // Runs one Temporal worker per registered task queue, all inside this single process, in parallel,
    // and tears them down together.
    //
    // The number of queues and the number of processes are independent axes - a worker is not tied to
    // one queue. That is what makes the per workflow queue default affordable: N queues here cost N
    // long poll connections, not N deployments.
    public sealed class WorkflowWorkerHost : BackgroundService
    {
        private readonly WorkflowRegistry _registry;
        private readonly WorkflowWorkerOptions _options;
        private readonly IServiceProvider _services;
        private readonly ILogger<WorkflowWorkerHost> _logger;

        public WorkflowWorkerHost(WorkflowRegistry registry, WorkflowWorkerOptions options, IServiceProvider services, ILogger<WorkflowWorkerHost> logger)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _services = services ?? throw new ArgumentNullException(nameof(services));
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (_registry.Queues.Count == 0)
            {
                _logger?.LogInformation("no workflow task queue is registered, the workflow worker host stays idle");
                return;
            }

            var client = await _ConnectAsync();
            var workers = new List<TemporalWorker>();

            try
            {
                foreach (var queue in _registry.Queues)
                    workers.Add(_CreateWorker(client, queue));

                _logger?.LogInformation("workflow worker host is starting on {QueueCount} task queue(s): {TaskQueues}",
                    workers.Count,
                    string.Join(", ", _registry.Queues.Select(queue => queue.TaskQueue)));

                await Task.WhenAll(workers.Select(worker => worker.ExecuteAsync(stoppingToken)));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested == true)
            {
                // Ordinary shutdown, not a failure
            }
            finally
            {
                foreach (var worker in workers)
                    worker.Dispose();
            }
        }

        private async Task<IWorkerClient> _ConnectAsync()
        {
            // A client registered in the container wins - it lets the host be pointed at a test server,
            // and lets an application share one connection
            var registered = _services.GetService<ITemporalClient>();
            if (registered != null)
                return registered;

            return await TemporalClient.ConnectAsync(new TemporalClientConnectOptions(_options.TargetHost)
            {
                Namespace = _options.Namespace,
                LoggerFactory = _services.GetService<ILoggerFactory>(),
            });
        }

        private TemporalWorker _CreateWorker(IWorkerClient client, WorkflowQueueRegistration queue)
        {
            var workerOptions = new TemporalWorkerOptions(queue.TaskQueue)
            {
                GracefulShutdownTimeout = _options.GracefulShutdownTimeout,
                WorkflowFailureExceptionTypes = _options.WorkflowFailureExceptionTypes.ToList(),
            };

            foreach (var workflowType in queue.WorkflowTypes)
                workerOptions.AddWorkflow(workflowType);

            foreach (var activityServiceType in queue.ActivityServiceTypes)
            {
                // Resolved from the root provider, so activity implementations must be singletons.
                // One that needs per call state should take an IServiceScopeFactory and open its own
                // scope - a worker outlives any request scope.
                var implementation = _services.GetService(activityServiceType);
                if (implementation == null)
                    throw new InvalidOperationException($"task queue '{queue.TaskQueue}' needs the activity implementation of '{activityServiceType.FullName}', but it is not registered in the service container");

                workerOptions.AddAllActivities(activityServiceType, implementation);
            }

            _options.ConfigureWorker?.Invoke(workerOptions);

            return new TemporalWorker(client, workerOptions);
        }
    }
}
