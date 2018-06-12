// Copyright (c) 2007-2018 ppy Pty Ltd <contact@ppy.sh>.
// Licensed under the MIT Licence - https://raw.githubusercontent.com/ppy/osu-elastic-indexer/master/LICENCE

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Elasticsearch.Net;
using MySql.Data.MySqlClient;
using Nest;

namespace osu.ElasticIndexer
{
    public class HighScoreIndexer<T> : IIndexer where T : Model
    {
        public string Name { get; set; }
        public long? ResumeFrom { get; set; }
        public string Suffix { get; set; }

        private static readonly ElasticClient elasticClient;

        private readonly ConcurrentBag<Task<IBulkResponse>> pendingTasks = new ConcurrentBag<Task<IBulkResponse>>();

        // Queues
        private readonly BlockingCollection<List<T>> defaultQueue = new BlockingCollection<List<T>>(AppSettings.QueueSize);
        private readonly BlockingCollection<List<T>> retryQueue = new BlockingCollection<List<T>>(AppSettings.QueueSize);
        private readonly BlockingCollection<List<T>>[] queues;

        private int waitingCount => pendingTasks.Count + defaultQueue.Count + retryQueue.Count;

        // throttle control
        private int delay;

        static HighScoreIndexer()
        {
            elasticClient = new ElasticClient
            (
                new ConnectionSettings(new Uri(AppSettings.ElasticsearchHost))
            );
        }

        public HighScoreIndexer()
        {
            queues = new [] { retryQueue, defaultQueue };
        }

        public void Run()
        {
            var index = findOrCreateIndex(Name);
            // find out if we should be resuming
            var resumeFrom = ResumeFrom ?? IndexMeta.GetByName(index)?.LastId;

            Console.WriteLine();
            Console.WriteLine($"{typeof(T)}, index `{index}`, chunkSize `{AppSettings.ChunkSize}`, resume `{resumeFrom}`");
            Console.WriteLine();

            var start = DateTime.Now;
            using (var consumerTask = consumerLoop(index))
            using (var producerTask = producerLoop(resumeFrom))
            {
                producerTask.Wait();
                endingTask();
                consumerTask.Wait();

                var count = producerTask.Result;
                var span = DateTime.Now - start;
                Console.WriteLine($"{count} records took {span}");
                if (count > 0) Console.WriteLine($"{count / span.TotalSeconds} records/s");
            }

            updateAlias(Name, index);

            int workerThreads;
            int completionPorts;
            ThreadPool.GetAvailableThreads(out workerThreads, out completionPorts);
            Console.WriteLine($"available threads: {workerThreads}, completion ports: {completionPorts}");
        }

        private void endingTask()
        {
            Task.WhenAll(pendingTasks).Wait();
            // Spin until queue and pendingTasks are empty.
            while (waitingCount > 0)
            {
                var delayDuration = Math.Max(waitingCount, delay) * 100;
                Console.WriteLine($@"Waiting for queues to empty...({defaultQueue.Count}) ({retryQueue.Count}) ({pendingTasks.Count}) delay for {delayDuration} ms");

                Task.Delay(delayDuration).Wait();
                Task.WhenAll(pendingTasks).Wait();
            }

            retryQueue.CompleteAdding();
        }

        private Task consumerLoop(string index)
        {
            return Task.Factory.StartNew(() =>
            {
                while (!defaultQueue.IsCompleted || !retryQueue.IsCompleted)
                {
                    if (delay > 0) Task.Delay(delay * 100).Wait();
                    waitIfTooBusy();

                    List<T> chunk;

                    try
                    {
                        BlockingCollection<List<T>>.TakeFromAny(queues, out chunk);
                    }
                    catch (ArgumentException ex)
                    {
                        // queue was marked as completed while blocked.
                        Console.WriteLine(ex.Message);
                        continue;
                    }

                    var bulkDescriptor = new BulkDescriptor().Index(index).IndexMany(chunk);

                    Task<IBulkResponse> task = elasticClient.BulkAsync(bulkDescriptor);
                    pendingTasks.Add(task);

                    task.ContinueWith(t =>
                    {
                        // wait until after any requeueing needs to be done before removing the task.
                        handleResult(t.Result, chunk);
                        pendingTasks.TryTake(out t);
                        t.Dispose();
                    });

                    // TODO: Less blind-fire update.
                    // I feel like this is in the wrong place...
                    IndexMeta.Update(new IndexMeta
                    {
                        Index = index,
                        Alias = Name,
                        LastId = chunk.Last().CursorValue,
                        UpdatedAt = DateTimeOffset.UtcNow
                    });

                    if (delay > 0) Interlocked.Decrement(ref delay);
                }
            }, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach);
        }

        private Task<long> producerLoop(long? resumeFrom)
        {
            return Task.Factory.StartNew(() =>
            {
                long count = 0;

                using (var dbConnection = new MySqlConnection(AppSettings.ConnectionString))
                {
                    dbConnection.Open();
                    // TODO: retry needs to be added on timeout
                    var chunks = Model.Chunk<T>(dbConnection, AppSettings.ChunkSize, resumeFrom);
                    foreach (var chunk in chunks)
                    {
                        defaultQueue.Add(chunk);
                        count += chunk.Count;
                    }
                }

                defaultQueue.CompleteAdding();
                Console.WriteLine("Mark queue as completed.");

                return count;
            },TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach);
        }

        private void waitIfTooBusy()
        {
            // too many pending responses, wait and let them be handled.
            if (pendingTasks.Count > AppSettings.QueueSize * 2) {
                Console.WriteLine($"Too many pending responses ({pendingTasks.Count}), waiting...");
                pendingTasks.FirstOrDefault()?.Wait();
            }
        }

        /// <summary>
        /// Attemps to find the matching index or creates a new one.
        /// </summary>
        /// <param name="name">Name of the alias to find the matching index for.</param>
        /// <returns>Name of index found or created.</returns>
        private string findOrCreateIndex(string name)
        {
            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine($"Find or create index for `{name}`...");
            var metas = IndexMeta.GetByAlias(name).ToList();
            var indices = elasticClient.GetIndicesPointingToAlias(name);

            string index = metas.FirstOrDefault(m => indices.Contains(m.Index))?.Index;
            // 3 cases are handled:
            // 1. Index was already aliased and has tracking information; likely resuming from a completed job.
            if (index != null)
            {
                Console.WriteLine($"Found matching aliased index `{index}`.");
                return index;
            }

            // 2. Index has not been aliased and has tracking information; likely resuming from an imcomplete job.
            index = metas.FirstOrDefault()?.Index;
            if (index != null)
            {
                Console.WriteLine($"Found previous index `{index}`.");
                return index;
            }

            // 3. Not aliased and no tracking information; likely starting from scratch
            var suffix = Suffix ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            index = $"{name}_{suffix}";

            Console.WriteLine($"Creating `{index}` for `{name}`.");
            // create by supplying the json file instead of the attributed class because we're not
            // mapping every field but still want everything for _source.
            var json = File.ReadAllText(Path.GetFullPath("schemas/high_scores.json"));
            elasticClient.LowLevel.IndicesCreate<DynamicResponse>(index, json);

            return index;

            // TODO: cases not covered should throw an Exception (aliased but not tracked, etc).
        }

        private void handleResult(IBulkResponse response, List<T> chunk)
        {
            if (response.ItemsWithErrors.All(item => item.Status != 429)) return;

            Interlocked.Increment(ref delay);
            retryQueue.Add(chunk);

            Console.WriteLine($"Server returned 429, requeued chunk with lastId {chunk.Last().CursorValue}");
        }

        private void updateAlias(string alias, string index)
        {
            Console.WriteLine($"Updating `{alias}` alias to `{index}`...");

            var aliasDescriptor = new BulkAliasDescriptor();
            var oldIndices = elasticClient.GetIndicesPointingToAlias(alias);

            foreach (var oldIndex in oldIndices)
                aliasDescriptor.Remove(d => d.Alias(alias).Index(oldIndex));

            aliasDescriptor.Add(d => d.Alias(alias).Index(index));

            Console.WriteLine(elasticClient.Alias(aliasDescriptor));

            // TODO: cleanup unaliased indices.
        }
    }
}
