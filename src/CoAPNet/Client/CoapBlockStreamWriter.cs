using CoAPNet.Utils;
using System;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace CoAPNet.Client
{
    /// <summary>
    /// A Coap Block-Wise Tranfer (RFC 7959) implementation of <see cref="Stream"/>.
    /// </summary>
    public class CoapBlockStreamWriter : CoapBlockStream
    {
        private readonly ByteQueue _writer = new ByteQueue();
        private readonly Task _writerTask;
        private readonly AsyncAutoResetEvent _writerEvent = new AsyncAutoResetEvent(false);
        private int _writeBlockNumber;

        /// <inheritdoc/>
        public override bool CanRead => false;

        /// <inheritdoc/>
        public override bool CanSeek => false;

        /// <inheritdoc/>
        public override bool CanWrite => !EndOfStream && (_writerTask?.IsCompleted ?? false);

        /// <summary>
        /// Create a new <see cref="CoapBlockStreamWriter"/> using <paramref name="client"/> to read and write blocks of data. <paramref name="baseMessage"/> is required to base blocked messages off of.
        /// </summary>
        /// <param name="client"></param>
        /// <param name="baseMessage"></param>
        /// <param name="endpoint"></param>
        public CoapBlockStreamWriter(CoapClient client, CoapMessage baseMessage, ICoapEndpoint endpoint = null)
            : this(baseMessage.CreateBlockWiseContext(client), endpoint)
        { }

        public CoapBlockStreamWriter(CoapBlockWiseContext context, ICoapEndpoint endpoint = null)
            : base(context, endpoint)
        {
            if (!Context.Request.Code.IsRequest())
                throw new InvalidOperationException($"Can not create a {nameof(CoapBlockStreamWriter)} with a {nameof(context)}.{nameof(context.Request)}.{nameof(CoapMessage.Type)} of {context.Request.Type}");

            _writerTask = WriteBlocksAsync();
        }

        private async Task WriteBlocksAsync()
        {
            var token = CancellationTokenSource.Token;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    await _writerEvent.WaitAsync(token);

                    while (_writer.Length > BlockSize || EndOfStream && _writer.Length > 0)
                    {
                        var message = Context.Request.Clone();

                        // Reset the message Id so it's set by CoapClient
                        message.Id = 0;

                        message.Options.Add(new Options.Block1(_writeBlockNumber, BlockSizeInternal, _writer.Length > BlockSizeInternal));

                        message.Payload = new byte[_writer.Length < BlockSizeInternal ? _writer.Length : BlockSizeInternal];
                        _writer.Peek(message.Payload, 0, BlockSizeInternal);

                        Context.MessageId = await Context.Client.SendAsync(message, token);

                        // Keep the response in the queue in case the Applciation needs it.
                        var result = await Context.Client.GetResponseAsync(Context.MessageId, token);

                        if (EndOfStream)
                            Context.Response = result;

                        if (result.Code.IsSuccess())
                        {
                            _writer.AdvanceQueue(message.Payload.Length);
                            _writeBlockNumber++;

                            var block = result.Options.Get<Options.Block1>();
                            var blockDelta = block.BlockSize - BlockSizeInternal;

                            // Only update the size if it's smaller
                            if (blockDelta < 0)
                            {
                                BlockSizeInternal += blockDelta;
                                _writeBlockNumber -= blockDelta / BlockSizeInternal;
                            }
                            else if (blockDelta > 0)
                                throw new CoapBlockException($"Remote endpoint requested to increase blocksize from {BlockSizeInternal} to {BlockSizeInternal + blockDelta}");

                        }
                        else if (result.Code.IsClientError() || result.Code.IsServerError())
                        {
                            if (_writeBlockNumber == 0 && result.Code == CoapMessageCode.RequestEntityTooLarge && BlockSizeInternal > 16)
                            {
                                // Try again and attempt at sending a smaller block size.
                                _writeBlockNumber = 0;
                                BlockSizeInternal /= 2;

                                continue;
                            }

                            Context.Response = result;
                            throw new CoapBlockException($"Failed to send block ({_writeBlockNumber}) to remote endpoint", CoapException.FromCoapMessage(result), result.Code);
                        }
                    }

                    // flag the mot recent flush has been performed
                    if (_writer.Length <= BlockSize)
                        FlushFinishedEvent.Set();

                    if (EndOfStream)
                        break;
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
                FlushFinishedEvent.Set();
            }
        }

        /// <summary>
        /// Attempt to flush any blocks to <see cref="CoapClient"/> that have been queued up.
        /// </summary>
        /// <inheritdoc/>
        public override void Flush()
        {
            if (CaughtException == null && !_writerTask.IsCompleted)
            {
                _writerEvent.Set();
                FlushFinishedEvent.WaitAsync(CancellationToken.None).Wait();
            }

            ThrowExceptionIfCaught();
        }

        /// <summary>
        /// Attempt to flush any blocks to <see cref="CoapClient"/> that have been queued up.
        /// </summary>
        /// <inheritdoc/>
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            _writerEvent.Set();

            await FlushFinishedEvent.WaitAsync(cancellationToken);

            ThrowExceptionIfCaught();
        }

        /// <inheritdoc/>
        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <inheritdoc/>
        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (EndOfStream)
                throw new EndOfStreamException("Stream ended before all bytes were written", CaughtException);

            // Lets artificailly block while the writer task has blocks to write.
            if (_writer.Length > BlockSize)
                await FlushFinishedEvent.WaitAsync(cancellationToken);

            _writer.Enqueue(buffer, offset, count);
            _writerEvent.Set();
        }

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ThrowExceptionIfCaught();

                EndOfStream = true;

                if (_writerTask != null && !_writerTask.IsCompleted)
                {
                    // Write any/all data to the output
                    if (_writer.Length > 0)
                        _writerEvent.Set();

                    CancellationTokenSource.CancelAfter(Timeout);

                    try
                    {
                        _writerTask.Wait();
                    }
                    catch (AggregateException ex)
                    {
                        ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                    }

                    ThrowExceptionIfCaught();
                }
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
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        #endregion
    }
}
