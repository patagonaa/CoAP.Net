using CoAPNet.Utils;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CoAPNet.Options;

namespace CoAPNet.Client
{
    /// <summary>
    /// A Coap Block-Wise Tranfer (RFC 7959) implementation of <see cref="Stream"/>.
    /// </summary>
    public class CoapBlockStreamReader : CoapBlockStream
    {
        private readonly ByteQueue _reader = new ByteQueue();
        private readonly Task _readerTask;
        private readonly AsyncAutoResetEvent _readerEvent = new AsyncAutoResetEvent(false);

        private int _readBlockNumber;

        /// <inheritdoc/>
        public override bool CanRead => !EndOfStream || _reader.Length > 0;

        /// <inheritdoc/>
        public override bool CanSeek => false;

        /// <inheritdoc/>
        public override bool CanWrite => false;

        /// <summary>
        /// Create a new <see cref="CoapBlockStreamWriter"/> using <paramref name="client"/> to read and write blocks of data. <paramref name="response"/> is required to base blocked messages off of.
        /// </summary>
        /// <param name="client"></param>
        /// <param name="response"></param>
        /// <param name="request"></param>
        /// <param name="endpoint"></param>
        public CoapBlockStreamReader(CoapClient client, CoapMessage response, CoapMessage request, ICoapEndpointInfo endpoint = null)
            : this(request.CreateBlockWiseContext(client, response), endpoint)
        { }

        public CoapBlockStreamReader(CoapBlockWiseContext context, ICoapEndpointInfo endpoint = null)
            : base(context, endpoint)
        {
            if (context.Response == null)
                throw new ArgumentNullException($"{nameof(context)}.{nameof(context.Response)}");

            var payload = Context.Response.Payload;
            Context.Request.Payload = null;
            Context.Response.Payload = null;

            if (payload != null)
                _reader.Enqueue(payload, 0, payload.Length);

            var block2 = Context.Response.Options.Get<Block2>();
            if (block2 != null)
            {
                _readBlockNumber = block2.BlockNumber;

                BlockSizeInternal = block2.BlockSize;
                EndOfStream = !block2.IsMoreFollowing;

                if (payload != null)
                    _readBlockNumber += payload.Length / BlockSizeInternal;

                _readerTask = ReadBlocksAsync();
            }
            else
            {
                EndOfStream = true;
                _readerTask = Task.CompletedTask;
            }
        }

        private async Task ReadBlocksAsync()
        {
            var cancellationToken = CancellationTokenSource.Token;

            try
            {
                while (!EndOfStream && !cancellationToken.IsCancellationRequested)
                {
                    var message = Context.Request.Clone();
                    message.Id = 0;

                    // Strip out any block options
                    message.Options.RemoveAll(o => o is Block1 || o is Block2);

                    message.Options.Add(new Block2(_readBlockNumber, BlockSizeInternal));

                    Context.MessageId = await Context.Client.SendAsync(message, Endpoint, cancellationToken);

                    var response = await Context.Client.GetResponseAsync(Context.MessageId, cancellationToken);

                    if (!response.Code.IsSuccess())
                        throw new CoapBlockException("Error occured while reading blocks from remote endpoint",
                            CoapException.FromCoapMessage(response), response.Code);

                    var block2 = response.Options.Get<Block2>();

                    if (block2.BlockNumber != _readBlockNumber)
                        throw new CoapBlockException("Received incorrect block number from remote host");

                    _readBlockNumber++;

                    _reader.Enqueue(response.Payload, 0, response.Payload.Length);
                    _readerEvent.Set();

                    if (!response.Options.Get<Block2>().IsMoreFollowing)
                    {
                        EndOfStream = true;
                        Context.Response = response;
                    }
                }
            }
            catch (Exception ex)
            {
                // Hold onto the exception to throw it from a synchronous call.
                CaughtException = ex;
            }
            finally
            {
                EndOfStream = true;
                _readerEvent.Set();
            }
        }

        /// <summary>
        /// Attempt to flush any blocks to <see cref="CoapClient"/> that have been queued up.
        /// </summary>
        /// <inheritdoc/>
        public override void Flush()
        {
            ThrowExceptionIfCaught();
        }

        /// <summary>
        /// Attempt to flush any blocks to <see cref="CoapClient"/> that have been queued up.
        /// </summary>
        /// <inheritdoc/>
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await FlushFinishedEvent.WaitAsync(cancellationToken);

            ThrowExceptionIfCaught();
        }

        /// <inheritdoc/>
        public override int Read(byte[] buffer, int offset, int count)
        {
            return ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <inheritdoc/>
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var read = 0;
            while (read < count && !cancellationToken.IsCancellationRequested)
            {
                if (!EndOfStream)
                    await _readerEvent.WaitAsync(cancellationToken);

                var bytesDequeued = _reader.Dequeue(buffer, offset + read, count - read);
                read += bytesDequeued;

                if (bytesDequeued == 0 && EndOfStream)
                    break;
            }

            return read;
        }

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ThrowExceptionIfCaught();

                EndOfStream = true;
            }

            base.Dispose(disposing);
        }

        #region NotSupppoorted

        /// <inheritdoc/>
        public override long Length => throw new NotSupportedException();

        /// <inheritdoc/>
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        /// <inheritdoc/>
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <inheritdoc/>
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <inheritdoc/>
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        #endregion
    }
}
