using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Identity;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;

namespace Enqueuer.Queue
{
    // Sends one scan request at a time, turning Azure's failures into the two the enqueuer reports (Issue 3 design,
    // decisions 10 and 13). Anything else, a cancelled run or a bug, goes through unchanged.
    public sealed class ScanRequestSender
    {
        private readonly QueueClient _queue;

        public ScanRequestSender(QueueClient queue)
        {
            _queue = queue;
        }

        public async Task SendAsync(ScanRequest request, CancellationToken cancellationToken)
        {
            string body = ScanRequestWriter.Write(request);
            try
            {
                // Visible at once, with Azure's default lifetime of 7 days (decision 16). The SDK retries a failed send
                // itself, 5 times (decision 11).
                await _queue.SendMessageAsync(body, visibilityTimeout: null, timeToLive: null, cancellationToken);
            }
            catch (RequestFailedException exception) when (exception.ErrorCode == QueueErrorCode.QueueNotFound)
            {
                throw new ScanQueueNotFoundException(_queue.Name, exception);
            }
            catch (RequestFailedException exception) when (exception.Status != 0)
            {
                // The status and error code only: the SDK's own message runs to many lines, with every response header.
                // Status 0 means Azure never answered, so it is left to the catch for an unreachable queue below.
                string answer = string.IsNullOrEmpty(exception.ErrorCode)
                    ? "Azure answered " + exception.Status + "."
                    : "Azure answered " + exception.Status + " (" + exception.ErrorCode + ").";
                throw new ScanQueueUnavailableException(Problem(request, answer), exception);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The job was stopped: not a failed send
                throw;
            }
            catch (Exception exception) when (IsQueueUnreachable(exception))
            {
                // Stopped part way through the retries, Azure.Core reports the cancellation inside an AggregateException
                // with the earlier failures, but it is still not a failed send
                cancellationToken.ThrowIfCancellationRequested();

                // The messages can run to many lines, so only the first is kept
                throw new ScanQueueUnavailableException(Problem(request, FirstLine(exception.Message)), exception);
            }
        }

        // How the storage SDK reports a queue it could not reach, or an identity that got no token, once its retries
        // are spent. Several failed tries come as an AggregateException; a lone last try, after the others were answered
        // 5xx, comes bare: a RequestFailedException with status 0, an IOException, or the SDK's own network timeout as a
        // cancellation the run did not ask for (final review, finding 1). Anything else, such as a bug, goes through
        // unchanged, with its stack trace.
        private static bool IsQueueUnreachable(Exception exception)
        {
            return exception is AggregateException
                || exception is AuthenticationFailedException
                || exception is RequestFailedException
                || exception is IOException
                || exception is OperationCanceledException;
        }

        private string Problem(ScanRequest request, string reason)
        {
            return "The scan request for " + request.ProjectName + "/" + request.RepositoryName + " could not be sent to " + _queue.Name + ": " + reason;
        }

        private static string FirstLine(string text)
        {
            using (StringReader reader = new StringReader(text))
            {
                return reader.ReadLine() ?? string.Empty;
            }
        }
    }
}
