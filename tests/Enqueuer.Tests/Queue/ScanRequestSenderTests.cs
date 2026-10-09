using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Identity;
using Enqueuer.Queue;
using NUnit.Framework;

namespace Enqueuer.Tests.Queue
{
    // Sending one request, and the two failures the enqueuer reports as one line each (Issue 3 design, decisions 10
    // and 13)
    [TestFixture]
    public class ScanRequestSenderTests
    {
        private const string Prefix = "The scan request for Kudos Wall/victim-repo could not be sent to scan-test: ";

        private static readonly ScanRequest Request = new ScanRequest(
            new Guid("5b1c6f3e-8a2d-4e7f-9c1a-2d3e4f5a6b7c"), new Guid("0f8fad5b-d9cb-469f-a165-70867728950e"), "Kudos Wall", "victim-repo", "main");

        private FakeQueueClient _queue = null!;
        private ScanRequestSender _sender = null!;

        [SetUp]
        public void SetUp()
        {
            _queue = new FakeQueueClient("scan-test");
            _sender = new ScanRequestSender(_queue);
        }

        // Visible at once, with Azure's default lifetime (decision 16)
        [Test]
        public async Task SendAsync_ARequest_SendsTheWritersBodyVisibleAtOnceWithTheDefaultLifetime()
        {
            await _sender.SendAsync(Request, CancellationToken.None);

            Assert.That(_queue.Sent, Is.EqualTo(new[] { (ScanRequestWriter.Write(Request), (TimeSpan?)null, (TimeSpan?)null) }));
        }

        [Test]
        public void SendAsync_WhenTheQueueIsMissing_ThrowsNotFoundNamingTheQueue()
        {
            RequestFailedException notFound = new RequestFailedException(404, "The specified queue does not exist.", "QueueNotFound", null);
            _queue.FailSend(1, notFound);

            ScanQueueNotFoundException? exception = Assert.ThrowsAsync<ScanQueueNotFoundException>(() => _sender.SendAsync(Request, CancellationToken.None));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(exception!.Message, Is.EqualTo("The queue scan-test does not exist. Create it, or correct Enqueuer:Queue:Name."));
                Assert.That(exception.InnerException, Is.SameAs(notFound));
            }
        }

        // The status and error code only, never the SDK's own message, which lists every response header; and no empty
        // brackets when there is no error code
        [TestCase(403, "AuthorizationPermissionMismatch", "Azure answered 403 (AuthorizationPermissionMismatch).")]
        [TestCase(503, null, "Azure answered 503.")]
        public void SendAsync_WhenAzureRefuses_ThrowsUnavailableWithStatusAndErrorCode(int status, string? errorCode, string answer)
        {
            RequestFailedException refused = new RequestFailedException(
                status, "Service request failed.\r\nStatus: " + status + "\r\nHeaders:\r\nx-ms-request-id: 1234", errorCode, null);
            _queue.FailSend(1, refused);

            ScanQueueUnavailableException? exception = Assert.ThrowsAsync<ScanQueueUnavailableException>(() => _sender.SendAsync(Request, CancellationToken.None));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(exception!.Message, Is.EqualTo(Prefix + answer));
                Assert.That(exception.Message, Does.Not.Contain("Headers"));
                Assert.That(exception.InnerException, Is.SameAs(refused));
            }
        }

        // Azure.Core throws an AggregateException when its retries of a network error or a timeout run out
        [Test]
        public void SendAsync_WhenTheRetriesRunOut_GivesTheFirstLineOfTheMessage()
        {
            _queue.FailSend(1, new AggregateException(
                "Retry failed after 6 tries.\r\nRetry settings can be adjusted in ClientOptions.Retry.", new RequestFailedException("Connection refused")));

            ScanQueueUnavailableException? exception = Assert.ThrowsAsync<ScanQueueUnavailableException>(() => _sender.SendAsync(Request, CancellationToken.None));

            Assert.That(exception!.Message, Is.EqualTo(Prefix + "Retry failed after 6 tries."));
        }

        // In Azure, the job's identity could not get a token: DefaultAzureCredential lists every credential it tried
        [Test]
        public void SendAsync_WhenTheIdentityGetsNoToken_GivesTheFirstLineOfTheMessage()
        {
            _queue.FailSend(1, new AuthenticationFailedException(
                "DefaultAzureCredential failed to retrieve a token from the included credentials.\n- EnvironmentCredential authentication unavailable."));

            ScanQueueUnavailableException? exception = Assert.ThrowsAsync<ScanQueueUnavailableException>(() => _sender.SendAsync(Request, CancellationToken.None));

            Assert.That(exception!.Message, Is.EqualTo(Prefix + "DefaultAzureCredential failed to retrieve a token from the included credentials."));
        }

        // Azure.Core rethrows a lone failure bare, not in an AggregateException: when only the last of the tries fails at
        // the network, after the others were answered 5xx (final review, finding 1). Still one line, never "Azure
        // answered 0".
        [Test]
        public void SendAsync_WhenTheLastTryTimesOutAlone_GivesTheFirstLineOfTheMessage()
        {
            _queue.FailSend(1, new TaskCanceledException(
                "The operation was cancelled because it exceeded the configured timeout of 0:01:40.\r\nNetwork timeout can be adjusted in ClientOptions.Retry.NetworkTimeout."));

            ScanQueueUnavailableException? exception = Assert.ThrowsAsync<ScanQueueUnavailableException>(() => _sender.SendAsync(Request, CancellationToken.None));

            Assert.That(exception!.Message, Is.EqualTo(Prefix + "The operation was cancelled because it exceeded the configured timeout of 0:01:40."));
        }

        [Test]
        public void SendAsync_WhenTheLastTryCannotConnectAlone_GivesTheReasonNotAnAnswer()
        {
            _queue.FailSend(1, new RequestFailedException(
                0, "No connection could be made because the target machine actively refused it. (127.0.0.1:10001)", null, null));

            ScanQueueUnavailableException? exception = Assert.ThrowsAsync<ScanQueueUnavailableException>(() => _sender.SendAsync(Request, CancellationToken.None));

            Assert.That(exception!.Message, Is.EqualTo(Prefix + "No connection could be made because the target machine actively refused it. (127.0.0.1:10001)"));
        }

        [Test]
        public void SendAsync_WhenTheLastTryLosesTheConnectionAlone_GivesTheFirstLineOfTheMessage()
        {
            _queue.FailSend(1, new IOException("Unable to read data from the transport connection: An existing connection was forcibly closed by the remote host."));

            ScanQueueUnavailableException? exception = Assert.ThrowsAsync<ScanQueueUnavailableException>(() => _sender.SendAsync(Request, CancellationToken.None));

            Assert.That(exception!.Message, Is.EqualTo(Prefix + "Unable to read data from the transport connection: An existing connection was forcibly closed by the remote host."));
        }

        // The job being stopped is not a failed send: the run's own token is cancelled
        [Test]
        public void SendAsync_WhenCancelled_LetsTheCancellationThrough()
        {
            using (CancellationTokenSource stopping = new CancellationTokenSource())
            {
                stopping.Cancel();
                _queue.FailSend(1, new OperationCanceledException(stopping.Token));

                Assert.ThrowsAsync<OperationCanceledException>(() => _sender.SendAsync(Request, stopping.Token));
            }
        }

        // Stopped part way through the retries, Azure.Core reports the cancellation inside an AggregateException with
        // the earlier failure (final review, finding 2). Still not a failed send.
        [Test]
        public void SendAsync_WhenCancelledDuringTheRetries_LetsTheCancellationThrough()
        {
            using (CancellationTokenSource stopping = new CancellationTokenSource())
            {
                stopping.Cancel();
                _queue.FailSend(1, new AggregateException(
                    "Retry failed after 2 tries.", new RequestFailedException(0, "No connection could be made.", null, null), new TaskCanceledException()));

                Assert.ThrowsAsync<OperationCanceledException>(() => _sender.SendAsync(Request, stopping.Token));
            }
        }

        // A bug keeps Program's "Enqueuer failed" line and its stack trace, so it can be diagnosed
        [Test]
        public void SendAsync_WhenSomethingUnexpectedFails_LetsItThrough()
        {
            _queue.FailSend(1, new InvalidOperationException("a bug"));

            Assert.ThrowsAsync<InvalidOperationException>(() => _sender.SendAsync(Request, CancellationToken.None));
        }
    }
}
