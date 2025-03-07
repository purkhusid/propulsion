namespace Propulsion.CosmosStore

open FSharp.Control
open Microsoft.Azure.Cosmos
open Serilog
open System
open System.Collections.Generic

[<NoComparison>]
type ChangeFeedObserverContext = { source : Container; group : string; epoch : int64; timestamp : DateTime; rangeId : int; requestCharge : float }

type IChangeFeedObserver =
    inherit IDisposable

    /// Callback responsible for
    /// - handling ingestion of a batch of documents (potentially offloading work to another control path)
    /// - ceding control as soon as commencement of the next batch retrieval is desired
    /// - triggering marking of progress via an invocation of `ctx.Checkpoint()` (can be immediate, but can also be deferred and performed asynchronously)
    /// NB emitting an exception will not trigger a retry, and no progress writing will take place without explicit calls to `ctx.Checkpoint`
    abstract member Ingest: context : ChangeFeedObserverContext * tryCheckpointAsync : Async<unit> * docs : IReadOnlyCollection<Newtonsoft.Json.Linq.JObject> -> Async<unit>

//// Wraps the V3 ChangeFeedProcessor and [`ChangeFeedProcessorEstimator`](https://docs.microsoft.com/en-us/azure/cosmos-db/how-to-use-change-feed-estimator)
type ChangeFeedProcessor =

    static member Start
        (   log : ILogger, monitored : Container,
            /// The non-partitioned (i.e., PartitionKey is "id") Container holding the partition leases.
            // Should always read from the write region to keep the number of write conflicts to a minimum when the sdk
            // updates the leases. Since the non-write region(s) might lag behind due to us using non-strong consistency, during
            // failover we are likely to reprocess some messages, but that's okay since processing has to be idempotent in any case
            leases : Container,
            /// Identifier to disambiguate multiple independent feed processor positions (akin to a 'consumer group')
            processorName : string,
            /// Observers to forward documents to
            observer : IChangeFeedObserver,
            ?leaseOwnerId : string,
            /// (NB Only applies if this is the first time this leasePrefix is presented)
            /// Specify `true` to request starting of projection from the present write position.
            /// Default: false (projecting all events from start beforehand)
            ?startFromTail : bool,
            /// Frequency to check for partitions without a processor. Default 1s
            ?leaseAcquireInterval : TimeSpan,
            /// Frequency to renew leases held by processors under our control. Default 3s
            ?leaseRenewInterval : TimeSpan,
            /// Duration to take lease when acquired/renewed. Default 10s
            ?leaseTtl : TimeSpan,
            /// Delay before re-polling a partition after backlog has been drained
            ?feedPollDelay : TimeSpan,
            /// Limit on items to take in a batch when querying for changes (in addition to 4MB response size limit). Default Unlimited.
            /// Max Items is not emphasized as a control mechanism as it can only be used meaningfully when events are highly regular in size.
            ?maxItems : int,
            /// Continuously fed per-partition lag information until parent Async completes
            /// callback should Async.Sleep until next update is desired
            ?reportLagAndAwaitNextEstimation,
            /// Enables reporting or other processing of Exception conditions as per <c>WithErrorNotification</c>
            ?notifyError : int -> exn -> unit,
            /// Admits customizations in the ChangeFeedProcessorBuilder chain
            ?customize) = async {

        let leaseOwnerId = defaultArg leaseOwnerId (ChangeFeedProcessor.mkLeaseOwnerIdForProcess())
        let feedPollDelay = defaultArg feedPollDelay (TimeSpan.FromSeconds 1.)
        let leaseAcquireInterval = defaultArg leaseAcquireInterval (TimeSpan.FromSeconds 1.)
        let leaseRenewInterval = defaultArg leaseRenewInterval (TimeSpan.FromSeconds 3.)
        let leaseTtl = defaultArg leaseTtl (TimeSpan.FromSeconds 10.)

        let inline s (x : TimeSpan) = x.TotalSeconds
        log.Information("ChangeFeed {processorName} Lease acquire {leaseAcquireIntervalS:n0}s ttl {ttlS:n0}s renew {renewS:n0}s feedPollDelay {feedPollDelayS:n0}s",
            processorName, s leaseAcquireInterval, s leaseTtl, s leaseRenewInterval, s feedPollDelay)
        let processorName_ =  processorName + ":"
        let leaseTokenToPartitionId (leaseToken : string) = int (leaseToken.Trim[|'"'|])
        let processor =
            let handler =
                let aux (context : ChangeFeedProcessorContext)
                        (changes : IReadOnlyCollection<Newtonsoft.Json.Linq.JObject>)
                        (checkpointAsync : Func<System.Threading.Tasks.Task>) = async {
                    let checkpoint = async { return! checkpointAsync.Invoke() |> Async.AwaitTaskCorrect }
                    try let ctx = { source = monitored; group = processorName
                                    epoch = context.Headers.ContinuationToken.Trim[|'"'|] |> int64
                                    timestamp = changes |> Seq.last |> EquinoxNewtonsoftParser.timestamp
                                    rangeId = leaseTokenToPartitionId context.LeaseToken
                                    requestCharge = context.Headers.RequestCharge }
                        return! observer.Ingest(ctx, checkpoint, changes)
                    with e ->
                        log.Error(e, "Reader {processorName}/{partitionId} Handler Threw", processorName, context.LeaseToken)
                        do! Async.Raise e }
                fun ctx chg chk ct -> Async.StartAsTask(aux ctx chg chk, cancellationToken = ct) :> System.Threading.Tasks.Task
            let acquireAsync leaseToken = log.Information("Reader {partitionId} Assigned", leaseTokenToPartitionId leaseToken); System.Threading.Tasks.Task.CompletedTask
            let releaseAsync leaseToken = log.Information("Reader {partitionId} Revoked", leaseTokenToPartitionId leaseToken); System.Threading.Tasks.Task.CompletedTask
            let notifyError =
                notifyError
                |> Option.defaultValue (fun i ex -> log.Error(ex, "Reader {partitionId} error", i))
                |> fun f -> fun leaseToken ex -> f (leaseTokenToPartitionId leaseToken) ex; System.Threading.Tasks.Task.CompletedTask
            monitored
                .GetChangeFeedProcessorBuilderWithManualCheckpoint(processorName_, Container.ChangeFeedHandlerWithManualCheckpoint handler)
                .WithLeaseContainer(leases)
                .WithPollInterval(feedPollDelay)
                .WithLeaseConfiguration(acquireInterval = Nullable leaseAcquireInterval, expirationInterval = Nullable leaseTtl, renewInterval = Nullable leaseRenewInterval)
                .WithInstanceName(leaseOwnerId)
                .WithLeaseAcquireNotification(Container.ChangeFeedMonitorLeaseAcquireDelegate acquireAsync)
                .WithLeaseReleaseNotification(Container.ChangeFeedMonitorLeaseReleaseDelegate releaseAsync)
                .WithErrorNotification(Container.ChangeFeedMonitorErrorDelegate notifyError)
                |> fun b -> if startFromTail = Some true then b else let minTime = DateTime.MinValue in b.WithStartTime(minTime.ToUniversalTime()) // fka StartFromBeginning
                |> fun b -> match maxItems with Some mi -> b.WithMaxItems(mi) | None -> b
                |> fun b -> match customize with Some c -> c b | None -> b
                |> fun b -> b.Build()
        match reportLagAndAwaitNextEstimation with
        | None -> ()
        | Some lagMonitorCallback ->
            let estimator = monitored.GetChangeFeedEstimator(processorName_, leases)
            let rec emitLagMetrics () = async {
                let feedIteratorMap (map : 't -> 'u) (query : FeedIterator<'t>) : AsyncSeq<'u> =
                    let rec loop () : AsyncSeq<'u> = asyncSeq {
                        if not query.HasMoreResults then return None else
                        let! ct = Async.CancellationToken
                        let! (res : FeedResponse<'t>) = query.ReadNextAsync(ct) |> Async.AwaitTaskCorrect
                        for x in res do yield map x
                        if query.HasMoreResults then
                            yield! loop () }
                    // earlier versions, such as 3.9.0, do not implement IDisposable; see linked issue for detail on when SDK team added it
                    use __ = query // see https://github.com/jet/equinox/issues/225 - in the Cosmos V4 SDK, all this is managed IAsyncEnumerable
                    loop ()
                let! leasesState =
                    estimator.GetCurrentStateIterator()
                    |> feedIteratorMap (fun s -> leaseTokenToPartitionId s.LeaseToken, s.EstimatedLag)
                    |> AsyncSeq.toArrayAsync
                do! lagMonitorCallback (Seq.sortBy fst leasesState |> List.ofSeq)
                return! emitLagMetrics () }
            let! _ = Async.StartChild(emitLagMetrics ()) in ()
        do! processor.StartAsync() |> Async.AwaitTaskCorrect
        return processor }
    static member private mkLeaseOwnerIdForProcess() =
        // If k>1 processes share an owner id, then they will compete for same partitions.
        // In that scenario, redundant processing happen on assigned partitions, but checkpoint will process on only 1 consumer.
        // Including the processId should eliminate the possibility that a broken process manager causes k>1 scenario to happen.
        // The only downside is that upon redeploy, lease expiration / TTL would have to be observed before a consumer can pick it up.
        let processName = System.Reflection.Assembly.GetEntryAssembly().GetName().Name
        let processId = System.Diagnostics.Process.GetCurrentProcess().Id
        let hostName = System.Environment.MachineName
        sprintf "%s-%s-%d" hostName processName processId
