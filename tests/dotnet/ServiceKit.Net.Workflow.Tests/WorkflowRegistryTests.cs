namespace ServiceKit.Net.Tests
{
    [TestClass]
    public class WorkflowRegistryTests
    {
        [TestMethod]
        public void Two_workflows_on_the_same_queue_merge_into_one_worker()
        {
            // This is what makes "one queue per workflow" the safe default: the coarser "one queue per
            // context" layout is reachable at runtime just by handing out the same queue name.
            var registry = new WorkflowRegistry();
            registry.Register<FulfilOrderWorkflow>("Orders", typeof(IFulfilOrderActivities));
            registry.Register<CancelOrderWorkflow>("Orders", typeof(IStockActivities));

            Assert.AreEqual(1, registry.Queues.Count);

            var queue = registry.Queues[0];
            Assert.AreEqual("Orders", queue.TaskQueue);
            CollectionAssert.AreEquivalent(new[] { typeof(FulfilOrderWorkflow), typeof(CancelOrderWorkflow) }, queue.WorkflowTypes.ToList());
            CollectionAssert.AreEquivalent(new[] { typeof(IFulfilOrderActivities), typeof(IStockActivities) }, queue.ActivityServiceTypes.ToList());
        }

        [TestMethod]
        public void Workflows_on_different_queues_stay_separate()
        {
            var registry = new WorkflowRegistry();
            registry.Register<FulfilOrderWorkflow>("Orders.FulfilOrder");
            registry.Register<CancelOrderWorkflow>("Orders.CancelOrder");

            Assert.AreEqual(2, registry.Queues.Count);
            Assert.AreEqual("Orders.FulfilOrder", registry.Queues[0].TaskQueue);
            Assert.AreEqual("Orders.CancelOrder", registry.Queues[1].TaskQueue);
        }

        [TestMethod]
        public void A_shared_activity_service_has_to_reach_every_queue_that_uses_it()
        {
            // Command and query are reused across workflows, so one activity implementation can be
            // needed on several queues. Registering it per workflow is what gets it there.
            var registry = new WorkflowRegistry();
            registry.Register<FulfilOrderWorkflow>("Orders.FulfilOrder", typeof(INotificationActivities));
            registry.Register<CancelOrderWorkflow>("Orders.CancelOrder", typeof(INotificationActivities));

            Assert.AreEqual(2, registry.Queues.Count);
            CollectionAssert.Contains(registry.FindQueue("Orders.FulfilOrder").ActivityServiceTypes.ToList(), typeof(INotificationActivities));
            CollectionAssert.Contains(registry.FindQueue("Orders.CancelOrder").ActivityServiceTypes.ToList(), typeof(INotificationActivities));
        }

        [TestMethod]
        public void The_same_workflow_registered_twice_is_not_duplicated()
        {
            var registry = new WorkflowRegistry();
            registry.Register<FulfilOrderWorkflow>("Orders", typeof(IFulfilOrderActivities));
            registry.Register<FulfilOrderWorkflow>("Orders", typeof(IFulfilOrderActivities));

            var queue = registry.Queues.Single();
            Assert.AreEqual(1, queue.WorkflowTypes.Count);
            Assert.AreEqual(1, queue.ActivityServiceTypes.Count);
        }

        [TestMethod]
        public void An_activity_service_can_be_registered_on_its_own()
        {
            var registry = new WorkflowRegistry();
            registry.Register<FulfilOrderWorkflow>("Orders");
            registry.RegisterActivities("Orders", typeof(INotificationActivities));

            var queue = registry.Queues.Single();
            Assert.AreEqual(1, queue.WorkflowTypes.Count);
            CollectionAssert.AreEqual(new[] { typeof(INotificationActivities) }, queue.ActivityServiceTypes.ToList());
        }

        [TestMethod]
        public void Task_queue_names_are_case_sensitive()
        {
            // Temporal treats them as distinct, so the registry must not merge them
            var registry = new WorkflowRegistry();
            registry.Register<FulfilOrderWorkflow>("Orders");
            registry.Register<CancelOrderWorkflow>("orders");

            Assert.AreEqual(2, registry.Queues.Count);
        }

        [TestMethod]
        public void A_type_without_the_workflow_attribute_is_rejected()
        {
            var registry = new WorkflowRegistry();

            var thrown = Assert.ThrowsException<ArgumentException>(() => registry.Register<NotAWorkflow>("Orders"));
            StringAssert.Contains(thrown.Message, "[Workflow]");
        }

        [TestMethod]
        public void A_service_without_activity_methods_is_rejected()
        {
            var registry = new WorkflowRegistry();

            var thrown = Assert.ThrowsException<ArgumentException>(() => registry.Register<FulfilOrderWorkflow>("Orders", typeof(IPlainService)));
            StringAssert.Contains(thrown.Message, "[Activity]");
        }

        [TestMethod]
        public void A_missing_task_queue_name_is_rejected()
        {
            var registry = new WorkflowRegistry();

            Assert.ThrowsException<ArgumentException>(() => registry.Register<FulfilOrderWorkflow>(null));
            Assert.ThrowsException<ArgumentException>(() => registry.Register<FulfilOrderWorkflow>("   "));
        }

        [TestMethod]
        public void A_rejected_registration_leaves_no_half_built_queue()
        {
            var registry = new WorkflowRegistry();

            Assert.ThrowsException<ArgumentException>(() => registry.Register<NotAWorkflow>("Orders"));

            Assert.AreEqual(0, registry.Queues.Count);
        }

        [TestMethod]
        public void An_unknown_queue_is_not_found()
        {
            var registry = new WorkflowRegistry();
            registry.Register<FulfilOrderWorkflow>("Orders");

            Assert.IsNull(registry.FindQueue("Payments"));
            Assert.IsNull(registry.FindQueue(null));
        }

        [TestMethod]
        public void The_compensation_exception_fails_the_workflow_by_default()
        {
            // Leave it out of WorkflowFailureExceptionTypes and Temporal fails the workflow TASK
            // instead, then retries it forever - the single easiest thing to forget here.
            var options = new WorkflowWorkerOptions();

            CollectionAssert.Contains(options.WorkflowFailureExceptionTypes.ToList(), typeof(WorkflowCompensationException));
        }
    }
}
