// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using System.Threading;
using Dapper;
using McMaster.Extensions.CommandLineUtils;
using MySqlConnector;

namespace osu.ElasticIndexer.Commands
{
    [Command("all", Description = "Pumps scores through the queue for processing")]
    public class PumpAllScoresCommand : ProcessorCommandBase
    {
        [Option("--delay", Description = "Delay in milliseconds between reading chunks")]
        public int Delay { get; set; }

        [Option("--from", Description = "Score id to resume from")]
        public long? From { get; set; }

        [Option("--switch", Description = "Update the configured schema in redis after completing")]
        public bool Switch { get; set; }

        [Option("--verbose", Description = "Fill your console with text")]
        public bool Verbose { get; set; }

        private CancellationToken cancellationToken;

        public int OnExecute(CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(AppSettings.Schema))
                throw new MissingSchemaException();

            this.cancellationToken = cancellationToken;

            var redis = new Redis();
            var currentSchema = redis.GetSchemaVersion();
            Console.WriteLine(ConsoleColor.Green, $"Current schema version is: {currentSchema}");
            Console.WriteLine(ConsoleColor.Green, $"Pushing to queue with schema: {AppSettings.Schema}");

            if (Switch && currentSchema == AppSettings.Schema)
                Console.WriteLine(ConsoleColor.Yellow, "Queue watchers will not update the alias if schema does not change!");

            var startTime = DateTimeOffset.Now;
            Console.WriteLine(ConsoleColor.Cyan, $"Start read: {startTime}");

            var lastId = queueScores(From);

            var endTime = DateTimeOffset.Now;
            Console.WriteLine(ConsoleColor.Cyan, $"End read: {endTime}, time taken: {endTime - startTime}");

            if (Switch)
            {
                redis.SetSchemaVersion(AppSettings.Schema);
                Console.WriteLine(ConsoleColor.Yellow, $"Schema version set to {AppSettings.Schema}, queueing scores > {lastId}");
                queueScores(lastId);

                var switchEndTime = DateTimeOffset.Now;
                Console.WriteLine(ConsoleColor.Cyan, $"End read after switch: {switchEndTime}, time taken: {switchEndTime - startTime}");
            }

            return 0;
        }

        private long? queueScores(long? from)
        {
            var chunks = ElasticModel.Chunk<SoloScore>(AppSettings.BatchSize, from);
            SoloScore? last = null;

            using (var dbConnection = new MySqlConnection(AppSettings.ConnectionString))
            {
                dbConnection.Open();

                foreach (var scores in chunks)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    var beatmapIds = scores.Select(score => score.beatmap_id);

                    string query = "SELECT beatmap_id, playmode FROM osu_beatmaps WHERE beatmap_id in @beatmapIds";

                    var playmodeLookup = dbConnection.Query(query, new { beatmapIds }).ToDictionary(
                        row => (uint)row.beatmap_id,
                        row => (int)row.playmode
                    );

                    foreach (var score in scores)
                    {
                        // just skip if beatmap doesn't exist to match query join behaviour.
                        if (!playmodeLookup.ContainsKey(score.beatmap_id))
                            continue;

                        score.country_code ??= "XX";
                        score.playmode = playmodeLookup[score.beatmap_id];

                        Console.WriteLine(score.convert.ToString());

                        if (Verbose)
                            Console.WriteLine($"Pushing {score}");

                        Processor.PushToQueue(new ScoreItem { Score = score });
                    }

                    last = scores.LastOrDefault();

                    if (!Verbose)
                        Console.WriteLine($"Pushed {last}");

                    if (Delay > 0)
                        Thread.Sleep(Delay);
                }
            }

            return last?.id;
        }
    }
}
