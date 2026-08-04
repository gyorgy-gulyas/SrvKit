using Temporalio.Activities;
using Temporalio.Workflows;

namespace ServiceKit.Net.Tests
{
    // Fixtures for the registry tests. They are never executed - the registry only inspects their
    // attributes - but they are shaped like the real generated output.

    [Workflow]
    public class FulfilOrderWorkflow
    {
        [WorkflowRun]
        public Task<string> RunAsync(string orderId)
        {
            return Task.FromResult(orderId);
        }
    }

    [Workflow]
    public class CancelOrderWorkflow
    {
        [WorkflowRun]
        public Task RunAsync(string orderId)
        {
            return Task.CompletedTask;
        }
    }

    public class NotAWorkflow
    {
    }

    public interface IFulfilOrderActivities
    {
        [Activity]
        Task<string> ChargeCard(string orderId, decimal amount);

        [Activity]
        Task RefundCard(string orderId, string chargeId);
    }

    public interface IStockActivities
    {
        [Activity]
        Task ReserveStock(string orderId, string sku);
    }

    // Shared by both workflows - the case that has to land on every queue
    public interface INotificationActivities
    {
        [Activity]
        Task Notify(string orderId, string message);
    }

    public interface IPlainService
    {
        Task DoSomething(string orderId);
    }
}
