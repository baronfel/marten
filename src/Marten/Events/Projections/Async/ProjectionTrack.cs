using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace Marten.Events.Projections.Async
{
    // Tested through integration tests
    public class ProjectionTrack : IProjectionTrack
    {
        private readonly CancellationTokenSource _cancellation;
        private readonly DaemonOptions _options;
        private readonly IProjection _projection;
        private readonly IDocumentSession _session;
        private readonly ActionBlock<EventPage> _track;

        private readonly IList<EventWaiter> _waiters = new List<EventWaiter>();

        public ProjectionTrack(DaemonOptions options, IProjection projection, IDocumentSession session)
        {
            _options = options;
            _projection = projection;
            _session = session;

            // TODO -- use this differently
            _cancellation = new CancellationTokenSource();

            _track = new ActionBlock<EventPage>(page => ExecutePage(page, _cancellation.Token));
        }

        public long LastEncountered { get; set; }

        public Type ViewType => _projection.Produces;

        public void QueuePage(EventPage page)
        {
            _track.Post(page);
        }

        public int QueuedPageCount => _track.InputCount;
        public ITargetBlock<IDaemonUpdate> Updater { get; set; }

        public void Dispose()
        {
            _waiters.Clear();
            _track.Complete();
        }

        public async Task ExecutePage(EventPage page, CancellationToken cancellation)
        {
            await _projection.ApplyAsync(_session, page.Streams, cancellation).ConfigureAwait(false);

            _session.QueueOperation(new EventProgressWrite(_options, _projection.Produces.FullName, page.To));

            await _session.SaveChangesAsync(cancellation).ConfigureAwait(false);

            Console.WriteLine($"Processed {page} for view {ViewType.FullName}");

            LastEncountered = page.To;

            evaluateWaiters();

            Updater?.Post(new StoreProgress(ViewType, page));
        }

        private void evaluateWaiters()
        {
            var expiredWaiters = _waiters.Where(x => x.Sequence <= LastEncountered).ToArray();
            foreach (var waiter in expiredWaiters)
            {
                waiter.Completion.SetResult(LastEncountered);
                _waiters.Remove(waiter);
            }
        }

        public Task<long> WaitUntilEventIsProcessed(long sequence)
        {
            if (LastEncountered >= sequence) return Task.FromResult(sequence);

            var waiter = new EventWaiter(sequence);
            _waiters.Add(waiter);

            return waiter.Completion.Task;
        }

        public Task Stop()
        {
            throw new NotImplementedException();
        }
    }
}