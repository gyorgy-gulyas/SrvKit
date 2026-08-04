using System.Reflection;
using Temporalio.Activities;
using Temporalio.Workflows;

namespace ServiceKit.Net
{
    // Collects the generated per-workflow registrations and MERGES them by task queue name.
    //
    // This merge is what makes the "one queue per workflow" decision cheap: two workflows pointed at
    // the same queue name end up in a single worker, so the coarser "one queue per context" layout is
    // just a runtime special case of the finer default - no code change, no migration.
    //
    // Note the trap that follows from command/query reuse: an activity interface shared by several
    // workflows must be registered on EVERY queue that serves one of them. Register it per workflow
    // and the merge takes care of the rest.
    public sealed class WorkflowRegistry
    {
        // Ordinal on purpose: Temporal task queue names are case sensitive
        private readonly Dictionary<string, WorkflowQueueRegistration> _byQueue = new Dictionary<string, WorkflowQueueRegistration>(StringComparer.Ordinal);
        private readonly List<WorkflowQueueRegistration> _queues = new List<WorkflowQueueRegistration>();

        // The merged queues, in the order they were first registered
        public IReadOnlyList<WorkflowQueueRegistration> Queues => _queues;

        // Register one workflow on a task queue, together with the activity interfaces it calls
        public WorkflowRegistry Register<TWorkflow>(string taskQueue, params Type[] activityServiceTypes)
        {
            return Register(taskQueue, typeof(TWorkflow), activityServiceTypes);
        }

        // Register one workflow on a task queue, together with the activity interfaces it calls
        public WorkflowRegistry Register(string taskQueue, Type workflowType, params Type[] activityServiceTypes)
        {
            if (workflowType == null)
                throw new ArgumentNullException(nameof(workflowType));

            if (workflowType.GetCustomAttribute<WorkflowAttribute>() == null)
                throw new ArgumentException($"'{workflowType.FullName}' is not a workflow, it carries no [Workflow] attribute", nameof(workflowType));

            var registration = _GetOrAdd(taskQueue);

            if (registration.WorkflowTypes.Contains(workflowType) == false)
                registration.WorkflowTypes.Add(workflowType);

            if (activityServiceTypes != null)
            {
                foreach (var activityServiceType in activityServiceTypes)
                    _AddActivityService(registration, activityServiceType);
            }

            return this;
        }

        // Register an activity interface on a queue on its own - for an activity implementation shared
        // by workflows living on different queues
        public WorkflowRegistry RegisterActivities(string taskQueue, params Type[] activityServiceTypes)
        {
            var registration = _GetOrAdd(taskQueue);

            if (activityServiceTypes != null)
            {
                foreach (var activityServiceType in activityServiceTypes)
                    _AddActivityService(registration, activityServiceType);
            }

            return this;
        }

        // The queue with this name, or null
        public WorkflowQueueRegistration FindQueue(string taskQueue)
        {
            if (string.IsNullOrWhiteSpace(taskQueue) == true)
                return null;

            _byQueue.TryGetValue(taskQueue, out var registration);
            return registration;
        }

        private WorkflowQueueRegistration _GetOrAdd(string taskQueue)
        {
            if (string.IsNullOrWhiteSpace(taskQueue) == true)
                throw new ArgumentException("the task queue name is required", nameof(taskQueue));

            if (_byQueue.TryGetValue(taskQueue, out var registration) == true)
                return registration;

            registration = new WorkflowQueueRegistration(taskQueue);
            _byQueue.Add(taskQueue, registration);
            _queues.Add(registration);

            return registration;
        }

        private static void _AddActivityService(WorkflowQueueRegistration registration, Type activityServiceType)
        {
            if (activityServiceType == null)
                throw new ArgumentNullException(nameof(activityServiceType));

            if (_HasActivityMethod(activityServiceType) == false)
                throw new ArgumentException($"'{activityServiceType.FullName}' declares no [Activity] method, so it is not an activity service", nameof(activityServiceType));

            if (registration.ActivityServiceTypes.Contains(activityServiceType) == false)
                registration.ActivityServiceTypes.Add(activityServiceType);
        }

        private static bool _HasActivityMethod(Type activityServiceType)
        {
            var methods = activityServiceType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);

            foreach (var method in methods)
            {
                if (method.GetCustomAttribute<ActivityAttribute>() != null)
                    return true;
            }

            return false;
        }
    }
}
