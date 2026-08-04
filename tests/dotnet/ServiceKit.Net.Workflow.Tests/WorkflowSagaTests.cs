namespace ServiceKit.Net.Tests
{
    [TestClass]
    public class WorkflowSagaTests
    {
        [TestMethod]
        public async Task Compensations_run_in_reverse_order()
        {
            var log = new List<string>();
            var saga = new WorkflowSaga();

            saga.Push("ReserveStock", () => { log.Add("ReleaseStock"); return Task.CompletedTask; });
            saga.Push("ChargeCard", () => { log.Add("RefundCard"); return Task.CompletedTask; });
            saga.Push("SendReceipt", () => { log.Add("VoidReceipt"); return Task.CompletedTask; });

            await saga.CompensateAsync();

            CollectionAssert.AreEqual(new[] { "VoidReceipt", "RefundCard", "ReleaseStock" }, log);
        }

        [TestMethod]
        public async Task Compensating_an_empty_saga_is_a_no_op()
        {
            var saga = new WorkflowSaga();

            await saga.CompensateAsync();

            Assert.IsTrue(saga.HasCompensated);
            Assert.AreEqual(0, saga.PendingCount);
        }

        [TestMethod]
        public async Task Compensating_twice_runs_the_compensations_once()
        {
            var calls = 0;
            var saga = new WorkflowSaga();
            saga.Push("ChargeCard", () => { calls++; return Task.CompletedTask; });

            await saga.CompensateAsync();
            await saga.CompensateAsync();

            Assert.AreEqual(1, calls);
        }

        [TestMethod]
        public async Task Pushing_after_compensation_is_rejected()
        {
            var saga = new WorkflowSaga();
            await saga.CompensateAsync();

            Assert.ThrowsException<InvalidOperationException>(() => saga.Push("ChargeCard", () => Task.CompletedTask));
        }

        [TestMethod]
        public void Pushing_without_a_step_name_is_rejected()
        {
            var saga = new WorkflowSaga();

            Assert.ThrowsException<ArgumentException>(() => saga.Push(null, () => Task.CompletedTask));
            Assert.ThrowsException<ArgumentException>(() => saga.Push("", () => Task.CompletedTask));
            Assert.ThrowsException<ArgumentException>(() => saga.Push("   ", () => Task.CompletedTask));
        }

        [TestMethod]
        public void Pushing_without_a_compensation_is_rejected()
        {
            var saga = new WorkflowSaga();

            Assert.ThrowsException<ArgumentNullException>(() => saga.Push("ChargeCard", null));
        }

        [TestMethod]
        public async Task A_failing_compensation_does_not_stop_the_others()
        {
            var log = new List<string>();
            var saga = new WorkflowSaga();

            saga.Push("ReserveStock", () => { log.Add("ReleaseStock"); return Task.CompletedTask; });
            saga.Push("ChargeCard", () => throw new InvalidOperationException("the gateway is down"));
            saga.Push("SendReceipt", () => { log.Add("VoidReceipt"); return Task.CompletedTask; });

            var thrown = await Assert.ThrowsExceptionAsync<WorkflowCompensationException>(() => saga.CompensateAsync());

            // The step before the failing one must still have been rolled back
            CollectionAssert.AreEqual(new[] { "VoidReceipt", "ReleaseStock" }, log);
            Assert.AreEqual(1, thrown.Failures.Count);
            Assert.AreEqual("ChargeCard", thrown.Failures[0].StepName);
            Assert.IsInstanceOfType(thrown.Failures[0].Failure, typeof(InvalidOperationException));
        }

        [TestMethod]
        public async Task Every_failing_compensation_is_collected()
        {
            var saga = new WorkflowSaga();
            saga.Push("ReserveStock", () => throw new InvalidOperationException("stock service is down"));
            saga.Push("ChargeCard", () => throw new ApplicationException("the gateway is down"));

            var thrown = await Assert.ThrowsExceptionAsync<WorkflowCompensationException>(() => saga.CompensateAsync());

            // Reverse order here too: ChargeCard was pushed last, so it is compensated first
            Assert.AreEqual(2, thrown.Failures.Count);
            Assert.AreEqual("ChargeCard", thrown.Failures[0].StepName);
            Assert.AreEqual("ReserveStock", thrown.Failures[1].StepName);
        }

        [TestMethod]
        public async Task The_original_failure_survives_as_the_inner_exception()
        {
            var original = new InvalidOperationException("the order was rejected");
            var saga = new WorkflowSaga();
            saga.Push("ChargeCard", () => throw new ApplicationException("the gateway is down"));

            var thrown = await Assert.ThrowsExceptionAsync<WorkflowCompensationException>(() => saga.CompensateAsync(original));

            Assert.AreSame(original, thrown.InnerException);
            StringAssert.Contains(thrown.Message, "ChargeCard");
        }

        [TestMethod]
        public async Task Without_an_original_failure_there_is_no_inner_exception()
        {
            var saga = new WorkflowSaga();
            saga.Push("ChargeCard", () => throw new ApplicationException("the gateway is down"));

            var thrown = await Assert.ThrowsExceptionAsync<WorkflowCompensationException>(() => saga.CompensateAsync());

            Assert.IsNull(thrown.InnerException);
        }

        [TestMethod]
        public async Task A_successful_saga_reports_nothing_pending()
        {
            var saga = new WorkflowSaga();
            saga.Push("ReserveStock", () => Task.CompletedTask);
            saga.Push("ChargeCard", () => Task.CompletedTask);

            Assert.AreEqual(2, saga.PendingCount);
            CollectionAssert.AreEqual(new[] { "ReserveStock", "ChargeCard" }, saga.PendingSteps.ToList());
            Assert.IsFalse(saga.HasCompensated);

            await saga.CompensateAsync();

            Assert.AreEqual(0, saga.PendingCount);
            Assert.IsTrue(saga.HasCompensated);
        }

        [TestMethod]
        public async Task Compensations_are_awaited_not_merely_started()
        {
            var finished = false;
            var saga = new WorkflowSaga();
            saga.Push("ChargeCard", async () =>
            {
                await Task.Yield();
                finished = true;
            });

            await saga.CompensateAsync();

            Assert.IsTrue(finished);
        }
    }
}
