namespace Dmg.Topics
open System
open System.Collections.Generic
open System.Collections.Concurrent
open System.Collections.Immutable
open System.Threading
open System.Threading.Tasks
open Dmg


type internal KOffset = Confluent.Kafka.Offset
type internal KPartition = Confluent.Kafka.Partition
type internal KTopicPartition = Confluent.Kafka.TopicPartition
type internal KWatermarkOffsets = Confluent.Kafka.WatermarkOffsets
type internal KISerializer<'T> = Confluent.Kafka.ISerializer<'T>
type internal KIDeserializer<'T> = Confluent.Kafka.IDeserializer<'T>
type internal KSerializationContext = Confluent.Kafka.SerializationContext
type internal KIConsumer<'K,'V> = Confluent.Kafka.IConsumer<'K,'V>
type internal KConsumerBuilder<'K,'V> = Confluent.Kafka.ConsumerBuilder<'K,'V>
type internal KConsumerConfig = Confluent.Kafka.ConsumerConfig
type internal KIProducer<'K,'V> = Confluent.Kafka.IProducer<'K,'V>
type internal KProducerBuilder<'K,'V> = Confluent.Kafka.ProducerBuilder<'K,'V>
type internal KProducerConfig = Confluent.Kafka.ProducerConfig
type internal KMessage<'K,'V> = Confluent.Kafka.Message<'K,'V>

module Serialize =
    open Google.Protobuf
    open System.Reflection

    let guidS = { new KISerializer<UUID> with
        override _.Serialize (data, ctx) =
            data.GetBytes
    }
    let guidD = { new KIDeserializer<UUID> with
        override _.Deserialize (data, isNull, ctx) =
            match UUID.New (data.ToArray()) with
            | Ok u -> u
            | Error e -> raise (Exception(e))
    }
    let guid = guidS, guidD

    let messageS<'T when 'T :> IMessage> = { new KISerializer<'T> with
        override _.Serialize (data,ctx) =
            data.ToByteArray ()
    }

    let messageD< 'T when 'T :> IMessage> =
        {
            new KIDeserializer<'T> with
                override _.Deserialize (data, isNull, ctx) =
                    // Modules with the same name as a type in the same file get mangled by appending
                    // "Module" - i.e. Foo -> FooModule
                    let tType = typeof<'T>
                    let tModule =
                        tType.Assembly.GetType(tType.FullName + "Module")
                    let msg : 'T =
                        tModule
                            .GetMethod("empty", BindingFlags.Public ||| BindingFlags.Static)
                            .Invoke(null, [||]) :?> 'T
                    msg.MergeFrom data
                    msg
        }

    let message< 'T when 'T :> IMessage> = messageS<'T>, messageD<'T>

    let optionalMessageS<'T when 'T :> IMessage> = { new KISerializer<'T option> with
        override _.Serialize (dataOpt, ctx) =
            match dataOpt with
            | Some data -> messageS.Serialize (data, ctx)
            | None -> null
    }

    let optionalMessageD<'T when 'T :> IMessage> = { new KIDeserializer<'T option> with
        override _.Deserialize (data, isNull, ctx) =
            if isNull then None
            else Some (messageD.Deserialize (data, isNull, ctx))
    }

    let optionalMessage<'T when 'T :> IMessage> = optionalMessageS<'T>, optionalMessageD<'T>

type TopicName = string
type Partition = | Any | Partition of int
type Offset =
    | Beginning
    | End
    | Stored
    | Unset
    | Offset of int64

    member this.Value =
        match this with
        | Offset o -> o
        | _ -> raise (InvalidOperationException("Offset represents a special value"))

module Partition =
    let fromConfluent (kp: KPartition) =
        if kp = KPartition.Any then Any
        else Partition kp.Value

    let toConfluent partition =
        match partition with
        | Partition p -> KPartition p
        | Any -> KPartition.Any

module Offset =
    let fromConfluent (koffset: KOffset) =
        if koffset = KOffset.Beginning then Beginning
        elif koffset = KOffset.End then End
        elif koffset = KOffset.Stored then Stored
        elif koffset = KOffset.Unset then Unset
        else Offset koffset.Value

    let toConfluent offset =
        match offset with
        | Beginning -> KOffset.Beginning
        | End -> KOffset.End
        | Stored -> KOffset.Stored
        | Unset -> KOffset.Unset
        | Offset o -> KOffset o

type TopicResult<'Key, 'Value> =
    {   key: 'Key
        value: 'Value
        position: TopicName * Partition * Offset }

type MissingTopicBehavior = AutoCreate of nAvailableBrokers:int16 | FailIfNotExists

type ITopicWriter<'Key, 'Value> =
    inherit IDisposable
    abstract member Write : key:'Key * value:'Value -> Async<TopicResult<'Key, 'Value>>

type ITopicReader<'Key, 'Value> =
    inherit IDisposable
    abstract member Read : unit -> Async<TopicResult<'Key,'Value>>
    abstract member QueryWatermarkOffsets : topicPartition: (TopicName * Partition) * timeout: TimeSpan -> KWatermarkOffsets
    abstract member Position : topic: TopicName * partition: Partition -> Offset

type TopicKind = | EventTopic | StateTopic

type TopicDefinition<'Key, 'Value> = {
    kind: TopicKind
    name: string
    keySerializer: (KISerializer<'Key> * KIDeserializer<'Key>)
    valueSerializer: (KISerializer<'Value> * KIDeserializer<'Value>)
}

open Google.Protobuf

[<Sealed; AbstractClass>]
type TopicDefinition =
    static member Create<'Value when 'Value :> IMessage<'Value>> (name, kind) : TopicDefinition<UUID, 'Value> = {
        kind = kind
        name = name
        keySerializer = Serialize.guid
        valueSerializer = Serialize.message<'Value>
    }
    static member CreateOptional<'Value when 'Value :> IMessage<'Value>> (name, kind) : TopicDefinition<UUID, 'Value option> = {
        kind = kind
        name = name
        keySerializer = Serialize.guid
        valueSerializer = Serialize.optionalMessage<'Value>
    }
    static member Create<'Value> (name, kind, valueSerializer) : TopicDefinition<UUID, 'Value> = {
        kind = kind
        name = name
        keySerializer = Serialize.guid
        valueSerializer = valueSerializer
    }

module TopicDefinition =
    let toTopicSpecification nAvailableBrokers topicDef =
        let replicationFactor = min 3s nAvailableBrokers
        let minInsyncReplicas = (replicationFactor / 2s) + 1s
        let ts =
            match topicDef.kind with
            | EventTopic ->
                new Confluent.Kafka.Admin.TopicSpecification(
                    Name = topicDef.name,
                    NumPartitions = 1,
                    ReplicationFactor = replicationFactor)
            | StateTopic ->
                new Confluent.Kafka.Admin.TopicSpecification(
                    Name = topicDef.name,
                    NumPartitions = 1,
                    ReplicationFactor = replicationFactor)
        ts.Configs <- Dictionary [KeyValuePair("retention.ms", "-1"); KeyValuePair("min.insync.replicas", string minInsyncReplicas)]
        ts

type ITopicServerConnection =
    abstract member BootstrapServers : string with get, set
    abstract member OpenReaderAsync : topicDef:TopicDefinition<'Key, 'Value> * ?startOffset: (TopicName * Partition * Offset) -> Async<ITopicReader<'Key, 'Value>>
    abstract member OpenWriterAsync : topicDef:TopicDefinition<'Key, 'Value> * ?missingTopicBehavior:MissingTopicBehavior -> Async<ITopicWriter<'Key, 'Value>>

type internal TopicData<'Key, 'Value>(serializeKey: (KISerializer<'Key> * KIDeserializer<'Key>), serializeValue: (KISerializer<'Value> * KIDeserializer<'Value>)) =
    //let mutable data = ImmutableList.Create<byte[]*byte[]>()
    let mutable dataLock = new Object()
    let data = List<byte[] option * byte[] option>()

    let _itemAddedEvent = new Event<EventArgs>()
    [<CLIEvent>]
    member _.ItemAddedEvent = _itemAddedEvent.Publish

    member _.Count = data.Count

    member _.Add (key, value) =
        let keyBytes = (fst serializeKey).Serialize (key, KSerializationContext.Empty) |> Option.ofObj
        let valueBytes = (fst serializeValue).Serialize (value, KSerializationContext.Empty) |> Option.ofObj
        let newCount =
            lock dataLock (fun () ->
                data.Add (keyBytes, valueBytes)   
                data.Count
            )
        _itemAddedEvent.Trigger (EventArgs())
        newCount

    member _.TryItem i =
        lock dataLock (fun () ->
            if i < data.Count then Some (data[i])
            else None
        )
        |> Option.bind (fun (keyBytes, valueBytes) ->
            let k = keyBytes |> Option.defaultValue [||]
            let v = valueBytes |> Option.defaultValue [||]
            let key = (snd serializeKey).Deserialize (ReadOnlySpan<_>(k), Option.isNone keyBytes, KSerializationContext.Empty)
            let value = (snd serializeValue).Deserialize (ReadOnlySpan<_>(v), Option.isNone valueBytes, KSerializationContext.Empty)
            Some (key, value)
        )

type internal InMemoryTopicWriter<'Key, 'Value>(getData, topicDef: TopicDefinition<'Key, 'Value>) =
    let (topicData: TopicData<'Key, 'Value>) = getData topicDef

    member _.Write (key: 'Key, value: 'Value) = async {
        return topicData.Add (key, value)
    }

    member _.Dispose () = ()

    interface ITopicWriter<'Key, 'Value> with
        override this.Write (key, value) = async {
            let! newTopicCount = this.Write (key, value)
            return { key = key; value = value; position = topicDef.name, Partition 0, (Offset (int64 newTopicCount - 1L)) }
        }
        override this.Dispose () = this.Dispose ()

type internal InMemoryTopicReader<'Key, 'Value>(getData, topicDef: TopicDefinition<'Key, 'Value>, ?startPosition) =
    let (topicData : TopicData<'Key, 'Value>) = getData topicDef
    let mutable headIndex =
        match startPosition with
        | Some (_, _, Offset.Beginning) | None -> 0
        | Some (_, _, Offset i) -> int i
        | _ -> raise (NotImplementedException())

    // Use an AutoResetEvent instead of Async.AwaitEvent, because the former cannot miss events between calls (while processing the last one), while
    // the latter can, since it immediately detaches the event handler after using it (under the hood)
    let mutable blockingTcs = ImmutableList<TaskCompletionSource<unit>>.Empty

    let notifyAllWaitersItemAdded () =
        let blockingTcsLocal = Interlocked.Exchange (&blockingTcs, ImmutableList<_>.Empty)
        for tcs in blockingTcsLocal do
            tcs.SetResult ()

    let disposeEventHandler =
        topicData.ItemAddedEvent.Subscribe (fun _ ->
            notifyAllWaitersItemAdded ()
        )

    let mutable disposed = false

    member this.Read (ct: CancellationToken) = task {
        match topicData.TryItem headIndex with
        | Some (k,v) ->
            let hi = headIndex
            headIndex <- headIndex + 1
            return (k,v,hi)
        | None ->
            let tcs = TaskCompletionSource<unit>()
            ImmutableInterlocked.Update (&blockingTcs, (fun (xs: ImmutableList<_>) -> xs.Add tcs)) |> ignore
            do! tcs.Task
            return! this.Read ct
    }

    member _.Dispose disposing =
        if not disposed then
            if disposing then
                disposeEventHandler.Dispose ()
            disposed <- true

    member _.Dispose () = ()

    interface ITopicReader<'Key, 'Value> with
        override this.Read () = async {
            let! ct = Async.CancellationToken
            let! (k,v,hi) = Async.AwaitTask (this.Read ct)
            return { key = k; value = v; position = (topicDef.name, Partition 0, Offset hi) }
        }
        override this.Dispose () = this.Dispose ()
        override _.QueryWatermarkOffsets (_, _) = KWatermarkOffsets(KOffset 0, KOffset topicData.Count)
        override _.Position (_, _) =
            if headIndex = 0 then Beginning
            else Offset headIndex

type InMemoryTopicServerConnection(bootstrapServers) =
    let topics = new ConcurrentDictionary<string, obj>()

    new() = InMemoryTopicServerConnection("")
    
    member internal _.GetData (topicDef: TopicDefinition<'Key, 'Value>) : TopicData<'Key, 'Value> =
        topics.GetOrAdd (topicDef.name, (fun key -> new TopicData<'Key, 'Value>(topicDef.keySerializer, topicDef.valueSerializer) :> obj)) :?> TopicData<'Key, 'Value>

    member this.OpenReaderAsync<'Key, 'Value> (topicDef, ?startPosition) = async {
        return new InMemoryTopicReader<_,_>(this.GetData, topicDef, ?startPosition = startPosition) :> ITopicReader<'Key, 'Value>
    }
    member this.OpenWriterAsync<'Key, 'Value> topicDef = async { return new InMemoryTopicWriter<_,_>(this.GetData, topicDef) :> ITopicWriter<'Key, 'Value> }

    /// Directly populate a topic with messages. Useful for testing purposes.
    member this.AddMessagesToTopic (topicDef, messages) =
        let td = this.GetData topicDef
        for (k,v) in messages do
            td.Add (k,v) |> ignore

    interface ITopicServerConnection with
        override val BootstrapServers = bootstrapServers with get, set
        override this.OpenReaderAsync<'Key, 'Value> (topicDef, ?startPosition) = this.OpenReaderAsync<'Key, 'Value> (topicDef, ?startPosition = startPosition)
        override this.OpenWriterAsync<'Key, 'Value> (topicDef, _) = this.OpenWriterAsync<'Key, 'Value> topicDef

[<AutoOpen>]
module Extensions2 =
    type ITopicWriter<'K,'V> with
        member this.AsyncWrite kv = this.Write kv
    type ITopicReader<'K,'V> with
        member this.AsyncRead () = this.Read ()
    type ITopicServerConnection with
        member this.AsyncOpenReader<'K,'V> (topicDef: TopicDefinition<'K,'V>, ?startOffset) = this.OpenReaderAsync (topicDef, ?startOffset = startOffset)
        member this.AsyncOpenWriter<'K,'V> (topicDef: TopicDefinition<'K,'V>, ?missingTopicBehavior) = this.OpenWriterAsync (topicDef, ?missingTopicBehavior = missingTopicBehavior)

type IKafkaHelpers =
    abstract member DoesTopicExist<'Entity> : bootstrapServers: string -> nAvailableBrokers: int16 -> topicDef: TopicDefinition<UUID, 'Entity> -> Async<bool>

module KafkaHelpers =
    let private makeTopicExistsClient bootstrapServers nAvailableBrokers topicDef = async {
        // There are two ways to determine if a topic already exists:
        // A) Use GetMetadata(string topicName) to look at an individual topic. If result is a result with
        //      an error status, the topic does not exist.
        // B) Use GetMetadata() to get a list of all topics, and see whether or not our topic of interest
        //      is in that list.
        // The first version of this code used approach A. However it turns out this only works when
        //      auto.create.topics.enable is set to false in Kafka, and it turns out it's often set to
        //      false by default, which has the effect of creating the topic on the GetMetadata
        //      call with all the wrong defaults... Approach B works regardless of the value of
        //      auto.create.topics.enable, so that is the approach you see here.
        
        // TODO: would be more ideal to retrieve the list of topics only once instead of fetching
        // the whole list every time we want to check whether or not one particular topic exists
        let topicSpec = TopicDefinition.toTopicSpecification nAvailableBrokers topicDef
        let! ctk = Async.CancellationToken
        let client =
            Confluent
                .Kafka
                .AdminClientBuilder(Confluent.Kafka.AdminClientConfig(BootstrapServers = bootstrapServers))
                .Build()
        let metadata =
            client.GetMetadata(TimeSpan.FromSeconds(10))
        let topicExists =
            metadata.Topics.Exists(fun (t: Confluent.Kafka.TopicMetadata) -> t.Topic = topicDef.name)
        return {| topicExists = topicExists; client = client; topicSpec = topicSpec |}
    }

    let doesTopicExist bootstrapServers nAvailableBrokers topicDef = async {
        let! data = makeTopicExistsClient bootstrapServers nAvailableBrokers topicDef
        use _ = data.client
        return data.topicExists
    }
    
    let createTopicIfNotExists bootstrapServers nAvailableBrokers topicDef : Async<unit> = async {
        let! data = makeTopicExistsClient bootstrapServers nAvailableBrokers topicDef
        use client = data.client
        if not data.topicExists then
            printfn "Topic not found; creating..."
            // ugh, can't pass in a CancellationToken to this...
            do! Async.AwaitTask(client.CreateTopicsAsync([| data.topicSpec |]))
            printfn "Successfully created topic"
        else
            printfn "Topic Exists..."
    }

    let impl = { new IKafkaHelpers with member this.DoesTopicExist b n t = doesTopicExist b n t }

type KafkaTopicWriter<'Key,'Value>(topicName: string, producer: KIProducer<'Key,'Value>) =
    //let cts = new CancellationTokenSource()
    let mutable disposed = false

    new(topicName, builder: KProducerBuilder<_,_>) = new KafkaTopicWriter<_,_>(topicName, builder.Build())
    new(topicName, config: KProducerConfig, ?keySerializer: KISerializer<'Key>, ?valueSerializer: KISerializer<'Value>) =
        let pb = new KProducerBuilder<_,_>(config)
        keySerializer |> Option.iter (pb.SetKeySerializer >> ignore)
        valueSerializer |> Option.iter (pb.SetValueSerializer >> ignore)
        new KafkaTopicWriter<_,_>(topicName, pb.Build())

    static member OpenAsync(topicSpec, config: KProducerConfig, ?missingTopicBehavior, ?keySerializer, ?valueSerializer, ?autoCreate) = async {
        match missingTopicBehavior |> Option.defaultValue FailIfNotExists with
        | AutoCreate nAvailableBrokers ->
            do! KafkaHelpers.createTopicIfNotExists config.BootstrapServers nAvailableBrokers topicSpec
        | FailIfNotExists -> ()
        return new KafkaTopicWriter<'Key,'Value>(topicSpec.name, config, ?keySerializer = keySerializer, ?valueSerializer = valueSerializer)
    }

    member _.Write (key, value) = async {
        if disposed then raise (new ObjectDisposedException(nameof(KafkaTopicWriter)))
        let! ct = Async.CancellationToken
        let! result = Async.AwaitTask (producer.ProduceAsync (topicName, KMessage(Key = key, Value = value), ct))
        return { key = key; value = value; position = (result.Topic, Partition.fromConfluent result.Partition, Offset.fromConfluent result.Offset) }
    }

    interface ITopicWriter<'Key,'Value> with
        override this.Write (key, value) = this.Write (key, value)
        override _.Dispose () =
            if not disposed then
                //cts.Cancel ()
                //cts.Dispose ()
                producer.Dispose ()

type KafkaTopicReader<'Key,'Value>(topicName: string, consumer: KIConsumer<'Key,'Value>) =
    let channel = Channels.Channel.CreateBounded<TopicResult<'Key,'Value>>(1)
    let cts = new CancellationTokenSource()
    let mutable isRunning = false
    let mutable disposed = false

    new(topicName, builder: KConsumerBuilder<'Key,'Value>) = new KafkaTopicReader<_,_>(topicName, builder.Build())
    new(topicName, config: KConsumerConfig, ?keyDeserializer: KIDeserializer<'Key>, ?valueDeserializer: KIDeserializer<'Value>) =
        let cb = new KConsumerBuilder<_,_>(config)
        keyDeserializer |> Option.iter (cb.SetKeyDeserializer >> ignore)
        valueDeserializer |> Option.iter (cb.SetValueDeserializer >> ignore)        
        new KafkaTopicReader<_,_>(topicName, cb.Build())

    static member OpenAsync(topicSpec, config: KConsumerConfig, ?keyDeserializer, ?valueDeserializer, ?startPosition) = async {      
        let reader = new KafkaTopicReader<'Key,'Value>(topicSpec.name, config, ?keyDeserializer = keyDeserializer, ?valueDeserializer = valueDeserializer)
        reader.Start (defaultArg startPosition (topicSpec.name, Partition 0, Beginning))
        return reader
    }

    member internal _.ReadLoop () = async {
        isRunning <- true
        try
            while true do
                try 
                    //System.Console.WriteLine $"[Read thread] [Thread #{Thread.CurrentThread.ManagedThreadId}] Waiting"
                    let result = consumer.Consume cts.Token
                    
                    if result.IsPartitionEOF then
                        // not sure how this is to be handled - if you ever run into this, you might know enough to implement this properly
                        raise (NotImplementedException("This case should be handled somehow"))
                    else
                        //System.Console.WriteLine $"[Read thread] [Thread #{Thread.CurrentThread.ManagedThreadId}] Read; Offset = {result.Offset}; Partition = {result.TopicPartition.Partition.Value}"
                        let result' =
                            {   key = result.Message.Key
                                value = result.Message.Value
                                position = (result.Topic, Partition.fromConfluent result.Partition, Offset.fromConfluent result.Offset) }
                        //consumer.Commit result
                        let t = channel.Writer.WriteAsync (result', cts.Token)
                        do! Async.AwaitTask (t.AsTask())
                with e ->
                    printfn "%A" e
        with e ->
            printfn "%A" e
            channel.Writer.Complete e
    }

    member private this.Start (startPosition: string * Partition * Offset) =
        let (t,p,o) = startPosition
        consumer.Assign (Confluent.Kafka.TopicPartitionOffset(t, Partition.toConfluent p, Offset.toConfluent o))
        if disposed then raise (ObjectDisposedException(nameof(KafkaTopicReader)))
        if isRunning then invalidOp "Already running the read loop"
        Async.Start (this.ReadLoop (), cts.Token)

    interface ITopicReader<'Key,'Value> with
        override _.Read () : Async<TopicResult<'Key,'Value>> = async {
            let! ct = Async.CancellationToken
            return! Async.AwaitTask (channel.Reader.ReadAsync(cancellationToken = ct).AsTask())
        }

        override _.Position (t, p) =
            Offset.fromConfluent (consumer.Position (KTopicPartition(t, Partition.toConfluent p)))

        override _.QueryWatermarkOffsets ((topic, partition), timeout) =
            consumer.QueryWatermarkOffsets (KTopicPartition(topic, Partition.toConfluent partition), timeout)

        override _.Dispose () =
            if not disposed then
                cts.Cancel ()
                cts.Dispose ()
                channel.Writer.Complete ()
                consumer.Dispose ()
                disposed <- true

type KafkaServerConnection(bootstrapServers: string) =
    member _.OpenReaderAsync<'K,'V> (topicDef: TopicDefinition<'K,'V>, ?startPosition) = async {
        let c = KConsumerConfig(
            GroupId = UUID.New().ToString(),
            EnableAutoCommit = true,
            AutoOffsetReset = Confluent.Kafka.AutoOffsetReset.Error,
            ApiVersionRequest = true,
            BootstrapServers = bootstrapServers,
            //AutoCommitIntervalMs = 0,
            EnableAutoOffsetStore = false,
            EnablePartitionEof = false,
            TopicMetadataRefreshIntervalMs = 120000
        )
        let! result = KafkaTopicReader.OpenAsync(
            topicDef,
            c,
            keyDeserializer = (topicDef.keySerializer |> snd),
            valueDeserializer = (topicDef.valueSerializer |> snd),
            ?startPosition = startPosition
        )
        return result :> ITopicReader<'K,'V>
    }
    member _.OpenWriterAsync<'K,'V> (topicDef: TopicDefinition<'K,'V>, ?missingTopicBehavior) = async {
        let! result = KafkaTopicWriter.OpenAsync(
            topicDef,
            KProducerConfig(BootstrapServers = bootstrapServers),
            ?missingTopicBehavior = missingTopicBehavior,
            keySerializer = (topicDef.keySerializer |> fst),
            valueSerializer = (topicDef.valueSerializer |> fst)
        )
        return result :> ITopicWriter<'K,'V>
    }
    
    interface ITopicServerConnection with
        override val BootstrapServers = bootstrapServers with get, set
        override this.OpenReaderAsync<'K,'V> (topicDef, ?startPosition) = this.OpenReaderAsync<'K,'V> (topicDef, ?startPosition = startPosition)
        override this.OpenWriterAsync<'K,'V> (topicDef, ?missingTopicBehavior) = this.OpenWriterAsync<'K,'V> (topicDef, ?missingTopicBehavior = missingTopicBehavior)
