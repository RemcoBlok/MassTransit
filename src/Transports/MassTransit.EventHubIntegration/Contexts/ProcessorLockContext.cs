namespace MassTransit.EventHubIntegration.Contexts
{
    using System;
    using System.Diagnostics;
    using System.Threading.Tasks;
    using Azure.Messaging.EventHubs.Processor;
    using Configuration;
    using Context;
    using Util;


    public class ProcessorLockContext :
        IProcessorLockContext
    {
        readonly SingleThreadedDictionary<string, PartitionCheckpointData> _data = new SingleThreadedDictionary<string, PartitionCheckpointData>();
        readonly IHostConfiguration _hostConfiguration;
        readonly ushort _maxCount;
        readonly TimeSpan _timeout;

        public ProcessorLockContext(IHostConfiguration hostConfiguration, ReceiveSettings receiveSettings)
        {
            _hostConfiguration = hostConfiguration;
            _timeout = receiveSettings.CheckpointInterval;
            _maxCount = receiveSettings.CheckpointMessageCount;
        }

        public Task Complete(ProcessEventArgs eventArgs)
        {
            LogContext.SetCurrentIfNull(_hostConfiguration.ReceiveLogContext);

            return _data.TryGetValue(eventArgs.Partition.PartitionId, out var data)
                ? data.TryCheckpointAsync(eventArgs)
                : TaskUtil.Completed;
        }

        public async Task OnPartitionInitializing(PartitionInitializingEventArgs eventArgs)
        {
            LogContext.SetCurrentIfNull(_hostConfiguration.ReceiveLogContext);

            _data.TryAdd(eventArgs.PartitionId, _ => new PartitionCheckpointData(_timeout, _maxCount));
            LogContext.Info?.Log("Partition: {PartitionId} was initialized", eventArgs.PartitionId);
        }

        public async Task OnPartitionClosing(PartitionClosingEventArgs eventArgs)
        {
            LogContext.SetCurrentIfNull(_hostConfiguration.ReceiveLogContext);

            if (_data.TryRemove(eventArgs.PartitionId, out var data))
                await data.Close(eventArgs).ConfigureAwait(false);
        }


        sealed class PartitionCheckpointData
        {
            readonly ushort _maxCount;
            readonly TimeSpan _timeout;
            readonly Stopwatch _timer;
            ProcessEventArgs _current;
            ushort _processed;

            public PartitionCheckpointData(TimeSpan timeout, ushort maxCount)
            {
                _timeout = timeout;
                _maxCount = maxCount;

                _processed = 0;
                _timer = Stopwatch.StartNew();
            }

            public async Task<bool> TryCheckpointAsync(ProcessEventArgs args)
            {
                void Reset()
                {
                    _current = default;
                    _processed = 0;
                    _timer.Restart();
                }

                _current = args;
                _processed += 1;

                if (_processed < _maxCount && _timer.Elapsed < _timeout)
                    return false;

                LogContext.Debug?.Log("Partition: {PartitionId} updating checkpoint with offset: {Offset}", _current.Partition.PartitionId,
                    _current.Data.Offset);
                await _current.UpdateCheckpointAsync().ConfigureAwait(false);
                Reset();
                return true;
            }

            public async Task Close(PartitionClosingEventArgs args)
            {
                try
                {
                    if (!_current.HasEvent || args.Reason != ProcessingStoppedReason.Shutdown)
                        return;

                    LogContext.Debug?.Log("Partition: {PartitionId} updating checkpoint with offset: {Offset}", _current.Partition.PartitionId,
                        _current.Data.Offset);
                    await _current.UpdateCheckpointAsync().ConfigureAwait(false);
                }
                finally
                {
                    _timer.Stop();
                    _current = default;
                    LogContext.Info?.Log("Partition: {PartitionId} was closed, reason: {Reason}", args.PartitionId, args.Reason);
                }
            }
        }
    }
}
