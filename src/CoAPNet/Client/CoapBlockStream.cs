using CoAPNet.Utils;
using System;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using CoAPNet.Options;

namespace CoAPNet.Client
{
    public abstract class CoapBlockStream : Stream
    {
        // Backing field for DefaultBlockSize
        private static int _defaultBlockSize = 1024;

        /// <summary>
        /// Gets or Sets the default blocksize used when initailising a new <see cref="CoapBlockStreamWriter"/>.
        /// </summary>
        public static int DefaultBlockSize
        {
            get => _defaultBlockSize;
            set => _defaultBlockSize = BlockBase.InternalSupportedBlockSizes.Any(b => b.Item2 == value)
                ? value
                : throw new ArgumentOutOfRangeException();
        }

        protected int BlockSizeInternal = DefaultBlockSize;

        protected readonly ICoapEndpointInfo Endpoint;

        protected Exception CaughtException;

        protected readonly AsyncAutoResetEvent FlushFinishedEvent = new AsyncAutoResetEvent(false);

        protected readonly CancellationTokenSource CancellationTokenSource = new CancellationTokenSource();

        public readonly CoapBlockWiseContext Context;

        protected bool EndOfStream;

        /// <summary>
        /// Gets or sets the maximum amount of time spent writing to <see cref="CoapClient"/> during <see cref="Dispose(bool)"/>
        /// </summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromMilliseconds(-1);

        /// <summary>
        /// Gets or Sets the Blocksize used for transfering data.
        /// </summary>
        /// <remarks>
        /// This can only be set with a decreased value to prevent unexpected behavior.
        /// </remarks>
        public int BlockSize
        {
            get => BlockSizeInternal;
            set
            {
                if (value > BlockSizeInternal)
                    throw new ArgumentOutOfRangeException($"Can not increase blocksize from {BlockSizeInternal} to {value}");

                if (BlockBase.InternalSupportedBlockSizes.All(b => b.Item2 != value))
                    throw new ArgumentOutOfRangeException($"Unsupported blocksize {value}. Expecting block sizes in ({string.Join(", ", BlockBase.InternalSupportedBlockSizes.Select(b => b.Item2))})");

                BlockSizeInternal = value;
            }
        }

        protected CoapBlockStream(CoapBlockWiseContext context, ICoapEndpointInfo endpoint = null)
        {
            Context = context ?? throw new ArgumentNullException(nameof(context));

            Endpoint = endpoint;
        }

        /// <summary>
        /// Gets the last <see cref="CoapMessageIdentifier"/> for the Block-Wise message stream. This may be used to retreive the the response from from the <see cref="CoapClient"/> by invokeing <see cref="CoapClient.GetResponseAsync(CoapMessageIdentifier, CancellationToken, bool)"/>
        /// </summary>
        /// <param name="ct">A cancelation token to cancel the async operation.</param>
        /// <returns>The very last <see cref="CoapMessageIdentifier"/> or <code>default</code> if the block-wise opration completed prematurely.</returns>
        //public Task<CoapMessageIdentifier> GetFinalMessageIdAsync(CancellationToken ct = default)
        //    => Task.Run(async () => await CoapMessageIdTask.Task, ct);

        protected void ThrowExceptionIfCaught()
        {
            if (CaughtException == null)
                return;

            var exception = CaughtException;
            CaughtException = null;
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }
}
